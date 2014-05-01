using System;
using System.Net;
using System.Threading.Tasks;
using uhttpsharp;
using uhttpsharp.Headers;

namespace AFR.ShoutcastBridge
{
    internal class AdminHandler : IHttpRequestHandler
    {
        private readonly ShoutcastBridgeServer _shoutcastBridge;

        public AdminHandler(ShoutcastBridgeServer bridgeServer)
        {
            _shoutcastBridge = bridgeServer;
        }

        public Task Handle(IHttpContext context, Func<Task> next)
        {
            string mode;
            context.Request.QueryString.TryGetByName("mode", out mode);

            switch (mode.ToLower())
            {
                case "updinfo":
                    string password;
                    string song;
                    context.Request.QueryString.TryGetByName("pass", out password);
                    context.Request.QueryString.TryGetByName("song", out song);

                    if (string.IsNullOrEmpty(password))
                        break;

                    context.Response = HttpResponse.CreateWithMessage(HttpResponseCode.Ok, _shoutcastBridge.UpdateMetadata((IPEndPoint)context.RemoteEndPoint, password, song)
                        ? "OK"
                        : "Could not update metadata",
                        context.Request.Headers.KeepAliveConnection());
                    break;
                default:
                    context.Response = HttpResponse.CreateWithMessage(HttpResponseCode.Ok, "Unknown mode", context.Request.Headers.KeepAliveConnection());
                    break;
            }


            return Task.Factory.GetCompleted();
        }
    }
}