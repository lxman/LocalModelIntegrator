using LocalModelIntegrator.Dialects;
using LocalModelIntegrator.Models;
using LocalModelIntegrator.Options;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LocalModelIntegrator.Services
{
    public class LLMService
    {
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };

        /// <summary>
        /// Calls the LLM API with conversation history and protocol-specific formatting.
        /// </summary>
        public async Task<string> CallLLMAsync(
            List<ChatMessage> messages,
            GeneralOptions options,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(options.ModelName))
                throw new InvalidOperationException("Model name is not configured. Set it in Tools > Options > Local Model Integrator.");

            if (string.IsNullOrWhiteSpace(options.ApiUrl))
                throw new InvalidOperationException("API URL is not configured. Set it in Tools > Options > Local Model Integrator.");

            if (!Uri.TryCreate(options.ApiUrl, UriKind.Absolute, out Uri uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https"))
                throw new InvalidOperationException($"Invalid API URL: {options.ApiUrl}");

            // The dialect owns the wire format; with "Auto (detect)" it (and the exact endpoint)
            // comes from server detection, cached per configuration.
            DialectResolution route = await DialectResolver.ResolveAsync(options, cancellationToken);
            IModelDialect dialect = route.Dialect;
            string jsonRequest = dialect.BuildRequestJson(new DialectRequest
            {
                Model = options.ModelName,
                Messages = ChatMessageNormalizer.CoalesceSystemMessages(messages),
                Temperature = options.Temperature,
                MaxTokens = options.MaxTokens,
                ContextWindowTokens = options.ContextWindowTokens
            });

            if (options.EnableLogging)
                DiagLog.Write("request", $"POST {route.Endpoint} [{dialect.Id} / {options.ModelName}]\n{jsonRequest}");

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, route.Endpoint);
            httpRequest.Content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");
            dialect.ApplyAuth(httpRequest, options.ApiKey);

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                if (options.RequestTimeout > 0)
                    cts.CancelAfter(options.RequestTimeout);

                try
                {
                    using HttpResponseMessage response = await _httpClient.SendAsync(httpRequest, cts.Token);

                    if (!response.IsSuccessStatusCode)
                    {
                        string errorText = await response.Content.ReadAsStringAsync();
                        if (options.EnableLogging)
                            DiagLog.Write("error", $"HTTP {(int)response.StatusCode}: {errorText}");
                        throw new HttpRequestException(
                            $"API error ({(int)response.StatusCode}): {errorText}");
                    }

                    string jsonResponse = await response.Content.ReadAsStringAsync();
                    if (options.EnableLogging)
                        DiagLog.Write("response", jsonResponse);

                    // Prefer the answer (content) over the chain-of-thought (reasoning). A
                    // reasoning model returns BOTH; the content carries the actual answer.
                    LlmToolTurn turn = dialect.ParseResponse(jsonResponse);
                    if (!string.IsNullOrWhiteSpace(turn.Content))
                        return turn.Content.Trim();
                    if (!string.IsNullOrWhiteSpace(turn.Reasoning))
                        return turn.Reasoning.Trim();

                    throw new InvalidOperationException("Empty response from API - all content fields are null or empty.");
                }
                catch (OperationCanceledException) when (cts.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"Request timed out after {options.RequestTimeout / 1000} seconds.");
                }
            }
        }

        /// <summary>
        /// Streaming variant of <see cref="CallLLMAsync"/>. Sends stream=true and reports
        /// content deltas via <paramref name="onDelta"/> as they arrive. Returns the full
        /// accumulated text. Works against OpenAI-compatible SSE endpoints
        /// (OpenAI, Ollama, LM Studio, vLLM).
        /// </summary>
        public async Task<string> CallLLMStreamingAsync(
            List<ChatMessage> messages,
            GeneralOptions options,
            IProgress<StreamDelta> onDelta,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(options.ModelName))
                throw new InvalidOperationException("Model name is not configured. Set it in Tools > Options > Local Model Integrator.");
            if (string.IsNullOrWhiteSpace(options.ApiUrl))
                throw new InvalidOperationException("API URL is not configured. Set it in Tools > Options > Local Model Integrator.");
            if (!Uri.TryCreate(options.ApiUrl, UriKind.Absolute, out Uri uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https"))
                throw new InvalidOperationException($"Invalid API URL: {options.ApiUrl}");

            DialectResolution route = await DialectResolver.ResolveAsync(options, cancellationToken);
            IModelDialect dialect = route.Dialect;
            string jsonRequest = dialect.BuildRequestJson(new DialectRequest
            {
                Model = options.ModelName,
                Messages = ChatMessageNormalizer.CoalesceSystemMessages(messages),
                Temperature = options.Temperature,
                MaxTokens = options.MaxTokens,
                Stream = true,   // force streaming on regardless of the dialect's default
                ContextWindowTokens = options.ContextWindowTokens
            });

            if (options.EnableLogging)
                DiagLog.Write("request (stream)", $"POST {route.Endpoint} [{dialect.Id} / {options.ModelName}]\n{jsonRequest}");

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, route.Endpoint);
            httpRequest.Content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");
            dialect.ApplyAuth(httpRequest, options.ApiKey);

            var answer = new StringBuilder();
            var reasoningBuffer = new StringBuilder();
            var reasoningParser = new ReasoningStreamParser();
            IDialectStreamParser frames = dialect.CreateStreamParser();

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                if (options.RequestTimeout > 0)
                    cts.CancelAfter(options.RequestTimeout);

                try
                {
                    using (HttpResponseMessage response = await _httpClient.SendAsync(
                        httpRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            string errorText = await response.Content.ReadAsStringAsync();
                            throw new HttpRequestException($"API error ({(int)response.StatusCode}): {errorText}");
                        }

                        using (Stream stream = await response.Content.ReadAsStreamAsync())
                        using (var reader = new StreamReader(stream))
                        {
                            string line;
                            while ((line = await reader.ReadLineAsync()) != null)
                            {
                                cts.Token.ThrowIfCancellationRequested();

                                // A frame can carry payload AND the end marker (Ollama's final
                                // NDJSON line) - only stop on Done once a line had no payload.
                                DialectDelta d = frames.ParseLine(line);
                                if (d == null)
                                {
                                    if (frames.Done)
                                        break;
                                    continue;
                                }

                                // Reasoning delivered in a dedicated field (e.g. DeepSeek reasoning_content).
                                // Buffer it unconditionally so we can fall back to it if no content arrives.
                                if (!string.IsNullOrEmpty(d.Reasoning))
                                {
                                    reasoningBuffer.Append(d.Reasoning);
                                    if (options.EnableReasoning)
                                        onDelta?.Report(new StreamDelta(true, d.Reasoning));
                                }

                                if (string.IsNullOrEmpty(d.Content))
                                    continue;

                                // Split any inline <think>...</think> out of the content stream.
                                (string content, string inlineReasoning) = reasoningParser.Push(d.Content);

                                if (!string.IsNullOrEmpty(inlineReasoning))
                                {
                                    reasoningBuffer.Append(inlineReasoning);
                                    if (options.EnableReasoning)
                                        onDelta?.Report(new StreamDelta(true, inlineReasoning));
                                }

                                if (!string.IsNullOrEmpty(content))
                                {
                                    answer.Append(content);
                                    onDelta?.Report(new StreamDelta(false, content));
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (cts.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    if (options.EnableLogging)
                        DiagLog.Write("error (stream)", $"timed out after {options.RequestTimeout / 1000}s (received {answer.Length} content chars so far)");
                    throw new TimeoutException($"Request timed out after {options.RequestTimeout / 1000} seconds.");
                }
            }

            string result = answer.ToString().Trim();
            if (result.Length == 0)
            {
                // The model emitted only reasoning and no content this turn (e.g. a reasoning model
                // that spent its whole token budget thinking and never reached the answer). Fall back
                // to the reasoning text instead of failing - mirrors the non-streaming path.
                string reasoningOnly = reasoningBuffer.ToString().Trim();
                if (reasoningOnly.Length > 0)
                {
                    if (options.EnableLogging)
                        DiagLog.Write("response (stream)", "(no content - using reasoning channel)\n" + reasoningOnly);
                    return reasoningOnly;
                }

                if (options.EnableLogging)
                    DiagLog.Write("error (stream)", "empty response (no content and no reasoning)");
                throw new InvalidOperationException("Empty response from API (stream produced no content).");
            }

            if (options.EnableLogging)
                DiagLog.Write("response (stream)", result);

            return result;
        }

        /// <summary>
        /// Non-streaming call that advertises native function tools. Returns the assistant turn's
        /// content, reasoning, and any tool_calls. Used by the agent when the probe confirms the
        /// endpoint supports native tool-calling.
        /// </summary>
        public async Task<LlmToolTurn> CallLLMWithToolsAsync(
            List<ChatMessage> messages,
            string toolsJson,
            GeneralOptions options,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(options.ModelName))
                throw new InvalidOperationException("Model name is not configured.");
            if (string.IsNullOrWhiteSpace(options.ApiUrl) ||
                !Uri.TryCreate(options.ApiUrl, UriKind.Absolute, out Uri uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https"))
                throw new InvalidOperationException($"Invalid API URL: {options.ApiUrl}");

            DialectResolution route = await DialectResolver.ResolveAsync(options, cancellationToken);
            IModelDialect dialect = route.Dialect;
            string jsonRequest = dialect.BuildRequestJson(new DialectRequest
            {
                Model = options.ModelName,
                Messages = ChatMessageNormalizer.CoalesceSystemMessages(messages),
                Temperature = options.Temperature,
                MaxTokens = options.MaxTokens,
                Stream = false,
                ToolsJson = toolsJson,
                ContextWindowTokens = options.ContextWindowTokens
            });

            if (options.EnableLogging)
                DiagLog.Write("request (tools)", $"POST {route.Endpoint} [{dialect.Id} / {options.ModelName}]\n{jsonRequest}");

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, route.Endpoint)
            {
                Content = new StringContent(jsonRequest, Encoding.UTF8, "application/json")
            };
            dialect.ApplyAuth(httpRequest, options.ApiKey);

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                if (options.RequestTimeout > 0)
                    cts.CancelAfter(options.RequestTimeout);
                try
                {
                    using HttpResponseMessage response = await _httpClient.SendAsync(httpRequest, cts.Token);
                    string jsonResponse = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        if (options.EnableLogging)
                            DiagLog.Write("error (tools)", $"HTTP {(int)response.StatusCode}: {jsonResponse}");
                        throw new HttpRequestException($"API error ({(int)response.StatusCode}): {jsonResponse}");
                    }

                    if (options.EnableLogging)
                        DiagLog.Write("response (tools)", jsonResponse);

                    return dialect.ParseResponse(jsonResponse);
                }
                catch (OperationCanceledException) when (cts.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    if (options.EnableLogging)
                        DiagLog.Write("error (tools)", $"timed out after {options.RequestTimeout / 1000}s");
                    throw new TimeoutException($"Request timed out after {options.RequestTimeout / 1000} seconds.");
                }
            }
        }

        /// <summary>
        /// Streaming variant of <see cref="CallLLMWithToolsAsync"/>: advertises native tools, streams
        /// reasoning to <paramref name="onDelta"/> as it arrives, and accumulates streamed tool_call
        /// fragments (merged by index) into a complete result. Used when the endpoint supports both
        /// native tools and SSE, so the agent's long steps stream live instead of going silent.
        /// </summary>
        public async Task<LlmToolTurn> CallLLMWithToolsStreamingAsync(
            List<ChatMessage> messages,
            string toolsJson,
            GeneralOptions options,
            IProgress<StreamDelta> onDelta,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(options.ModelName))
                throw new InvalidOperationException("Model name is not configured.");
            if (string.IsNullOrWhiteSpace(options.ApiUrl) ||
                !Uri.TryCreate(options.ApiUrl, UriKind.Absolute, out Uri uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https"))
                throw new InvalidOperationException($"Invalid API URL: {options.ApiUrl}");

            DialectResolution route = await DialectResolver.ResolveAsync(options, cancellationToken);
            IModelDialect dialect = route.Dialect;
            string jsonRequest = dialect.BuildRequestJson(new DialectRequest
            {
                Model = options.ModelName,
                Messages = ChatMessageNormalizer.CoalesceSystemMessages(messages),
                Temperature = options.Temperature,
                MaxTokens = options.MaxTokens,
                Stream = true,
                ToolsJson = toolsJson,
                ContextWindowTokens = options.ContextWindowTokens
            });

            if (options.EnableLogging)
                DiagLog.Write("request (tools-stream)", $"POST {route.Endpoint} [{dialect.Id} / {options.ModelName}]\n{jsonRequest}");

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, route.Endpoint)
            {
                Content = new StringContent(jsonRequest, Encoding.UTF8, "application/json")
            };
            dialect.ApplyAuth(httpRequest, options.ApiKey);

            var content = new StringBuilder();
            var reasoning = new StringBuilder();
            var inlineThink = new ReasoningStreamParser();
            IDialectStreamParser frames = dialect.CreateStreamParser();

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                if (options.RequestTimeout > 0)
                    cts.CancelAfter(options.RequestTimeout);
                try
                {
                    using (HttpResponseMessage response = await _httpClient.SendAsync(
                        httpRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            string errorText = await response.Content.ReadAsStringAsync();
                            if (options.EnableLogging)
                                DiagLog.Write("error (tools-stream)", $"HTTP {(int)response.StatusCode}: {errorText}");
                            throw new HttpRequestException($"API error ({(int)response.StatusCode}): {errorText}");
                        }

                        using (Stream stream = await response.Content.ReadAsStreamAsync())
                        using (var reader = new StreamReader(stream))
                        {
                            string line;
                            while ((line = await reader.ReadLineAsync()) != null)
                            {
                                cts.Token.ThrowIfCancellationRequested();

                                // The parser also accumulates streamed tool_call fragments
                                // internally. A frame can carry payload AND the end marker
                                // (Ollama's final NDJSON line) - only stop on Done once a line
                                // had no payload.
                                DialectDelta d = frames.ParseLine(line);
                                if (d == null)
                                {
                                    if (frames.Done)
                                        break;
                                    continue;
                                }

                                if (!string.IsNullOrEmpty(d.Reasoning))
                                {
                                    reasoning.Append(d.Reasoning);
                                    if (options.EnableReasoning)
                                        onDelta?.Report(new StreamDelta(true, d.Reasoning));
                                }

                                if (!string.IsNullOrEmpty(d.Content))
                                {
                                    (string cleaned, string inlineReasoning) = inlineThink.Push(d.Content);
                                    if (!string.IsNullOrEmpty(inlineReasoning))
                                    {
                                        reasoning.Append(inlineReasoning);
                                        if (options.EnableReasoning)
                                            onDelta?.Report(new StreamDelta(true, inlineReasoning));
                                    }
                                    if (!string.IsNullOrEmpty(cleaned))
                                        content.Append(cleaned);
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (cts.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    if (options.EnableLogging)
                        DiagLog.Write("error (tools-stream)", $"timed out after {options.RequestTimeout / 1000}s");
                    throw new TimeoutException($"Request timed out after {options.RequestTimeout / 1000} seconds.");
                }
            }

            JArray toolCalls = frames.ToolCalls;

            if (options.EnableLogging)
                DiagLog.Write("response (tools-stream)",
                    $"tool_calls: {(toolCalls?.Count ?? 0)}, content: {content.Length} chars, reasoning: {reasoning.Length} chars");

            return new LlmToolTurn
            {
                Content = content.ToString(),
                Reasoning = reasoning.ToString(),
                ToolCalls = toolCalls
            };
        }

        /// <summary>
        /// Requests a code completion at the cursor. <paramref name="prefix"/> is the text before
        /// the caret and <paramref name="suffix"/> the text after; the model is asked to produce
        /// only the code to insert. Returns null on any non-cancellation failure so typing is never
        /// disrupted. Cancellation propagates so callers can treat it as "user kept typing".
        /// </summary>
        public async Task<string> CompleteCodeAsync(
            string prefix,
            string suffix,
            string semanticContext,
            GeneralOptions options,
            int maxTokens,
            double temperature,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(options.ModelName) || string.IsNullOrWhiteSpace(options.ApiUrl))
                return null;
            if (!Uri.TryCreate(options.ApiUrl, UriKind.Absolute, out Uri uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https"))
                return null;

            DialectResolution route;
            try
            {
                route = await DialectResolver.ResolveAsync(options, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return null; // completions never disrupt typing
            }
            IModelDialect dialect = route.Dialect;

            var prompt = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(semanticContext))
                prompt.AppendLine("Relevant symbols in scope:").AppendLine(semanticContext).AppendLine();
            prompt.AppendLine("Complete the code at the <CURSOR> marker. Output ONLY the code to insert at the cursor - no explanations, no markdown fences.");
            prompt.AppendLine();
            prompt.Append(prefix).Append("<CURSOR>").Append(suffix);

            bool suppressReasoning = options.SuppressCompletionReasoning;

            string systemContent = "You are an expert code completion engine. You output only the code that belongs at the cursor position - no prose, no markdown fences.";
            if (suppressReasoning)
                systemContent += " /no_think";

            var messages = new List<ChatMessage>
            {
                new ChatMessage("system", systemContent),
                new ChatMessage("user", prompt.ToString())
            };

            // SuppressReasoning adds the dialect's wire-level "don't think" knobs (the /no_think
            // appended to the system prompt above is a model-level hint, not wire format).
            string jsonRequest = dialect.BuildRequestJson(new DialectRequest
            {
                Model = options.ModelName,
                Messages = ChatMessageNormalizer.CoalesceSystemMessages(messages),
                Temperature = temperature,
                MaxTokens = maxTokens,
                Stream = false,
                SuppressReasoning = suppressReasoning,
                ContextWindowTokens = options.ContextWindowTokens
            });

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, route.Endpoint)
            {
                Content = new StringContent(jsonRequest, Encoding.UTF8, "application/json")
            };
            dialect.ApplyAuth(httpRequest, options.ApiKey);

            try
            {
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    // Completions must be snappy; cap well below the chat timeout (even when chat is unlimited).
                    cts.CancelAfter(options.RequestTimeout > 0 ? Math.Min(options.RequestTimeout, 15000) : 15000);

                    using HttpResponseMessage response = await _httpClient.SendAsync(httpRequest, cts.Token);
                    if (!response.IsSuccessStatusCode)
                        return null;

                    string json = await response.Content.ReadAsStringAsync();
                    return CleanCompletion(dialect.ParseResponse(json).Content);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Strips surrounding markdown code fences and trims a model completion.</summary>
        private static string CleanCompletion(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;

            text = text.Trim();
            if (text.StartsWith("```"))
            {
                int firstNewline = text.IndexOf('\n');
                text = firstNewline >= 0 ? text.Substring(firstNewline + 1) : text.TrimStart('`');
                if (text.EndsWith("```"))
                    text = text.Substring(0, text.Length - 3);
                text = text.Trim('\r', '\n');
            }

            return text.Length == 0 ? null : text;
        }

        /// <summary>
        /// Trims message history to max count, preserving system prompt.
        /// </summary>
        public List<ChatMessage> TrimMessageHistory(List<ChatMessage> messages, int maxMessages)
        {
            if (messages.Count <= maxMessages)
                return new List<ChatMessage>(messages);

            List<ChatMessage> systemMessages = messages.Where(m => m.Role == "system").ToList();
            List<ChatMessage> otherMessages = messages.Where(m => m.Role != "system").ToList();
            int keepForOthers = maxMessages - systemMessages.Count;

            List<ChatMessage> recentMessages = otherMessages
                .Skip(Math.Max(0, otherMessages.Count - keepForOthers))
                .ToList();

            return systemMessages.Concat(recentMessages).ToList();
        }

    }

    /// <summary>One assistant turn as parsed from a reply: the content, any dedicated-channel
    /// reasoning, and any native tool calls. Produced by the dialect's response parsing.</summary>
    public sealed class LlmToolTurn
    {
        public string Content { get; set; }
        public string Reasoning { get; set; }
        public Newtonsoft.Json.Linq.JArray ToolCalls { get; set; }
    }

    /// <summary>One incremental piece of a streamed response: either answer content or reasoning.</summary>
    public sealed class StreamDelta
    {
        public bool IsReasoning { get; }
        public string Text { get; }

        public StreamDelta(bool isReasoning, string text)
        {
            IsReasoning = isReasoning;
            Text = text;
        }
    }

    /// <summary>
    /// Separates inline &lt;think&gt;...&lt;/think&gt; reasoning from answer content across
    /// streamed chunks. Tracks open/closed state and buffers partial tags that span chunk
    /// boundaries so a tag split across two SSE events is still recognized.
    /// </summary>
    internal sealed class ReasoningStreamParser
    {
        private const string OpenTag = "<think>";
        private const string CloseTag = "</think>";
        private bool _inThink;
        private string _carry = string.Empty;

        public (string content, string reasoning) Push(string text)
        {
            if (string.IsNullOrEmpty(text))
                return (null, null);

            string s = _carry + text;
            _carry = string.Empty;

            var content = new StringBuilder();
            var reasoning = new StringBuilder();
            int i = 0;

            while (i < s.Length)
            {
                if (!_inThink)
                {
                    int open = s.IndexOf(OpenTag, i, StringComparison.OrdinalIgnoreCase);
                    if (open < 0)
                    {
                        int partial = TrailingPartialTag(s, i, OpenTag);
                        if (partial >= 0) { content.Append(s.Substring(i, partial - i)); _carry = s.Substring(partial); }
                        else content.Append(s.Substring(i));
                        break;
                    }
                    content.Append(s.Substring(i, open - i));
                    i = open + OpenTag.Length;
                    _inThink = true;
                }
                else
                {
                    int close = s.IndexOf(CloseTag, i, StringComparison.OrdinalIgnoreCase);
                    if (close < 0)
                    {
                        int partial = TrailingPartialTag(s, i, CloseTag);
                        if (partial >= 0) { reasoning.Append(s.Substring(i, partial - i)); _carry = s.Substring(partial); }
                        else reasoning.Append(s.Substring(i));
                        break;
                    }
                    reasoning.Append(s.Substring(i, close - i));
                    i = close + CloseTag.Length;
                    _inThink = false;
                }
            }

            return (content.Length > 0 ? content.ToString() : null,
                    reasoning.Length > 0 ? reasoning.ToString() : null);
        }

        // If a suffix of s is a (non-empty) prefix of tag, return that suffix's start index; else -1.
        private static int TrailingPartialTag(string s, int from, string tag)
        {
            int max = Math.Min(tag.Length - 1, s.Length - from);
            for (int len = max; len > 0; len--)
            {
                if (string.Compare(s, s.Length - len, tag, 0, len, StringComparison.OrdinalIgnoreCase) == 0)
                    return s.Length - len;
            }
            return -1;
        }
    }
}
