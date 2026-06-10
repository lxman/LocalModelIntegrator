using System.Collections.Generic;
using System.Net.Http;
using LocalModelIntegrator.Models;
using LocalModelIntegrator.Services;
using Newtonsoft.Json.Linq;

namespace LocalModelIntegrator.Dialects
{
    /// <summary>
    /// A model-server "dialect": everything about HOW to talk to a chat endpoint - request body
    /// shape, auth, streaming framing, and where content/reasoning/tool calls live in replies.
    /// The transport layer (LLMService) and the agent never see the wire format; supporting a
    /// differently-shaped server means implementing this interface, nothing else.
    ///
    /// The capability probe also speaks through this interface (bodies via BuildRequestJson,
    /// replies via ParseResponse / the stream parser), and DialectResolver picks the dialect
    /// automatically when options are set to "Auto (detect)". One piece of dialect knowledge
    /// deliberately remains outside: the agent's tool-definition encoding
    /// (<see cref="DialectRequest.ToolsJson"/> is OpenAI function-tool JSON; dialects whose
    /// servers want another tool encoding convert it).
    /// </summary>
    public interface IModelDialect
    {
        /// <summary>Stable identifier persisted in options (the "Protocol" setting) - never rename.</summary>
        string Id { get; }

        string DisplayName { get; }

        // Self-description for the options UX (D2 offers these when the user switches dialect).
        string DefaultApiUrl { get; }
        string DefaultModel { get; }
        string ApiKeyHint { get; }

        /// <summary>Builds the JSON request body for one chat turn.</summary>
        string BuildRequestJson(DialectRequest request);

        /// <summary>Applies this dialect's auth scheme; no-op when the key is blank.</summary>
        void ApplyAuth(HttpRequestMessage request, string apiKey);

        /// <summary>
        /// Extracts content / reasoning / native tool calls from a non-streamed reply. A reply
        /// missing the expected structure yields empty fields; malformed JSON throws (callers
        /// that must never throw, like completions, already catch).
        /// </summary>
        LlmToolTurn ParseResponse(string json);

        /// <summary>Creates a stateful parser for one streamed reply (framing + delta fields).</summary>
        IDialectStreamParser CreateStreamParser();

        /// <summary>The model-list endpoint for a given chat endpoint, or null if unsupported.</summary>
        string DeriveModelsUrl(string apiUrl);

        /// <summary>Model ids from a model-list reply (empty on any parse trouble).</summary>
        IReadOnlyList<string> ParseModelList(string json);
    }

    /// <summary>Everything a dialect needs to shape one request.</summary>
    public sealed class DialectRequest
    {
        public string Model { get; set; }
        public IReadOnlyList<ChatMessage> Messages { get; set; }
        public double Temperature { get; set; }
        public int MaxTokens { get; set; }

        /// <summary>true/false forces streaming on/off; null leaves the dialect's default.</summary>
        public bool? Stream { get; set; }

        /// <summary>Native tool definitions to advertise, or null for none. OpenAI function-tool
        /// JSON for now; lifting the encoding into the dialect is D3 work.</summary>
        public string ToolsJson { get; set; }

        /// <summary>Ask the server to skip "thinking" (used by completions, where latency rules).</summary>
        public bool SuppressReasoning { get; set; }

        /// <summary>
        /// The user-configured context window in tokens (0 = unset). Dialects whose servers can
        /// size the context per request use it (Ollama native: options.num_ctx - its default
        /// context otherwise silently truncates long transcripts); others ignore it. Callers set
        /// it on EVERY request, completions included, because Ollama reloads the model when
        /// num_ctx changes between requests.
        /// </summary>
        public int ContextWindowTokens { get; set; }
    }

    /// <summary>
    /// Parses one streamed reply line by line. The transport reads lines and feeds them here;
    /// both SSE ("data: {...}") and NDJSON dialects are line-framed, so the seam holds for both.
    /// Stateful per call: accumulates tool-call fragments across lines.
    /// </summary>
    public interface IDialectStreamParser
    {
        /// <summary>Deltas carried by one line, or null for noise/keep-alives/the end marker.</summary>
        DialectDelta ParseLine(string line);

        /// <summary>True once the dialect's end-of-stream marker has been seen.</summary>
        bool Done { get; }

        /// <summary>Tool calls assembled from streamed fragments, or null if none arrived.</summary>
        JArray ToolCalls { get; }
    }

    /// <summary>Content and/or dedicated-channel reasoning carried by one stream line.</summary>
    public sealed class DialectDelta
    {
        public string Content { get; set; }
        public string Reasoning { get; set; }
    }
}
