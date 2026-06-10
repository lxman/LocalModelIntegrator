using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LocalModelIntegrator.Dialects;
using LocalModelIntegrator.Models;
using LocalModelIntegrator.Options;
using Newtonsoft.Json.Linq;

namespace LocalModelIntegrator.Services
{
    /// <summary>
    /// Interrogates an endpoint with a few tiny requests to learn what it supports:
    /// reachability/auth, streaming, a reasoning channel, native tool-calling, and (best-effort)
    /// the model list. The dialect to speak - and the exact endpoint - comes from
    /// <see cref="DialectResolver"/> first (auto-detected or manually configured), and every
    /// probe body/reply goes through that dialect, so the checks work for any wire format.
    /// Used by the "Test Connection" button and lazily before the agent's first run.
    /// All calls are short and best-effort; nothing here throws to the caller except the caller's
    /// own cancellation (so a user's Stop is reported as a cancel, not as an unreachable endpoint).
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

            // Pick the dialect (and the exact endpoint) before measuring anything else.
            DialectResolution route = await DialectResolver.ResolveAsync(options, ct).ConfigureAwait(false);
            caps.DialectId = route.Dialect.Id;
            caps.DialectEvidence = route.Evidence;
            caps.ResolvedEndpoint = route.Endpoint;

            Log(options, $"probing {route.Endpoint} as \"{route.Dialect.Id}\" ({route.Evidence}) [model {options.ModelName}]");

            await BasicChatAsync(options, route, caps, ct).ConfigureAwait(false);

            if (caps.Chat)
            {
                await StreamCheckAsync(options, route, caps, ct).ConfigureAwait(false);
                await ToolsCheckAsync(options, route, caps, ct).ConfigureAwait(false);
                await ModelsAsync(options, route, caps, ct).ConfigureAwait(false);
            }

