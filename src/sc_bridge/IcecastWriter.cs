using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web;
using log4net;

namespace AFR.ShoutcastBridge
{
    public class IcecastWriter
    {
        private NetworkStream _ns;
        private StreamReader _sr;
        private StreamWriter _sw;
        private TcpClient _tcp;

        public IcecastWriter()
        {
            Hostname = "127.0.0.1";
            Port = 8000;
            Username = "source";
            Password = "hackme";
            ContentType = "audio/mpeg";
            Description = "Livestream";
            Name = "Livestream";
            Genre = "Various";
            Public = false;
        }

        public string Name { get; set; }
        public string Genre { get; set; }
        public string Url { get; set; }
        public ushort KBitrate { get; set; }
        public bool Public { get; set; }
        public string Description { get; set; }
        public string ContentType { get; set; }
        public string Hostname { get; set; }
        public ushort Port { get; set; }
        public string Mountpoint { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }

        public bool Connected
        {
            get { return _ns != null && _sr != null && _sw != null && _tcp != null && _tcp.Connected; }
        }

        public bool Open()
        {
            try
            {
                _tcp = new TcpClient(Hostname, Port);
                _ns = _tcp.GetStream();
                _sr = new StreamReader(_ns);
                _sw = new StreamWriter(_ns) {AutoFlush = true};

                // Request headers
                _sw.WriteLine("SOURCE {0} ICE/1.0", Mountpoint);
                _sw.WriteLine("content-type: {0}", ContentType);
                _sw.WriteLine("Authorization: Basic {0}",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Format("{0}:{1}", Username, Password))));
                _sw.WriteLine("ice-name: {0}", Name);
                _sw.WriteLine("ice-url: {0}", Url);
                _sw.WriteLine("ice-genre: {0}", Genre);
                _sw.WriteLine("ice-bitrate: {0}", KBitrate);
                _sw.WriteLine("ice-private: {0}", Public ? 0 : 1);
                _sw.WriteLine("ice-public: {0}", Public ? 1 : 0);
                _sw.WriteLine("ice-description: {0}", Description);
                _sw.WriteLine("ice-audio-info: ice-bitrate={0}", KBitrate);
                _sw.WriteLine();

                // Authorized?
                string statusLine = _sr.ReadLine();
                if (statusLine == null)
                {
                    LogManager.GetLogger("IcecastWriter").ErrorFormat("Icecast socket error: No response");
                    return false;
                }
                string[] status = statusLine.Split(' ');

                if (status[1] == "200")
                    // Now we can stream
                    return true;

                // Something went wrong
                LogManager.GetLogger("IcecastWriter").ErrorFormat("Icecast HTTP error: {0} {1}", status[1], status[2]);
                Close();
                return false;
            }
            catch (Exception error)
            {
                LogManager.GetLogger("IcecastWriter").ErrorFormat("BUG IN ICECASTWRITING: {0}", error);
                // Something went wrong
                Close();
                return false;
            }
        }

        public void Push(byte[] data)
        {
            try
            {
                if (_ns == null || _sr == null || _sw == null || _tcp == null || !_tcp.Connected)
                    return;
                _sw.BaseStream.Write(data, 0, data.Length);
            }
            catch (Exception error)
            {
                LogManager.GetLogger("IcecastWriter").ErrorFormat("Icecast socket error while pushing data: {0}", error);
                Close();
            }
        }

        public bool SendMetadata(string song, bool tryOnce = false)
        {
            if (song == null)
                song = string.Empty;

            var reqQuery = new Dictionary<string, string>
            {
                //{"pass",Password},
                {"mode", "updinfo"},
                {"mount", Mountpoint},
                {"song", song}
            };

            var reqUriBuilder = new UriBuilder("http", Hostname, Port, "/admin/metadata")
            {
                Query =
                    string.Join("&",
                        reqQuery.Keys.Select(
                            key =>
                                string.Format("{0}={1}", HttpUtility.UrlEncode(key),
                                    HttpUtility.UrlEncode(reqQuery[key])))),
                //UserName = Username,
                //Password = Password
            };

            var req = (HttpWebRequest) WebRequest.Create(reqUriBuilder.Uri);
            req.UserAgent = string.Format("ShoutcastBridge/{0}", Assembly.GetExecutingAssembly().GetName().Version);
            req.Credentials = new NetworkCredential(Username, Password);

            try
            {
                req.GetResponse();
                return true;
            }
            catch (Exception error)
            {
                LogManager.GetLogger("IcecastWriter").WarnFormat("Uri: {0}", reqUriBuilder.Uri);
                LogManager.GetLogger("IcecastWriter").WarnFormat("Could not send metadata: {0}", error.Message);
                if (tryOnce)
                    return false;

                Thread.Sleep(1000);
                return SendMetadata(song, true);
            }
        }

        public void Close()
        {
            LogManager.GetLogger("IcecastWriter").Info("Disconnecting");
            if (_ns == null || _sr == null || _sw == null || _tcp == null || !_tcp.Connected)
                return;
            _sr.Dispose();
            _sw.Dispose();
            _ns.Dispose();
            _tcp.Close();
        }
    }
}