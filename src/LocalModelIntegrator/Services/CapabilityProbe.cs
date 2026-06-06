using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LocalModelIntegrator.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LocalModelIntegrator.Services
{
    /// <summary>
    /// Interrogates an OpenAI-compatible endpoint with a few tiny requests to learn what it supports:
    /// reachability/auth, streaming, a reasoning channel, native tool-calling, and (best-effort) the
    /// model list. Used by the "Test Connection" button and lazily before the agent's first run.
    /// All calls are short and best-effort; nothing here throws to the caller.
    /// </summary>
    public static class CapabilityProbe
    {
        private static readonly HttpClient _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        private const int ProbeTimeoutMs = 25000;

        public static async Task<ModelCapabilities> ProbeAsync(GeneralOptions options, CancellationToken ct)
        {
            var caps = new ModelCapabilities
            {
                ProbedUrl = options.ApiUrl,
                ProbedModel = options.ModelName,
                ProbedProtocol = options.Protocol
            };

            if (string.IsNullOrWhiteSpace(options.ApiUrl) ||
                !Uri.TryCreate(options.ApiUrl, UriKind.Absolute, out Uri uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                caps.Error = "Invalid or empty API URL.";
                caps.Report = "✗ " + caps.Error;
                return caps;
            }

            Log(options, $"probing {options.ApiUrl} [{options.Protocol} / {options.ModelName}]");

            await BasicChatAsync(options, caps, ct).ConfigureAwait(false);

            if (caps.Chat)
            {
                await StreamCheckAsync(options, caps, ct).ConfigureAwait(false);
                await ToolsCheckAsync(options, caps, ct).ConfigureAwait(false);
                await ModelsAsync(options, caps, ct).ConfigureAwait(false);
            }

            caps.Report = BuildReport(caps, options.ModelName);
            Log(options, "result:\n" + caps.Report);
            return caps;
        }

        // ---- individual probes ------------------------------------------------------------------

        private static async Task BasicChatAsync(GeneralOptions options, ModelCapabilities caps, CancellationToken ct)
        {
            string body = JsonConvert.SerializeObject(new
            {
                model = options.ModelName,
                messages = new[] { new { role = "user", content = "Reply with the single word: ready" } },
                max_tokens = 16,
                temperature = 0.0
            });

            (int status, string respBody, long ms, Exception ex) = await PostAsync(options, body, stream: false, ct).ConfigureAwait(false);
            caps.LatencyMs = ms;

            if (ex != null)
            {
                caps.Reachable = false;
                caps.Error = FriendlyError(ex);
                Log(options, "basic chat: unreachable - " + ex.Message);
                return;
            }

            caps.Reachable = true;
            caps.StatusCode = status;
            caps.AuthOk = status != 401 && status != 403;
            caps.Chat = status >= 200 && status < 300;
            Log(options, $"basic chat: HTTP {status} in {ms} ms");

            if (!caps.AuthOk)
            {
                caps.Error = $"Authentication failed (HTTP {status}). Check the API key.";
                return;
            }
            if (!caps.Chat)
            {
                caps.Error = $"Server returned HTTP {status}.";
                return;
            }

            // Inspect the response shape for a reasoning channel.
            try
            {
                JToken msg = JObject.Parse(respBody)["choices"]?[0]?["message"];
                string content = msg?["content"]?.ToString();
                string reasoning = msg?["reasoning_content"]?.ToString() ?? msg?["reasoning"]?.ToString();
                bool inlineThink = content != null && content.IndexOf("<think>", StringComparison.OrdinalIgnoreCase) >= 0;
                caps.Reasoning = (!string.IsNullOrEmpty(reasoning) || inlineThink) ? Tristate.Yes : Tristate.No;
            }
            catch
            {
                caps.Reasoning = Tristate.Unknown;
            }
        }

        private static async Task StreamCheckAsync(GeneralOptions options, ModelCapabilities caps, CancellationToken ct)
        {
            string body = JsonConvert.SerializeObject(new
            {
                model = options.ModelName,
                messages = new[] { new { role = "user", content = "Reply with the single word: ready" } },
                max_tokens = 16,
                temperature = 0.0,
                stream = true
            });

            (int status, string respBody, long _, Exception ex) = await PostAsync(options, body, stream: true, ct).ConfigureAwait(false);
            if (ex != null || status < 200 || status >= 300)
            {
                caps.Streaming = Tristate.Unknown;
                return;
            }
            // SSE responses are line-oriented "data: {...}" frames; a JSON object body means no streaming.
            caps.Streaming = (respBody != null && respBody.IndexOf("data:", StringComparison.Ordinal) >= 0)
                ? Tristate.Yes
                : Tristate.No;
            Log(options, "streaming: " + caps.Streaming);
        }

        private static async Task ToolsCheckAsync(GeneralOptions options, ModelCapabilities caps, CancellationToken ct)
        {
            string body = JsonConvert.SerializeObject(new
            {
                model = options.ModelName,
                messages = new[] { new { role = "user", content = "Call the ping tool now." } },
                max_tokens = 64,
                temperature = 0.0,
                tool_choice = "auto",
                tools = new[]
                {
                    new
                    {
                        type = "function",
                        function = new
                        {
                            name = "ping",
                            description = "Returns pong. Call this when the user asks to ping.",
                            parameters = new { type = "object", properties = new { } }
                        }
                    }
                }
            });

            (int status, string respBody, long _, Exception ex) = await PostAsync(options, body, stream: false, ct).ConfigureAwait(false);
            if (ex != null)
            {
                caps.NativeTools = Tristate.Unknown;
            }
            else if (status == 400 || status == 422 || status == 404 || status == 501)
            {
                caps.NativeTools = Tristate.No;   // the `tools` parameter was rejected outright
            }
            else if (status >= 200 && status < 300)
            {
                try
                {
                    JToken toolCalls = JObject.Parse(respBody)["choices"]?[0]?["message"]?["tool_calls"];
                    // accepted + actually called -> yes; accepted but didn't call -> can't be sure, try at runtime
                    caps.NativeTools = (toolCalls is JArray arr && arr.Count > 0) ? Tristate.Yes : Tristate.Maybe;
                }
                catch
                {
                    caps.NativeTools = Tristate.Maybe;
                }
            }
            else
            {
                caps.NativeTools = Tristate.Unknown;
            }
            Log(options, $"native tools: {caps.NativeTools} (HTTP {status})");
        }

        private static async Task ModelsAsync(GeneralOptions options, ModelCapabilities caps, CancellationToken ct)
        {
            string modelsUrl = DeriveModelsUrl(options.ApiUrl);
            if (modelsUrl == null)
                return;

            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
                if (!string.IsNullOrWhiteSpace(options.ApiKey))
                    req.Headers.Add("Authorization", "Bearer " + options.ApiKey);

                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(8000);
                    using (HttpResponseMessage resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode)
                            return;
                        string b = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (JObject.Parse(b)["data"] is JArray arr)
                        {
                            foreach (JToken m in arr)
                            {
                                string id = m["id"]?.ToString();
                                if (!string.IsNullOrEmpty(id))
                                    caps.Models.Add(id);
                            }
                        }
                    }
                }
            }
            catch
            {
                // model listing is best-effort; many servers don't implement /v1/models
            }
        }

        // ---- transport --------------------------------------------------------------------------

        private static async Task<(int status, string body, long ms, Exception ex)> PostAsync(
            GeneralOptions options, string json, bool stream, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Post, options.ApiUrl)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                if (!string.IsNullOrWhiteSpace(options.ApiKey))
                    req.Headers.Add("Authorization", "Bearer " + options.ApiKey);

                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(ProbeTimeoutMs);

                    HttpCompletionOption mode = stream
                        ? HttpCompletionOption.ResponseHeadersRead
                        : HttpCompletionOption.ResponseContentRead;

                    using (HttpResponseMessage resp = await _http.SendAsync(req, mode, cts.Token).ConfigureAwait(false))
                    {
                        int status = (int)resp.StatusCode;
                        string body;
                        if (stream && resp.IsSuccessStatusCode)
                        {
                            // Read just enough to tell SSE frames from a plain JSON body.
                            using (Stream s = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            using (var r = new StreamReader(s))
                            {
                                var sb = new StringBuilder();
                                string line;
                                int n = 0;
                                while ((line = await r.ReadLineAsync().ConfigureAwait(false)) != null && n < 8)
                                {
                                    sb.AppendLine(line);
                                    n++;
                                    if (line.StartsWith("data:", StringComparison.Ordinal))
                                        break;
                                }
                                body = sb.ToString();
                            }
                        }
                        else
                        {
                            body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        }
                        sw.Stop();
                        return (status, body, sw.ElapsedMilliseconds, null);
                    }
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                return (0, null, sw.ElapsedMilliseconds, ex);
            }
        }

        private static string DeriveModelsUrl(string apiUrl)
        {
            int idx = apiUrl.IndexOf("/chat/completions", StringComparison.OrdinalIgnoreCase);
            return idx >= 0 ? apiUrl.Substring(0, idx) + "/models" : null;
        }

        private static string FriendlyError(Exception ex)
        {
            if (ex is TaskCanceledException || ex is OperationCanceledException)
                return "Timed out reaching the endpoint - is the server running and the URL correct?";
            if (ex is HttpRequestException)
                return "Could not connect - " + ex.Message;
            return ex.Message;
        }

        private static string BuildReport(ModelCapabilities c, string model)
        {
            if (!c.Reachable)
                return "✗ Could not reach the endpoint.\n" + (c.Error ?? "");
            if (!c.AuthOk)
                return "✗ " + (c.Error ?? "Authentication failed.");
            if (!c.Chat)
                return "✗ " + (c.Error ?? $"Chat request failed (HTTP {c.StatusCode}).");

            var sb = new StringBuilder();
            sb.AppendLine($"✓ Connected to \"{model}\"  (~{c.LatencyMs} ms)");
            sb.AppendLine("  streaming:    " + Mark(c.Streaming));
            sb.AppendLine("  reasoning:    " + Mark(c.Reasoning));
            sb.AppendLine("  native tools: " + Mark(c.NativeTools));
            if (c.Models.Count > 0)
            {
                sb.AppendLine($"  models ({c.Models.Count}): " + string.Join(", ", c.Models.Take(8)) +
                              (c.Models.Count > 8 ? ", …" : ""));
                if (!c.Models.Contains(model))
                    sb.AppendLine($"  ⚠ configured model \"{model}\" is not in the server's list");
            }
            return sb.ToString().TrimEnd();
        }

        private static string Mark(Tristate t)
        {
            switch (t)
            {
                case Tristate.Yes: return "yes";
                case Tristate.No: return "no";
                case Tristate.Maybe: return "maybe (will try at runtime)";
                default: return "unknown";
            }
        }

        private static void Log(GeneralOptions options, string message)
        {
            if (options.EnableLogging)
                DiagLog.Write("probe", message);
        }
    }
}
