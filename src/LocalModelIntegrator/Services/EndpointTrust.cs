using System;
using System.Net;

namespace LocalModelIntegrator.Services
{
    /// <summary>
    /// Where the configured model endpoint sends its traffic, reduced to what the user cares about:
    /// the local machine (loopback - silent, no disclosure), or somewhere off the box. The only
    /// further distinction is whether that off-box connection is encrypted (https) or not (http).
    /// No DNS lookups and no LAN-vs-internet guessing - anything that is not loopback is "external".
    /// </summary>
    public enum EndpointLevel { Local, ExternalSecure, ExternalCleartext }

    public sealed class EndpointTrust
    {
        public string Host { get; }
        public string Scheme { get; }
        public bool IsHttp { get; }
        public EndpointLevel Level { get; }

        private EndpointTrust(EndpointLevel level, bool isHttp, string host, string scheme)
        {
            Level = level;
            IsHttp = isHttp;
            Host = host;
            Scheme = scheme;
        }

        /// <summary>True when the endpoint is off this machine - acknowledged once (whether the
        /// connection is encrypted/yellow or not/red), then never prompted again.</summary>
        public bool NeedsAck => Level != EndpointLevel.Local;

        public string Summary
        {
            get
            {
                switch (Level)
                {
                    case EndpointLevel.Local:
                        return $"Endpoint: {Host} — on this machine.";
                    case EndpointLevel.ExternalSecure:
                        return $"Endpoint: {Host} — external, encrypted (https).";
                    default:
                        return $"Endpoint: {Host} — external, UNENCRYPTED (http).";
                }
            }
        }

        public static EndpointTrust Classify(string apiUrl)
        {
            if (string.IsNullOrWhiteSpace(apiUrl) ||
                !Uri.TryCreate(apiUrl, UriKind.Absolute, out Uri uri))
                return null;

            bool isHttp = string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase);
            EndpointLevel level = IsLoopback(uri.Host)
                ? EndpointLevel.Local
                : (isHttp ? EndpointLevel.ExternalCleartext : EndpointLevel.ExternalSecure);
            return new EndpointTrust(level, isHttp, uri.Host, uri.Scheme);
        }

        // "localhost of any sort": the localhost name, or a loopback IP (127.0.0.0/8, ::1).
        private static bool IsLoopback(string host)
        {
            if (string.IsNullOrEmpty(host))
                return false;
            string h = host.Trim('[', ']');
            if (string.Equals(h, "localhost", StringComparison.OrdinalIgnoreCase) ||
                h.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
                return true;
            return IPAddress.TryParse(h, out IPAddress ip) && IPAddress.IsLoopback(ip);
        }
    }
}
