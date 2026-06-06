using LocalModelIntegrator.Models;
using LocalModelIntegrator.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
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

            // Get the protocol definition
            ModelProtocol protocol = ModelProtocolRegistry.Get(options.Protocol);

            // Build messages list
            List<object> messagePayload = messages.Select(m => m.ToRequestBody()).ToList();

            // Build request body via protocol
            object requestBody = protocol.BuildRequestBody(new ChatCompletionRequest
            {
                Model = options.ModelName,
                Messages = messagePayload,
                Temperature = options.Temperature,
                MaxTokens = options.MaxTokens
            });

            string jsonRequest = JsonConvert.SerializeObject(requestBody);

            if (options.EnableLogging)
                DiagLog.Write("request", $"POST {options.ApiUrl} [{options.Protocol} / {options.ModelName}]\n{jsonRequest}");

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, options.ApiUrl);
            httpRequest.Content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

            // Add auth header if key is provided
            if (!string.IsNullOrWhiteSpace(options.ApiKey))
                httpRequest.Headers.Add("Authorization", $"Bearer {options.ApiKey}");

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                if (options.RequestTimeout > 0)
                    cts.CancelAfter(options.RequestTimeout);

                try
                {
                    HttpResponseMessage response = await _httpClient.SendAsync(httpRequest, cts.Token);

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
                    JObject responseObj = JObject.Parse(jsonResponse);

                    // Try each content path in order defined by the protocol
                    var choices = responseObj["choices"] as JArray;
                    if (choices == null || choices.Count == 0)
                        throw new InvalidOperationException("No response choices returned from API");

                    JToken message = choices[0]["message"];

                    // Walk the response content paths in protocol order (content first, per the registry).
                    foreach (string path in protocol.ResponseContentPaths)
                    {
                        JToken token = ResolveJsonPath(responseObj, path);
                        if (token != null && token.Type == JTokenType.String)
                        {
                            string value = token.ToString();
                            if (!string.IsNullOrWhiteSpace(value))
                                return value.Trim();
                        }
                    }

                    // Fallback: prefer the answer (content) over the chain-of-thought (reasoning).
                    // A reasoning model returns BOTH; the content carries the actual answer/tool call.
                    string directContent = message?["content"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(directContent))
                        return directContent.Trim();

                    string reasoning = message?["reasoning"]?.ToString();
                    if (string.IsNullOrWhiteSpace(reasoning))
                        reasoning = message?["reasoning_content"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(reasoning))
                        return reasoning.Trim();

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

            ModelProtocol protocol = ModelProtocolRegistry.Get(options.Protocol);
            List<object> messagePayload = messages.Select(m => m.ToRequestBody()).ToList();
            object requestBody = protocol.BuildRequestBody(new ChatCompletionRequest
            {
                Model = options.ModelName,
                Messages = messagePayload,
                Temperature = options.Temperature,
                MaxTokens = options.MaxTokens
            });

            // Force streaming on regardless of the protocol's default.
            JObject body = JObject.FromObject(requestBody);
            body["stream"] = true;
            string jsonRequest = body.ToString(Formatting.None);

            if (options.EnableLogging)
                DiagLog.Write("request (stream)", $"POST {options.ApiUrl} [{options.Protocol} / {options.ModelName}]\n{jsonRequest}");

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, options.ApiUrl);
            httpRequest.Content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");
            if (!string.IsNullOrWhiteSpace(options.ApiKey))
                httpRequest.Headers.Add("Authorization", $"Bearer {options.ApiKey}");

            var answer = new StringBuilder();
            var reasoningBuffer = new StringBuilder();
            var reasoningParser = new ReasoningStreamParser();

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

                                if (line.Length == 0 || !line.StartsWith("data:"))
                                    continue;

                                string data = line.Substring("data:".Length).Trim();
                                if (data == "[DONE]")
                                    break;

                                (string contentField, string reasoningField) = ParseDeltaFields(data);

                                // Reasoning delivered in a dedicated field (e.g. DeepSeek reasoning_content).
                                // Buffer it unconditionally so we can fall back to it if no content arrives.
                                if (!string.IsNullOrEmpty(reasoningField))
                                {
                                    reasoningBuffer.Append(reasoningField);
                                    if (options.EnableReasoning)
                                        onDelta?.Report(new StreamDelta(true, reasoningField));
                                }

                                if (string.IsNullOrEmpty(contentField))
                                    continue;

                                // Split any inline <think>...</think> out of the content stream.
                                (string content, string inlineReasoning) = reasoningParser.Push(contentField);

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

            ModelProtocol protocol = ModelProtocolRegistry.Get(options.Protocol);
            List<object> messagePayload = messages.Select(m => m.ToRequestBody()).ToList();
            object requestBody = protocol.BuildRequestBody(new ChatCompletionRequest
            {
                Model = options.ModelName,
                Messages = messagePayload,
                Temperature = options.Temperature,
                MaxTokens = options.MaxTokens
            });

            JObject body = JObject.FromObject(requestBody);
            body["stream"] = false;
            body["tools"] = JArray.Parse(toolsJson);
            body["tool_choice"] = "auto";
            string jsonRequest = body.ToString(Formatting.None);

            if (options.EnableLogging)
                DiagLog.Write("request (tools)", $"POST {options.ApiUrl} [{options.Protocol} / {options.ModelName}]\n{jsonRequest}");

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, options.ApiUrl)
            {
                Content = new StringContent(jsonRequest, Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(options.ApiKey))
                httpRequest.Headers.Add("Authorization", $"Bearer {options.ApiKey}");

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                if (options.RequestTimeout > 0)
                    cts.CancelAfter(options.RequestTimeout);
                try
                {
                    HttpResponseMessage response = await _httpClient.SendAsync(httpRequest, cts.Token);
                    string jsonResponse = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        if (options.EnableLogging)
                            DiagLog.Write("error (tools)", $"HTTP {(int)response.StatusCode}: {jsonResponse}");
                        throw new HttpRequestException($"API error ({(int)response.StatusCode}): {jsonResponse}");
                    }

                    if (options.EnableLogging)
                        DiagLog.Write("response (tools)", jsonResponse);

                    JToken message = JObject.Parse(jsonResponse)["choices"]?[0]?["message"];
                    return new LlmToolTurn
                    {
                        Content = message?["content"]?.ToString(),
                        Reasoning = message?["reasoning_content"]?.ToString() ?? message?["reasoning"]?.ToString(),
                        ToolCalls = message?["tool_calls"] as JArray
                    };
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

            ModelProtocol protocol = ModelProtocolRegistry.Get(options.Protocol);
            List<object> messagePayload = messages.Select(m => m.ToRequestBody()).ToList();
            object requestBody = protocol.BuildRequestBody(new ChatCompletionRequest
            {
                Model = options.ModelName,
                Messages = messagePayload,
                Temperature = options.Temperature,
                MaxTokens = options.MaxTokens
            });

            JObject body = JObject.FromObject(requestBody);
            body["stream"] = true;
            body["tools"] = JArray.Parse(toolsJson);
            body["tool_choice"] = "auto";
            string jsonRequest = body.ToString(Formatting.None);

            if (options.EnableLogging)
                DiagLog.Write("request (tools-stream)", $"POST {options.ApiUrl} [{options.Protocol} / {options.ModelName}]\n{jsonRequest}");

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, options.ApiUrl)
            {
                Content = new StringContent(jsonRequest, Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(options.ApiKey))
                httpRequest.Headers.Add("Authorization", $"Bearer {options.ApiKey}");

            var content = new StringBuilder();
            var reasoning = new StringBuilder();
            var toolAcc = new SortedDictionary<int, ToolCallAccumulator>();
            var inlineThink = new ReasoningStreamParser();

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
                                if (line.Length == 0 || !line.StartsWith("data:"))
                                    continue;
                                string data = line.Substring("data:".Length).Trim();
                                if (data == "[DONE]")
                                    break;

                                JToken delta;
                                try { delta = JObject.Parse(data)["choices"]?[0]?["delta"]; }
                                catch (JsonException) { continue; }
                                if (delta == null)
                                    continue;

                                string r = delta["reasoning_content"]?.ToString() ?? delta["reasoning"]?.ToString();
                                if (!string.IsNullOrEmpty(r))
                                {
                                    reasoning.Append(r);
                                    if (options.EnableReasoning)
                                        onDelta?.Report(new StreamDelta(true, r));
                                }

                                string c = delta["content"]?.ToString();
                                if (!string.IsNullOrEmpty(c))
                                {
                                    (string cleaned, string inlineReasoning) = inlineThink.Push(c);
                                    if (!string.IsNullOrEmpty(inlineReasoning))
                                    {
                                        reasoning.Append(inlineReasoning);
                                        if (options.EnableReasoning)
                                            onDelta?.Report(new StreamDelta(true, inlineReasoning));
                                    }
                                    if (!string.IsNullOrEmpty(cleaned))
                                        content.Append(cleaned);
                                }

                                if (delta["tool_calls"] is JArray fragments)
                                {
                                    foreach (JToken frag in fragments)
                                    {
                                        int index = frag["index"]?.Value<int>() ?? 0;
                                        if (!toolAcc.TryGetValue(index, out ToolCallAccumulator acc))
                                        {
                                            acc = new ToolCallAccumulator();
                                            toolAcc[index] = acc;
                                        }
                                        string id = frag["id"]?.ToString();
                                        if (!string.IsNullOrEmpty(id)) acc.Id = id;
                                        string name = frag["function"]?["name"]?.ToString();
                                        if (!string.IsNullOrEmpty(name)) acc.Name = name;
                                        string args = frag["function"]?["arguments"]?.ToString();
                                        if (!string.IsNullOrEmpty(args)) acc.Arguments.Append(args);
                                    }
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

            JArray toolCalls = null;
            if (toolAcc.Count > 0)
            {
                toolCalls = new JArray();
                foreach (KeyValuePair<int, ToolCallAccumulator> kv in toolAcc)
                {
                    toolCalls.Add(new JObject
                    {
                        ["id"] = kv.Value.Id ?? ("call_" + kv.Key),
                        ["type"] = "function",
                        ["function"] = new JObject
                        {
                            ["name"] = kv.Value.Name ?? string.Empty,
                            ["arguments"] = kv.Value.Arguments.ToString()
                        }
                    });
                }
            }

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

        private sealed class ToolCallAccumulator
        {
            public string Id;
            public string Name;
            public readonly StringBuilder Arguments = new StringBuilder();
        }

        /// <summary>
        /// Reads choices[0].delta from one SSE chunk, returning the content token and any
        /// dedicated reasoning token (reasoning_content or reasoning). Non-JSON keep-alive
        /// lines yield (null, null).
        /// </summary>
        private static (string content, string reasoning) ParseDeltaFields(string data)
        {
            try
            {
                JToken delta = JObject.Parse(data)["choices"]?[0]?["delta"];
                if (delta == null)
                    return (null, null);

                string content = delta["content"]?.ToString();
                string reasoning = delta["reasoning_content"]?.ToString();
                if (string.IsNullOrEmpty(reasoning))
                    reasoning = delta["reasoning"]?.ToString();

                return (content, reasoning);
            }
            catch (JsonException)
            {
                return (null, null);
            }
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

            ModelProtocol protocol = ModelProtocolRegistry.Get(options.Protocol);

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

            var messages = new List<object>
            {
                new { role = "system", content = systemContent },
                new { role = "user", content = prompt.ToString() }
            };

            object requestBody = protocol.BuildRequestBody(new ChatCompletionRequest
            {
                Model = options.ModelName,
                Messages = messages,
                Temperature = temperature,
                MaxTokens = maxTokens
            });

            JObject body = JObject.FromObject(requestBody);
            body["stream"] = false;

            if (suppressReasoning)
            {
                // vLLM / Qwen-style chat templates: turn off "thinking" so the model emits the
                // answer in `content` instead of spending the budget on reasoning.
                body["chat_template_kwargs"] = new JObject { ["enable_thinking"] = false };
                body["enable_thinking"] = false;
            }

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, options.ApiUrl)
            {
                Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(options.ApiKey))
                httpRequest.Headers.Add("Authorization", $"Bearer {options.ApiKey}");

            try
            {
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    // Completions must be snappy; cap well below the chat timeout (even when chat is unlimited).
                    cts.CancelAfter(options.RequestTimeout > 0 ? Math.Min(options.RequestTimeout, 15000) : 15000);

                    HttpResponseMessage response = await _httpClient.SendAsync(httpRequest, cts.Token);
                    if (!response.IsSuccessStatusCode)
                        return null;

                    string json = await response.Content.ReadAsStringAsync();
                    string content = JObject.Parse(json)["choices"]?[0]?["message"]?["content"]?.ToString();
                    return CleanCompletion(content);
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

        private static readonly Regex IndexSegment = new Regex(@"\[(\d+)\]", RegexOptions.Compiled);

        /// <summary>
        /// Resolves a simple JSON path like "choices[0].message.content" against a token, supporting
        /// object keys and array indices, including combined "key[0]" segments. Returns null if any
        /// segment is missing. (The previous splitter treated "choices[0]" as a literal key and so
        /// never matched, which silently fell through to the reasoning field.)
        /// </summary>
        private static JToken ResolveJsonPath(JToken root, string path)
        {
            if (root == null || string.IsNullOrEmpty(path))
                return null;

            JToken current = root;
            foreach (string segment in path.Split('.'))
            {
                if (current == null)
                    return null;

                int bracket = segment.IndexOf('[');
                string key = bracket < 0 ? segment : segment.Substring(0, bracket);
                if (key.Length > 0)
                    current = current[key];

                if (bracket >= 0)
                {
                    foreach (Match m in IndexSegment.Matches(segment.Substring(bracket)))
                    {
                        if (current is JArray arr && int.TryParse(m.Groups[1].Value, out int idx) && idx < arr.Count)
                            current = arr[idx];
                        else
                            return null;
                    }
                }
            }
            return current;
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

        /// <summary>
        /// Extracts file suggestions from response in ```file path="..." blocks.
        /// </summary>
        public List<FileSuggestion> ExtractFileSuggestions(string text)
        {
            var suggestions = new List<FileSuggestion>();
            var regex = new System.Text.RegularExpressions.Regex(
                @"```file\s+path=""([^""]+)""[\r\n]+([\s\S]*?)```",
                System.Text.RegularExpressions.RegexOptions.Multiline);

            MatchCollection matches = regex.Matches(text);
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                if (match.Groups.Count >= 3)
                {
                    string path = match.Groups[1].Value.Trim();
                    string content = match.Groups[2].Value;
                    if (!string.IsNullOrEmpty(path))
                        suggestions.Add(new FileSuggestion(path, content));
                }
            }

            return suggestions;
        }
    }

    /// <summary>Result of a non-streaming native tool-calling turn.</summary>
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
