using System.Collections.Generic;

namespace LocalModelIntegrator.Services
{
    /// <summary>Three-valued result for a probed capability that can't always be determined for certain.</summary>
    public enum Tristate
    {
        Unknown,
        No,
        Maybe,
        Yes
    }

    /// <summary>
    /// Snapshot of what an endpoint/model can do, established by <see cref="CapabilityProbe"/>.
    /// Structural axes only - behavioral edge cases (e.g. empty-content under load) are still handled
    /// at runtime by the tolerant parser/fallback, so this is a fast-start hint, not gospel.
    /// </summary>
    public sealed class ModelCapabilities
    {
        public bool Reachable { get; set; }
        public bool AuthOk { get; set; }
        public bool Chat { get; set; }            // a basic chat request returned a usable response
        public int StatusCode { get; set; }
        public long LatencyMs { get; set; }

        public Tristate Streaming { get; set; } = Tristate.Unknown;
        public Tristate Reasoning { get; set; } = Tristate.Unknown;   // exposes a reasoning channel / <think>
        public Tristate NativeTools { get; set; } = Tristate.Unknown; // accepts `tools` and returns tool_calls

        public List<string> Models { get; } = new List<string>();

        public string Error { get; set; }
        public string Report { get; set; }        // human-readable multi-line summary

        // How the endpoint was actually spoken to (manual selection or auto-detection).
        public string DialectId { get; set; }
        public string DialectEvidence { get; set; }
        public string ResolvedEndpoint { get; set; }

        // What this snapshot was taken against (for session caching / staleness checks).
        public string ProbedUrl { get; set; }
        public string ProbedModel { get; set; }
        public string ProbedProtocol { get; set; }

        public bool MatchesConfig(string url, string model, string protocol) =>
            string.Equals(ProbedUrl, url, System.StringComparison.OrdinalIgnoreCase) &&
            string.Equals(ProbedModel, model, System.StringComparison.Ordinal) &&
            string.Equals(ProbedProtocol, protocol, System.StringComparison.Ordinal);
    }
}
