using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using LocalModelIntegrator.Models;
using LocalModelIntegrator.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LocalModelIntegrator.Dialects
{
    /// <summary>
    /// The OpenAI chat-completions dialect - the lingua franca of local model servers (vLLM,
    /// LM Studio, Ollama, llama.cpp server, ...). One class serves every flavor; per-server
    /// quirks are constructor parameters (extra body fields), which is the same seam the D2
    /// declarative dialect descriptors will feed.
    /// </summary>
    public sealed class OpenAIChatDialect : IModelDialect
    {
        // Flavor quirks merged into every request body (e.g. Ollama's stream:false default,
        // DeepSeek vLLM's reasoning_effort). Explicit per-request fields overwrite them.
        private readonly JObject _extraBody;

        public string Id { get; }
        public string DisplayName { get; }
        public string DefaultApiUrl { get; }
        public string DefaultModel { get; }
        public string ApiKeyHint { get; }

        public OpenAIChatDialect(string id, string defaultApiUrl, string defaultModel,
            string apiKeyHint, JObject extraBody = null)
        {
            Id = id;
            DisplayName = id;
            DefaultApiUrl = defaultApiUrl;
            DefaultModel = defaultModel;
            ApiKeyHint = apiKeyHint;
            _extraBody = extraBody;
        }

        public string BuildRequestJson(DialectRequest request)
        {
            var messages = new JArray();
            foreach (ChatMessage m in request.Messages)
                messages.Add(ToMessageJson(m));

            var body = new JObject
            {
                ["model"] = request.Model,
                ["messages"] = messages,
                ["temperature"] = request.Temperature,
                ["max_tokens"] = request.MaxTokens
            };

            if (_extraBody != null)
            {
                foreach (JProperty p in _extraBody.Properties())
                    body[p.Name] = p.Value.DeepClone();
            }

            if (request.Stream.HasValue)
                body["stream"] = request.Stream.Value;

            if (request.ToolsJson != null)
            {
                body["tools"] = JArray.Parse(request.ToolsJson);
                body["tool_choice"] = "auto";
            }

            if (request.SuppressReasoning)
            {
                // vLLM / Qwen-style chat templates: turn off "thinking" so the model emits the
                // answer in `content` instead of spending the budget on reasoning.
                body["chat_template_kwargs"] = new JObject { ["enable_thinking"] = false };
                body["enable_thinking"] = false;
            }

            return body.ToString(Formatting.None);
        }

        // OpenAI message shape. An assistant turn carrying tool_calls may legitimately have
        // null content; a tool result carries the id of the tool_call it answers.
        private static JToken ToMessageJson(ChatMessage m)
        {
            if (m.ToolCalls == null && m.ToolCallId == null)
                return new JObject { ["role"] = m.Role, ["content"] = m.Content ?? string.Empty };

            var o = new JObject { ["role"] = m.Role, ["content"] = m.Content };
            if (m.ToolCalls != null)
                o["tool_calls"] = m.ToolCalls.DeepClone();
            if (m.ToolCallId != null)
                o["tool_call_id"] = m.ToolCallId;
            return o;
        }

        public void ApplyAuth(HttpRequestMessage request, string apiKey)
        {
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Add("Authorization", "Bearer " + apiKey);
        }

        public LlmToolTurn ParseResponse(string json)
        {
            // FirstOrDefault (not [0]): a present-but-empty "choices": [] - which OpenAI-compatible
            // gateways DO send (role/usage/keep-alive frames) - would make JArray[0] throw
            // ArgumentOutOfRangeException, since the null-conditional only guards a null choices.
            JToken message = (JObject.Parse(json)["choices"] as JArray)?.FirstOrDefault()?["message"];

            string reasoning = message?["reasoning_content"]?.ToString();
            if (string.IsNullOrEmpty(reasoning))
                reasoning = message?["reasoning"]?.ToString();

            return new LlmToolTurn
            {
                Content = message?["content"]?.ToString(),
                Reasoning = reasoning,
                ToolCalls = message?["tool_calls"] as JArray
            };
        }

        public IDialectStreamParser CreateStreamParser() => new SseParser();

        public string DeriveModelsUrl(string apiUrl)
        {
            int idx = apiUrl.IndexOf("/chat/completions", StringComparison.OrdinalIgnoreCase);
            return idx >= 0 ? apiUrl.Substring(0, idx) + "/models" : null;
        }

        public IReadOnlyList<string> ParseModelList(string json)
        {
            var ids = new List<string>();
            try
            {
                if (JObject.Parse(json)["data"] is JArray arr)
                {
                    foreach (JToken m in arr)
                    {
                        string id = m["id"]?.ToString();
                        if (!string.IsNullOrEmpty(id))
                            ids.Add(id);
                    }
                }
            }
            catch (JsonException)
            {
                // model listing is best-effort
            }
            return ids;
        }

        /// <summary>
        /// OpenAI SSE framing: "data: {json}" lines, a "[DONE]" sentinel, deltas at
        /// choices[0].delta with content / reasoning_content / reasoning fields, and
        /// tool_calls fragments merged by index.
        /// </summary>
        private sealed class SseParser : IDialectStreamParser
        {
            private readonly SortedDictionary<int, ToolCallAccumulator> _tools =
                new SortedDictionary<int, ToolCallAccumulator>();

            public bool Done { get; private set; }

            public DialectDelta ParseLine(string line)
            {
                if (string.IsNullOrEmpty(line) || !line.StartsWith("data:", StringComparison.Ordinal))
                    return null;

                string data = line.Substring("data:".Length).Trim();
                if (data == "[DONE]")
                {
                    Done = true;
                    return null;
                }

                JToken delta;
                // FirstOrDefault, not [0]: a streamed frame with an empty "choices": [] (common
                // first/last SSE chunk) must yield no delta, not an ArgumentOutOfRangeException.
                try { delta = (JObject.Parse(data)["choices"] as JArray)?.FirstOrDefault()?["delta"]; }
                catch (JsonException) { return null; }
                if (delta == null)
                    return null;

                if (delta["tool_calls"] is JArray fragments)
                    Accumulate(fragments);

                string reasoning = delta["reasoning_content"]?.ToString();
                if (string.IsNullOrEmpty(reasoning))
                    reasoning = delta["reasoning"]?.ToString();

                return new DialectDelta
                {
                    Content = delta["content"]?.ToString(),
                    Reasoning = reasoning
                };
            }

            private void Accumulate(JArray fragments)
            {
                foreach (JToken frag in fragments)
                {
                    int index = ResolveFragmentIndex(frag);
                    if (!_tools.TryGetValue(index, out ToolCallAccumulator acc))
                    {
                        acc = new ToolCallAccumulator();
                        _tools[index] = acc;
                    }
                    string id = frag["id"]?.ToString();
                    if (!string.IsNullOrEmpty(id)) acc.Id = id;
                    string name = frag["function"]?["name"]?.ToString();
                    if (!string.IsNullOrEmpty(name)) acc.Name = name;
                    string args = frag["function"]?["arguments"]?.ToString();
                    if (!string.IsNullOrEmpty(args)) acc.Arguments.Append(args);
                }
            }

            // Fragments are merged by their "index" field. Some servers omit it; without this
            // guard every parallel call would collapse into slot 0 and corrupt the arguments.
            // A fragment carrying a NEW id starts a new call; anything else continues the most
            // recent one.
            private int ResolveFragmentIndex(JToken frag)
            {
                JToken idx = frag["index"];
                if (idx != null && idx.Type != JTokenType.Null)
                    return idx.Value<int>();

                if (_tools.Count == 0)
                    return 0;

                string id = frag["id"]?.ToString();
                if (!string.IsNullOrEmpty(id))
                {
                    foreach (KeyValuePair<int, ToolCallAccumulator> kv in _tools)
                        if (string.Equals(kv.Value.Id, id, StringComparison.Ordinal))
                            return kv.Key;
                    return _tools.Keys.Max() + 1;
                }

                return _tools.Keys.Max();
            }

            public JArray ToolCalls
            {
                get
                {
                    if (_tools.Count == 0)
                        return null;

                    var calls = new JArray();
                    foreach (KeyValuePair<int, ToolCallAccumulator> kv in _tools)
                    {
                        calls.Add(new JObject
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
                    return calls;
                }
            }

            private sealed class ToolCallAccumulator
            {
                public string Id;
                public string Name;
                public readonly StringBuilder Arguments = new StringBuilder();
            }
        }
    }
}
