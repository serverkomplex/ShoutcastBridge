using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using log4net;

namespace AFR.ShoutcastBridge
{
    public class ShoutcastReadingStream
    {
        private readonly NetworkStream _ns;
        private readonly StreamReader _sr;
        private readonly StreamWriter _sw;
        private CancellationTokenSource _cancel;
        private Task _task;
        private readonly Dictionary<string, string> _headers = new Dictionary<string, string>();

        public string StreamName
        {
            get { return _headers.ContainsKey("icy-name") ? _headers["icy-name"] : string.Empty; }
        }

        public string StreamGenre
        {
            get { return _headers.ContainsKey("icy-genre") ? _headers["icy-genre"] : string.Empty; }
        }

        public bool StreamPublic
        {
            get { return _headers.ContainsKey("icy-pub") && int.Parse(_headers["icy-pub"]) >= 1; }
        }

        public ushort StreamKBitrate
        {
            get { return _headers.ContainsKey("icy-br") ? ushort.Parse(_headers["icy-br"]) : (ushort)0; }
        }

        public string StreamUrl
        {
            get { return _headers.ContainsKey("icy-url") ? _headers["icy-url"] : string.Empty; }
        }

        public string StreamIrc
        {
            get { return _headers.ContainsKey("icy-irc") ? _headers["icy-irc"] : string.Empty; }
        }

        public IPEndPoint ClientEndPoint { get; private set; }

        public ShoutcastReadingStream(IPEndPoint ep, NetworkStream ns)
        {
            ClientEndPoint = ep;
            _ns = ns;
            _task = null;
            _sr = new StreamReader(ns);
            _sw = new StreamWriter(ns) {AutoFlush = true};
        }

        public void Start()
        {
            _cancel = new CancellationTokenSource();
            _task = Task.Factory.StartNew(_start, _cancel.Token).ContinueWith(task =>
            {
                if (Disconnected != null)
                    Disconnected();
            });
        }

        private void _start()
        {
            var password = _sr.ReadLine();

            // Check if any password given
            if (password == null)
                return;

            // Check if password correct
            password = password.TrimEnd('\n', '\r');
            var status = Authenticating(password);
            if (Authenticating != null && status != "OK2")
            {
                _sw.WriteLine(status);
                _ns.Dispose();
                return;
            }

            _sw.WriteLine(status);

            // Headers
            var line = _sr.ReadLine();
            while (line != null && line.Trim().Any())
            {
                var lineSplit = line.Split(':');
                if (lineSplit.Length < 2)
                    continue; // Invalid header line
                
                var name = lineSplit[0];
                var value = string.Join(":", lineSplit.Skip(1));
                
                if (_headers.ContainsKey(name))
                    _headers[name] = value;
                else
                    _headers.Add(name, value);

                line = _sr.ReadLine();
            }

            // Content
            var data = new byte[16 * 1024];
            try
            {
                int length;
                while ((length = _ns.Read(data, 0, data.Length)) > 0)
                {
                    _cancel.Token.ThrowIfCancellationRequested();
                    if (ReceivedData != null)
                        ReceivedData(data.Take(length).ToArray());
                }
            }
            catch
            {
                LogManager.GetLogger("ShoutRead").Warn("Exception in reading loop while reading. Interpreting as disconnection.");
            }
        }

        public void Stop()
        {
            if (_cancel == null || _task == null)
                return;

            _cancel.Cancel();
            _task.Wait();
            _task = null;
        }

        ~ShoutcastReadingStream()
        {
            _sr.Dispose();
            _sw.Dispose();
        }

        public event ShoutcastDisconnectEventHandler Disconnected;
        public event ShoutcastAuthenticationEventHandler Authenticating;
        public event ShoutcastDataEventHandler ReceivedData;
    }
}
