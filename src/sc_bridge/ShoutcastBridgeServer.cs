using log4net;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using uhttpsharp;
using uhttpsharp.Listeners;
using uhttpsharp.RequestProviders;

namespace AFR.ShoutcastBridge
{
    public class ShoutcastBridgeServer
    {
        private readonly ILog _log;
        private readonly ILog _adminlog;
        private readonly ILog _sourcelog;

        private TcpListener _sourceServer;

        private HttpServer _adminServer;

        private readonly IPAddress _ip;

        private readonly ushort _port;

        private readonly Dictionary<string, ShoutcastBridgeMountpoint> _mountpoints =
            new Dictionary<string, ShoutcastBridgeMountpoint>();

        private readonly Dictionary<string, Tuple<ShoutcastReadingStream, IcecastWriter>> _connectedMountpoints =
            new Dictionary<string, Tuple<ShoutcastReadingStream, IcecastWriter>>();

        public ShoutcastBridgeServer(IPAddress ip, ushort port)
        {
            _ip = ip;
            _port = port;
            _log = LogManager.GetLogger("ShoutcastBridge");
            _adminlog = LogManager.GetLogger("Shout1Admin");
            _sourcelog = LogManager.GetLogger("Shout1Source");
        }

        public void Start()
        {
            _log.DebugFormat("Starting SCv1 source server on port {0}...", _port + 1);
            _sourceServer = new TcpListener(_ip, _port + 1);
            try
            {
                _sourceServer.Start();
            }
            catch (Exception error)
            {
                _log.ErrorFormat("Could not start up source server. {0}", error.Message);
            }

            _log.DebugFormat("Starting SC admin HTTP server on port {0}...", _port);
            _adminServer = new HttpServer(new HttpRequestProvider());
            try
            {
                _adminServer.Use(new TcpListenerAdapter(new TcpListener(_ip, _port)));
            }
            catch (Exception error)
            {
                _log.ErrorFormat("Could not start up admin HTTP server. {0}", error.Message);
            }
            _adminServer.Use(new AdminHandler(this));
            try
            {
                _adminServer.Start();
            }
            catch (Exception error)
            {
                _log.ErrorFormat("Could not start up admin HTTP server. {0}", error.Message);
            }

            _log.Info("Shoutcast bridge started up successfully, now ready.");

            while (true)
            {
                Socket sock;
                try
                {
                    sock = _sourceServer.AcceptSocket();
                }
                catch
                {
                    continue;
                }

                _log.Debug("Accepting incoming connection");
                var ns = new NetworkStream(sock, true);
                var srs = new ShoutcastReadingStream((IPEndPoint)sock.RemoteEndPoint, ns);
                srs.Authenticating += password =>
                {
                    if (!_mountpoints.ContainsKey(password))
                    {
                        _sourcelog.DebugFormat("[{0}] Connection declined: Invalid password", srs.ClientEndPoint);
                        return "Invalid password";
                    }

                    if (_connectedMountpoints.ContainsKey(password))
                    {
                        _sourcelog.DebugFormat("[{0}] Connection declined: Stream already in use", srs.ClientEndPoint);
                        return "Stream already in use";
                    }

                    var mp = _mountpoints[password];

                    var writer = new IcecastWriter()
                    {
                        Hostname = mp.IcecastHost,
                        Port = mp.IcecastPort,
                        Mountpoint = mp.IcecastMountpoint,
                        Username = string.IsNullOrEmpty(mp.IcecastUsername) ? "source" : mp.IcecastUsername,
                        Password = mp.IcecastPassword,
                        ContentType = "undefined" /* will be defined on first packet */
                    };

                    _connectedMountpoints.Add(password, new Tuple<ShoutcastReadingStream, IcecastWriter>(srs, writer));


                    _sourcelog.DebugFormat("[{0}] Testing connection to master server...", srs.ClientEndPoint);
                    if (!writer.Open())
                    {
                        _sourcelog.DebugFormat("[{0}] Connection declined: Master server was unavailable", srs.ClientEndPoint);
                        return "Master server unavailable, try again later.";
                    }
                    writer.Close();

                    _sourcelog.DebugFormat("[{0}] Connection accepted", srs.ClientEndPoint);
                    return "OK2";
                };
                srs.Disconnected += () =>
                {
                    _sourcelog.DebugFormat("[{0}] Disconnected", srs.ClientEndPoint);
                    var pws = _connectedMountpoints.Where(s => s.Value.Item1 == srs).Select(s => s.Key).ToArray();
                    if (!pws.Any())
                    {
                        _sourcelog.ErrorFormat("[{0}] Could not find any connected mountpoints associated with this IP, dangling connection! This is a bug!", srs.ClientEndPoint);
                    }
                    foreach (var pw in pws.ToArray())
                    {
                        _connectedMountpoints[pw].Item2.Close(); // close Icecast connection
                        _connectedMountpoints.Remove(pw); // remove from registered connections
                    }
                };
                srs.ReceivedData += data =>
                {
                    var conns = _connectedMountpoints.Where(s => s.Value.Item1 == srs).ToArray();
                    if (!conns.Any())
                    {
                        _sourcelog.ErrorFormat("Dangling source connection from {0} - nothing bound to it!", srs.ClientEndPoint);
                    }
                    foreach (var conn in conns)
                    {
                        var icecast = conn.Value.Item2;
                        var shoutcast = conn.Value.Item1;

                        if (!icecast.Connected)
                        {
                            _sourcelog.InfoFormat("[{0}] Connecting to {1}:{2}{3}...", srs.ClientEndPoint, icecast.Hostname, icecast.Port, icecast.Mountpoint);
                            icecast.Name = icecast.Description = shoutcast.StreamName;
                            icecast.Genre = shoutcast.StreamGenre;
                            icecast.KBitrate = shoutcast.StreamKBitrate;
                            icecast.Url = shoutcast.StreamUrl;
                            icecast.Public = shoutcast.StreamPublic;
                            if (icecast.ContentType == "undefined")
                                switch (BitConverter.ToString(data.Take(2).ToArray()).Replace("-", "").ToLower())
                                {
                                    case "4f67": // OGG container
                                        _sourcelog.DebugFormat("[{0}] Detected OGG audio container", srs.ClientEndPoint);
                                        icecast.ContentType = "audio/ogg";
                                        break;
                                    case "fff9": // AAC
                                        _sourcelog.DebugFormat("[{0}] Detected AAC-LC data", srs.ClientEndPoint);
                                        icecast.ContentType = "audio/aac";
                                        break;
                                    default:
                                        _sourcelog.DebugFormat("[{0}] Assuming MP3 codec", srs.ClientEndPoint);
                                        icecast.ContentType = "audio/mpeg";
                                        break;
                                }
                            if (!icecast.Open())
                            {
                                _sourcelog.ErrorFormat("[{0}] Could not connect with Icecast, retrying on next packet", srs.ClientEndPoint);
                                continue;
                            }
                            _sourcelog.InfoFormat("[{0}] Connected!", srs.ClientEndPoint);
                            // TODO: Sync metadata after connection immediately!
                        }

                        icecast.Push(data);
                    }
                };
                srs.Start();
            }
        }

        public void Stop()
        {
            _adminServer.Dispose();
            _sourceServer.Stop();
        }

        public void AddMountpoint(string password, ShoutcastBridgeMountpoint mp)
        {
            _log.DebugFormat("Adding mountpoint: {0}:{1}, mountpoint \"{2}\"", mp.IcecastHost, mp.IcecastPort, mp.IcecastMountpoint);
            _mountpoints.Add(password, mp);
        }

        public bool UpdateMetadata(IPEndPoint remoteEndPoint, string password, string song)
        {
            // Is stream connected?
            if (!_connectedMountpoints.ContainsKey(password))
            {
                _adminlog.DebugFormat("[{0}] Metadata update declined: Invalid password", remoteEndPoint);
                return false;
            }
            
            // Authorize client - requirement: same IP
            if (!_connectedMountpoints[password].Item1.ClientEndPoint.Address.Equals(remoteEndPoint.Address))
            {
                _adminlog.DebugFormat("[{0}] Metadata update declined: IP mismatch", remoteEndPoint);
                return false;
            }

            // Finally update metadata
            _adminlog.DebugFormat("[{0}] Metadata update: {1}", remoteEndPoint, song);
            return _connectedMountpoints[password].Item2.SendMetadata(song);
        }
    }
}