            caps.Report = BuildReport(caps, options.ModelName);
            Log(options, "result:\n" + caps.Report);
            return caps;
        }

        // ---- individual probes ------------------------------------------------------------------

        private static DialectRequest ProbeRequest(GeneralOptions options, string userText, int maxTokens) =>
            new DialectRequest
            {
                Model = options.ModelName,
                Messages = new List<ChatMessage> { new ChatMessage("user", userText) },
                Temperature = 0.0,
                MaxTokens = maxTokens,
                Stream = false,
                // Keep num_ctx identical to real traffic: Ollama reloads the model when it changes.
                ContextWindowTokens = options.ContextWindowTokens
            };

        private static async Task BasicChatAsync(GeneralOptions options, DialectResolution route,
            ModelCapabilities caps, CancellationToken ct)
        {
            string body = route.Dialect.BuildRequestJson(ProbeRequest(options, "Reply with the single word: ready", 16));

            (int status, string respBody, long ms, Exception ex) = await PostAsync(route, options, body, ct).ConfigureAwait(false);
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

            // Inspect the reply (through the dialect) for a reasoning channel.
            try
            {
                LlmToolTurn turn = route.Dialect.ParseResponse(respBody);
                bool inlineThink = turn.Content != null &&
                    turn.Content.IndexOf("<think>", StringComparison.OrdinalIgnoreCase) >= 0;
                caps.Reasoning = (!string.IsNullOrEmpty(turn.Reasoning) || inlineThink)
                    ? Tristate.Yes
                    : Tristate.No;
            }
            catch
            {
                caps.Reasoning = Tristate.Unknown;
            }
        }

        private static async Task StreamCheckAsync(GeneralOptions options, DialectResolution route,
            ModelCapabilities caps, CancellationToken ct)
        {
            DialectRequest req = ProbeRequest(options, "Reply with the single word: ready", 16);
            req.Stream = true;
            string body = route.Dialect.BuildRequestJson(req);

            try
            {
                var httpRequest = new HttpRequestMessage(HttpMethod.Post, route.Endpoint)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                route.Dialect.ApplyAuth(httpRequest, options.ApiKey);

                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(ProbeTimeoutMs);
                    using (HttpResponseMessage response = await _http.SendAsync(
                        httpRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            caps.Streaming = Tristate.Unknown;
                        }
                        else
                        {
                            // A streaming reply yields a parseable frame within the first few
                            // lines; a single non-streamed JSON body yields none.
                            caps.Streaming = Tristate.No;
                            IDialectStreamParser frames = route.Dialect.CreateStreamParser();
                            using (Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            using (var reader = new StreamReader(stream))
                            {
                                string line;
                                int n = 0;
                                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null && n++ < 10)
                                {
                                    cts.Token.ThrowIfCancellationRequested();
                                    if (frames.ParseLine(line) != null || frames.Done)
                                    {
                                        caps.Streaming = Tristate.Yes;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                caps.Streaming = Tristate.Unknown;
            }
            Log(options, "streaming: " + caps.Streaming);
        }

        private static async Task ToolsCheckAsync(GeneralOptions options, DialectResolution route,
            ModelCapabilities caps, CancellationToken ct)
        {
            DialectRequest req = ProbeRequest(options, "Call the ping tool now.", 64);
            req.ToolsJson = PingToolJson;
            string body = route.Dialect.BuildRequestJson(req);

            (int status, string respBody, long _, Exception ex) = await PostAsync(route, options, body, ct).ConfigureAwait(false);
            if (ex != null)
            {
                caps.NativeTools = Tristate.Unknown;
            }
            else if (status == 400 || status == 422 || status == 404 || status == 501)
            {
                caps.NativeTools = Tristate.No;   // the tools parameter was rejected outright
            }
            else if (status >= 200 && status < 300)
            {
                try
                {
                    LlmToolTurn turn = route.Dialect.ParseResponse(respBody);
                    // accepted + actually called -> yes; accepted but didn't call -> can't be sure, try at runtime
                    caps.NativeTools = (turn.ToolCalls != null && turn.ToolCalls.Count > 0)
                        ? Tristate.Yes
                        : Tristate.Maybe;
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

        // OpenAI function-tool format - the ToolsJson contract; dialects convert if their server
        // wants another encoding.
        private static readonly string PingToolJson = new JArray
        {
            new JObject
            {
                ["type"] = "function",
                ["function"] = new JObject
                {
                    ["name"] = "ping",
                    ["description"] = "Returns pong. Call this when the user asks to ping.",
                    ["parameters"] = new JObject { ["type"] = "object", ["properties"] = new JObject() }
                }
            }
        }.ToString(Newtonsoft.Json.Formatting.None);

        private static async Task ModelsAsync(GeneralOptions options, DialectResolution route,
            ModelCapabilities caps, CancellationToken ct)
        {
            string modelsUrl = route.Dialect.DeriveModelsUrl(route.Endpoint);
            if (modelsUrl == null)
                return;

            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
                route.Dialect.ApplyAuth(req, options.ApiKey);

                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(8000);
                    using (HttpResponseMessage resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode)
                            return;
                        string b = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        caps.Models.AddRange(route.Dialect.ParseModelList(b));
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // model listing is best-effort; many servers don't implement it
            }
        }

        // ---- transport --------------------------------------------------------------------------

        private static async Task<(int status, string body, long ms, Exception ex)> PostAsync(
            DialectResolution route, GeneralOptions options, string json, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Post, route.Endpoint)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                route.Dialect.ApplyAuth(req, options.ApiKey);

                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(ProbeTimeoutMs);
                    using (HttpResponseMessage resp = await _http.SendAsync(
                        req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false))
                    {
                        int status = (int)resp.StatusCode;
                        string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        sw.Stop();
                        return (status, body, sw.ElapsedMilliseconds, null);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The CALLER canceled (user hit Stop) - propagate instead of reporting the endpoint
                // as unreachable. The probe's own internal timeout still lands in the catch below.
                throw;
            }
            catch (Exception ex)
            {
                sw.Stop();
                return (0, null, sw.ElapsedMilliseconds, ex);
            }
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
            string dialectLine = c.DialectId == null
                ? null
                : $"  dialect:      {c.DialectId} — {c.DialectEvidence}";

            if (!c.Reachable)
                return "✗ Could not reach the endpoint.\n" + (c.Error ?? "") +
                       (dialectLine == null ? "" : "\n" + dialectLine.TrimStart());
            if (!c.AuthOk)
                return "✗ " + (c.Error ?? "Authentication failed.");
            if (!c.Chat)
                return "✗ " + (c.Error ?? $"Chat request failed (HTTP {c.StatusCode}).") +
                       (dialectLine == null ? "" : "\n" + dialectLine.TrimStart());

            var sb = new StringBuilder();
            sb.AppendLine($"✓ Connected to \"{model}\"  (~{c.LatencyMs} ms)");
            if (dialectLine != null)
                sb.AppendLine(dialectLine);
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
