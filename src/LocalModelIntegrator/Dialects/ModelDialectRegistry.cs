using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace LocalModelIntegrator.Dialects
{
    /// <summary>
    /// The known model-server dialects: the OpenAI chat shape (the local-server lingua franca,
    /// several flavors of one class) plus Ollama's native API. A new wire format arrives as a
    /// new <see cref="IModelDialect"/> implementation here; <see cref="DialectResolver"/> adds
    /// the "Auto (detect)" choice on top. Ids are persisted in options (the "Protocol" setting),
    /// so they must stay stable.
    /// </summary>
    public static class ModelDialectRegistry
    {
        // Stable ids (persisted in options; also used by DialectResolver's detection).
        public const string OpenAIChatId = "OpenAI Compatible";
        public const string OllamaNativeId = "Ollama (native)";

        private static readonly IModelDialect[] _all =
        {
            new OpenAIChatDialect(
                id: OpenAIChatId,
                defaultApiUrl: "https://api.openai.com/v1/chat/completions",
                defaultModel: "gpt-4o",
                apiKeyHint: "OpenAI API key (sk-...)"),

            new OllamaNativeDialect(OllamaNativeId),

            // Ollama's OpenAI-compatible endpoint, kept for stored configs; "Auto (detect)"
            // and new setups prefer the native dialect above (per-request num_ctx).
            new OpenAIChatDialect(
                id: "Ollama",
                defaultApiUrl: "http://localhost:11434/v1/chat/completions",
                defaultModel: "llama3.2",
                apiKeyHint: "Ollama (use 'ollama' or any dummy value)",
                extraBody: new JObject { ["stream"] = false }),

            new OpenAIChatDialect(
                id: "DeepSeek vLLM",
                defaultApiUrl: "http://localhost:8000/v1/chat/completions",
                defaultModel: "deepseek-v4-flash",
                apiKeyHint: "vLLM endpoint (any dummy value for local)",
                // Open review item: this suppresses server-side thinking unconditionally,
                // which fights the EnableReasoning option - to be resolved in D2.
                extraBody: new JObject { ["reasoning_effort"] = "none" }),

            new OpenAIChatDialect(
                id: "LM Studio",
                defaultApiUrl: "http://localhost:1234/v1/chat/completions",
                defaultModel: "local-model",
                apiKeyHint: "LM Studio (use 'lmstudio' or any dummy value)",
                extraBody: new JObject { ["stream"] = false })
        };

        private static readonly Dictionary<string, IModelDialect> _byId =
            _all.ToDictionary(d => d.Id);

        /// <summary>The dialect with the given id, falling back to OpenAI Compatible.</summary>
        public static IModelDialect Get(string id)
        {
            return id != null && _byId.TryGetValue(id, out IModelDialect dialect)
                ? dialect
                : _byId[OpenAIChatId];
        }

        /// <summary>Dialect ids in display order (drives the options dropdown).</summary>
        public static string[] GetNames() => _all.Select(d => d.Id).ToArray();
    }
}
