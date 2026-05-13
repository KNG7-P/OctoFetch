using System;
using System.Net;

namespace OctoFetch.Services
{
    public sealed class DynamicMitmProxy : IWebProxy
    {
        private readonly IMitmService _mitm;
        public ICredentials? Credentials { get; set; }

        public DynamicMitmProxy(IMitmService mitm)
        {
            _mitm = mitm;
        }

        public Uri? GetProxy(Uri destination)
        {
            if (!_mitm.IsRunning) return null;
            if (destination.IsLoopback) return null;
            return new Uri(_mitm.ProxyUrl);
        }

        public bool IsBypassed(Uri host)
        {
            if (!_mitm.IsRunning) return true;
            return host.IsLoopback;
        }
    }
}
