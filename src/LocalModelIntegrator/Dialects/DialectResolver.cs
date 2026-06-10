using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LocalModelIntegrator.Options;
using LocalModelIntegrator.Services;

namespace LocalModelIntegrator.Dialects
{
    /// <summary>Which dialect to speak and the exact endpoint to speak it to.</summary>
    public sealed class DialectResolution
    {
        public IModelDialect Dialect { get; }
        public string Endpoint { get; }

        /// <summary>Human-readable reason, surfaced in the /test report and the diag log.</summary>
        public string Evidence { get; }

        /// <summary>True when backed by an actual server response (cacheable); false for
        /// URL-shape guesses made while the server was unreachable (retried next call).</summary>
        public bool Confirmed { get; }

        public DialectResolution(IModelDialect dialect, string endpoint, string evidence, bool confirmed)
        {
            Dialect = dialect;
            Endpoint = endpoint;
            Evidence = evidence;
            Confirmed = confirmed;
        }
    }

    /// <summary>
    /// Resolves the configured options to a (dialect, endpoint) pair. A manually-selected
    /// dialect is returned as-is with the configured URL. With "Auto (detect)" the server is
    /// identified by cheap markers - the user can paste a bare base URL (http://localhost:11434)
    /// and the right endpoint path is derived:
    ///   1. A URL already ending in a dialect-specific path (/api/chat) is conclusive by itself.
    ///   2. GET {base}/api/version - answered (unauthenticated) by every Ollama install. An
    ///      Ollama server is spoken to NATIVELY even if the configured URL was its
    ///      OpenAI-compatible endpoint, because native is strictly better (num_ctx).
    ///   3. Any other live server: OpenAI chat at the configured path, or {base}/v1/chat/completions
    ///      when only a base was given (200/401/403 from {base}/v1/models counts as alive).
    /// Confirmed detections are cached per (url) until the configuration changes; unreachable
    /// servers yield an uncached URL-shape fallback so detection retries once the server is up
    /// (same lesson as the capability-probe failure caching).
    /// </summary>
    public static class DialectResolver
    {
        /// <summary>The options "Protocol" value meaning "detect the dialect".</summary>
        public const string AutoId = "Auto (detect)";

        private const int MarkerTimeoutMs = 4000;

        private static readonly HttpClient _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        private static readonly object _gate = new object();
        private static string _cachedKey;
        private static DialectResolution _cached;

        public static async Task<DialectResolution> ResolveAsync(GeneralOptions options, CancellationToken ct)
        {
            // Manual selection: the user's word is final - exact dialect, exact URL.
            if (!string.Equals(options.Protocol, AutoId, StringComparison.OrdinalIgnoreCase))
                return new DialectResolution(ModelDialectRegistry.Get(options.Protocol), options.ApiUrl,
                    "configured manually", confirmed: true);

            string key = options.ApiUrl ?? string.Empty;
            lock (_gate)
            {
                if (_cached != null && string.Equals(_cachedKey, key, StringComparison.Ordinal))
                    return _cached;
            }

            DialectResolution resolution = await DetectAsync(options, ct).ConfigureAwait(false);

            if (options.EnableLogging)
                DiagLog.Write("dialect", $"auto-detect: {resolution.Dialect.Id} at {resolution.Endpoint} ({resolution.Evidence})");

            if (resolution.Confirmed)
            {
                lock (_gate)
                {
                    _cachedKey = key;
                    _cached = resolution;
                }
            }
            return resolution;
        }

        private static async Task<DialectResolution> DetectAsync(GeneralOptions options, CancellationToken ct)
        {
            if (!Uri.TryCreate(options.ApiUrl, UriKind.Absolute, out Uri uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                // Invalid URL: hand back the default dialect; the transport's own validation
                // produces the user-facing error.
                return new DialectResolution(ModelDialectRegistry.Get(ModelDialectRegistry.OpenAIChatId),
                    options.ApiUrl, "invalid URL - not detected", confirmed: false);
            }

            string baseUrl = uri.GetLeftPart(UriPartial.Authority);
            string path = uri.AbsolutePath.TrimEnd('/');

            // 1. An explicit dialect-specific path is conclusive without touching the network.
            if (path.EndsWith("/api/chat", StringComparison.OrdinalIgnoreCase))
                return new DialectResolution(ModelDialectRegistry.Get(ModelDialectRegistry.OllamaNativeId),
                    options.ApiUrl, "URL is an Ollama native endpoint (/api/chat)", confirmed: true);

            // 2. Ollama marker. Even when the configured URL is Ollama's OpenAI-compatible
            //    endpoint, prefer native - it is the same server with per-request num_ctx.
            (int versionStatus, string versionBody) =
                await MarkerGetAsync(baseUrl + "/api/version", null, ct).ConfigureAwait(false);
            if (versionStatus == 200 && versionBody != null && versionBody.Contains("\"version\""))
                return new DialectResolution(ModelDialectRegistry.Get(ModelDialectRegistry.OllamaNativeId),
                    baseUrl + "/api/chat", "Ollama server detected (/api/version)", confirmed: true);

            bool serverAlive = versionStatus != 0; // any HTTP status means something answered

            // 3. The configured URL already names an OpenAI chat endpoint.
            if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                return new DialectResolution(ModelDialectRegistry.Get(ModelDialectRegistry.OpenAIChatId),
                    options.ApiUrl,
                    serverAlive ? "OpenAI-compatible endpoint (server is not Ollama)" : "URL shape (/chat/completions); server unreachable",
                    confirmed: serverAlive);

            // 4. Base (or unknown) URL: an OpenAI-style server answers /v1/models with a list,
            //    or with an auth wall when a key is required.
            (int modelsStatus, _) =
                await MarkerGetAsync(baseUrl + "/v1/models", options.ApiKey, ct).ConfigureAwait(false);
            if (modelsStatus == 200 || modelsStatus == 401 || modelsStatus == 403)
                return new DialectResolution(ModelDialectRegistry.Get(ModelDialectRegistry.OpenAIChatId),
                    baseUrl + "/v1/chat/completions",
                    $"OpenAI-compatible server detected (/v1/models HTTP {modelsStatus})", confirmed: true);

            // 5. No evidence (server down or exotic): best-effort OpenAI chat guess, uncached so
            //    a later call re-detects once the server responds.
            string endpoint = path.Length == 0 ? baseUrl + "/v1/chat/completions" : options.ApiUrl;
            return new DialectResolution(ModelDialectRegistry.Get(ModelDialectRegistry.OpenAIChatId),
                endpoint, "no server evidence; defaulting to OpenAI chat", confirmed: false);
        }

        // A short, failure-tolerant GET used only for server identification. Returns status 0
        // when nothing answered. User cancellation propagates.
        private static async Task<(int status, string body)> MarkerGetAsync(string url, string apiKey, CancellationToken ct)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrWhiteSpace(apiKey))
                    request.Headers.Add("Authorization", "Bearer " + apiKey);

                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(MarkerTimeoutMs);
                    using (HttpResponseMessage response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false))
                    {
                        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        return ((int)response.StatusCode, body);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return (0, null);
            }
        }
    }
}
