using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Xml.Linq;
using log4net;
using log4net.Appender;
using log4net.Config;
using log4net.Core;

namespace AFR.ShoutcastBridge
{
    class Program
    {
        private static ILog _log;
        static int Main(string[] args)
        {
            var configFile = "Config.xml";

            // Arguments
            if (args.Any())
            {
                configFile = args[0];
            }

            // Initialize logging
            BasicConfigurator.Configure(new ConsoleAppender()
            {
                Layout = new log4net.Layout.PatternLayout(@"[%logger] %level: %message%newline"),
                Threshold = Level.Debug
            });
            _log = LogManager.GetLogger("bridgeapp");

            // Configuration setup
            XDocument xdoc;
            if (!File.Exists(configFile))
            {
                _log.Error("Invalid configuration file given (file not found).");
                return -2;
            }
            try
            {
                xdoc = XDocument.Load(configFile);
                if (xdoc.Root == null)
                {
                    _log.Error("Invalid configuration file given (could not parse file, no XML root found).");
                    return -2;
                }
            }
            catch
            {
                _log.Error("Invalid configuration file given (could not parse file, any typos in the XML code?).");
                return -2;
            }

            // Get ip address to work with
            var ip = IPAddress.Any;
            var ipElements = xdoc.Root.Elements("ip").ToArray();
            if (ipElements.Any())
            {
                if (!IPAddress.TryParse(ipElements.First().Value, out ip))
                {
                    _log.Error("Invalid IP address in configuration, please check the configuration file.");
                    return -1;
                }
                if (ipElements.Count() > 1)
                    _log.Warn("Multiple ip addresses defined, using first one. Please check the configuration file as soon as possible and fix this.");
            }

            // Get port to work with
            var port = (ushort)8000;
            var portElements = xdoc.Root.Elements("port").ToArray();
            if (portElements.Any())
            {
                if (!ushort.TryParse(portElements.First().Value, out port))
                {
                    _log.Error("Invalid port in configuration, please check the configuration file.");
                    return -1;
                }
                if (portElements.Count() > 1)
                    _log.Warn("Multiple ports defined, using first one. Please check the configuration file as soon as possible and fix this.");
            }

            var sb = new ShoutcastBridgeServer(ip, port);

            // Mountpoint configuration
            var mpElements = xdoc.Root.Descendants("stream").ToArray();
            if (!mpElements.Any())
            {
                _log.Error("No mountpoints/streams configured. Please define at least one in the configuration file.");
                return -1;
            }
            foreach (var mpElement in mpElements)
            {
                var pwAttr = mpElement.Attribute("password");
                string pw;
                if (pwAttr == null || string.IsNullOrEmpty(pw = pwAttr.Value))
                {
                    _log.Error("Mountpoint without password defined. All mountpoints need a unique password.");
                    return -1;
                }
                if (mpElements.Count(m => m.Attribute("password").Value == pwAttr.Value) > 1)
                {
                    _log.ErrorFormat("Too many mountpoints with same password ({0}) defined. All mountpoints need a unique password.", pwAttr.Value);
                    return -1;
                }

                ushort parsedPort;
                var mpPortElements = mpElement.Elements("port").ToArray();
                if (!mpPortElements.Any())
                {
                    //mpElement.Add(new XElement("port", 8000));
                    //mpPortElements = mpElement.Elements("port").ToArray();
                    parsedPort = 8000;
                }
                else if (mpPortElements.Count() > 1)
                {
                    _log.WarnFormat("Too many icecast ports defined for mountpoint pw={0}, please only define one.", pw);
                    return -1;
                }
                else if (!ushort.TryParse(mpPortElements.Single().Value, out parsedPort))
                {
                    _log.ErrorFormat("Invalid icecast port defined for mountpoint pw={0}, please define a proper port number.", pw);
                    return -1;
                }

                var mpHostElements = mpElement.Elements("host").ToArray();
                if (!mpHostElements.Any())
                {
                    mpElement.Add(new XElement("host", IPAddress.Loopback.ToString()));
                    mpHostElements = mpElement.Elements("host").ToArray();
                }
                else if (mpHostElements.Count() > 1)
                {
                    _log.WarnFormat("Too many icecast host names defined for mountpoint pw={0}, please only define one.", pw);
                    return -1;
                }

                var mpMountpointElements = mpElement.Elements("mountpoint").ToArray();
                if (!mpMountpointElements.Any())
                {
                    mpElement.Add(new XElement("mountpoint", "/stream"));
                    mpMountpointElements = mpElement.Elements("mountpoint").ToArray(); // yes, this is rude.
                }
                else if (mpMountpointElements.Count() > 1)
                {
                    _log.WarnFormat(
                        "Too many icecast mountpoint names defined for mountpoint pw={0}, please only define one.", pw);
                    return -1;
                }

                var mpPasswordElements = mpElement.Elements("password").ToArray();
                if (!mpPasswordElements.Any())
                {
                    mpElement.Add(new XElement("password", "hackme"));
                    mpPasswordElements = mpElement.Elements("password").ToArray();
                }
                else if (mpPasswordElements.Count() > 1)
                {
                    _log.WarnFormat("Too many icecast passwords defined for mountpoint pw={0}, please only define one.", pw);
                    return -1;
                }

                var mpUsernameElements = mpElement.Elements("username").ToArray();
                if (!mpUsernameElements.Any())
                {
                    mpElement.Add(new XElement("username", "source"));
                    mpUsernameElements = mpElement.Elements("username").ToArray();
                }
                else if (mpUsernameElements.Count() > 1)
                {
                    _log.WarnFormat("Too many icecast usernames defined for mountpoint pw={0}, please only define one.", pw);
                    return -1;
                }
                
                sb.AddMountpoint(pw, new ShoutcastBridgeMountpoint()
                {
                    IcecastHost = mpHostElements.Single().Value,
                    IcecastPort = parsedPort,
                    IcecastMountpoint = mpMountpointElements.Single().Value,
                    IcecastUsername = mpUsernameElements.Single().Value,
                    IcecastPassword = mpPasswordElements.Single().Value
                });
            }

            _log.DebugFormat("Starting up Shoutcast bridge now.");
            sb.Start();

            return 0; // We'll never get here anyways... except on errors `-`
        }
    }
}
