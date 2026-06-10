using System;
using System.Collections.Generic;
using System.Net.Http;
using LocalModelIntegrator.Models;
using LocalModelIntegrator.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LocalModelIntegrator.Dialects
{
    /// <summary>
    /// Ollama's native chat API (/api/chat). Differences from the OpenAI chat dialect that make
    /// it worth speaking natively rather than using Ollama's OpenAI-compatible endpoint:
    ///   - options.num_ctx sizes the context per request; the OpenAI endpoint runs the model at
    ///     its default context and SILENTLY TRUNCATES longer transcripts (poison for the agent).
    ///   - Streaming is NDJSON (one JSON object per line, "done" flag) - not SSE "data:" frames.
    ///   - Reasoning arrives in message.thinking; "think": false suppresses it server-side.
    ///   - The model list lives at /api/tags.
    /// Tool definitions use the same OpenAI function schema (pass-through), but tool CALLS come
    /// back with function.arguments as a JSON OBJECT and usually no id - normalized here to the
    /// OpenAI shape (arguments as string, synthesized ids) that the agent stores, and converted
    /// back when the transcript is replayed.
    /// </summary>
    public sealed class OllamaNativeDialect : IModelDialect
    {
        public string Id { get; }
        public string DisplayName { get; }
        public string DefaultApiUrl => "http://localhost:11434/api/chat";
        public string DefaultModel => "llama3.2";
        public string ApiKeyHint => "Not used by Ollama (any value; sent as Bearer for proxies)";

        public OllamaNativeDialect(string id)
        {
            Id = id;
            DisplayName = id;
        }

        public string BuildRequestJson(DialectRequest request)
        {
            var messages = new JArray();
            foreach (ChatMessage m in request.Messages)
                messages.Add(ToMessageJson(m));

            var options = new JObject
            {
                ["temperature"] = request.Temperature,
                ["num_predict"] = request.MaxTokens
            };
            if (request.ContextWindowTokens > 0)
                options["num_ctx"] = request.ContextWindowTokens;

            var body = new JObject
            {
                ["model"] = request.Model,
                ["messages"] = messages,
                // Ollama streams BY DEFAULT; the transport expects a single JSON reply unless it
                // explicitly asked for a stream, so default to false here.
                ["stream"] = request.Stream ?? false,
                ["options"] = options
            };

            if (request.ToolsJson != null)
                body["tools"] = JArray.Parse(request.ToolsJson); // same OpenAI function schema

            if (request.SuppressReasoning)
                body["think"] = false;

            return body.ToString(Formatting.None);
        }

        // Ollama message shape: role/content, assistant tool_calls carry function.arguments as a
        // JSON OBJECT (parse the normalized string form back), tool results are role:"tool" with
        // plain content - Ollama matches them to calls positionally, ids are not in its schema.
        private static JToken ToMessageJson(ChatMessage m)
        {
            var o = new JObject { ["role"] = m.Role, ["content"] = m.Content ?? string.Empty };
            if (m.ToolCalls == null)
                return o;

            var calls = new JArray();
            foreach (JToken tc in m.ToolCalls)
            {
                JToken fn = tc["function"];
                JToken args = fn?["arguments"];
                string argsStr = args == null
                    ? null
                    : (args.Type == JTokenType.String ? args.ToString() : args.ToString(Formatting.None));

                JToken argsOut;
                try { argsOut = string.IsNullOrWhiteSpace(argsStr) ? new JObject() : JToken.Parse(argsStr); }
                catch (JsonException) { argsOut = new JValue(argsStr); }

                calls.Add(new JObject
                {
                    ["function"] = new JObject
                    {
                        ["name"] = fn?["name"]?.ToString() ?? string.Empty,
                        ["arguments"] = argsOut
                    }
                });
            }
            o["tool_calls"] = calls;
            return o;
        }

        public void ApplyAuth(HttpRequestMessage request, string apiKey)
        {
            // Ollama itself is unauthenticated; reverse proxies in front of it use Bearer.
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Add("Authorization", "Bearer " + apiKey);
        }

        public LlmToolTurn ParseResponse(string json)
        {
            JToken message = JObject.Parse(json)["message"];

            JArray tools = null;
            if (message?["tool_calls"] is JArray calls && calls.Count > 0)
            {
                tools = new JArray();
                int i = 0;
                foreach (JToken c in calls)
                    tools.Add(NormalizeToolCall(c, i++));
            }

            return new LlmToolTurn
            {
                Content = message?["content"]?.ToString(),
                Reasoning = message?["thinking"]?.ToString(),
                ToolCalls = tools
            };
        }

        // Normalizes one Ollama tool call to the OpenAI shape the agent stores: id (synthesized
        // when absent), type, and function.arguments as a JSON STRING.
        private static JObject NormalizeToolCall(JToken call, int ordinal)
        {
            JToken fn = call["function"];
            JToken args = fn?["arguments"];
            string argsJson = args == null
                ? "{}"
                : (args.Type == JTokenType.String ? args.ToString() : args.ToString(Formatting.None));

            return new JObject
            {
                ["id"] = call["id"]?.ToString() ?? ("call_" + ordinal),
                ["type"] = "function",
                ["function"] = new JObject
                {
                    ["name"] = fn?["name"]?.ToString() ?? string.Empty,
                    ["arguments"] = argsJson
                }
            };
        }

        public IDialectStreamParser CreateStreamParser() => new NdjsonParser();

        public string DeriveModelsUrl(string apiUrl)
        {
            int idx = apiUrl.IndexOf("/api/chat", StringComparison.OrdinalIgnoreCase);
            return idx >= 0 ? apiUrl.Substring(0, idx) + "/api/tags" : null;
        }

        public IReadOnlyList<string> ParseModelList(string json)
        {
            var ids = new List<string>();
            try
            {
                if (JObject.Parse(json)["models"] is JArray arr)
                {
                    foreach (JToken m in arr)
                    {
                        string name = m["name"]?.ToString();
                        if (!string.IsNullOrEmpty(name))
                            ids.Add(name);
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
        /// NDJSON framing: every line is a complete JSON object carrying message.content /
        /// message.thinking / message.tool_calls and a "done" flag on the final frame. The final
        /// frame can carry payload AND done:true, so Done is set without suppressing the delta.
        /// </summary>
        private sealed class NdjsonParser : IDialectStreamParser
        {
            private readonly List<JToken> _toolCalls = new List<JToken>();

            public bool Done { get; private set; }

            public JArray ToolCalls => _toolCalls.Count == 0 ? null : new JArray(_toolCalls);

            public DialectDelta ParseLine(string line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    return null;

                JObject frame;
                try { frame = JObject.Parse(line); }
                catch (JsonException) { return null; }

                if (frame["done"]?.Type == JTokenType.Boolean && frame.Value<bool>("done"))
                    Done = true;

                JToken message = frame["message"];
                if (message == null)
                    return null;

                if (message["tool_calls"] is JArray calls)
                {
                    foreach (JToken c in calls)
                        _toolCalls.Add(NormalizeToolCall(c, _toolCalls.Count));
                }

                string content = message["content"]?.ToString();
                string thinking = message["thinking"]?.ToString();
                if (string.IsNullOrEmpty(content) && string.IsNullOrEmpty(thinking))
                    return null;

                return new DialectDelta { Content = content, Reasoning = thinking };
            }
        }
    }
}
