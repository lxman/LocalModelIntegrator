using System.Collections.Generic;
using System.Linq;
using LocalModelIntegrator.Models;

namespace LocalModelIntegrator.Services
{
    /// <summary>
    /// Registry of supported model protocols. Each protocol knows how to
    /// build the request body and extract the response text for its API shape.
    /// </summary>
    public static class ModelProtocolRegistry
    {
        private static readonly Dictionary<string, ModelProtocol> _protocols = new Dictionary<string, ModelProtocol>()
        {
            ["OpenAI Compatible"] = new ModelProtocol
            {
                DisplayName = "OpenAI Compatible",
                DefaultApiUrl = "https://api.openai.com/v1/chat/completions",
                DefaultModel = "gpt-4o",
                TokenDescription = "OpenAI API key (sk-...)",
                ResponseContentPaths = new[] { "choices[0].message.content", "choices[0].message.reasoning" },
                BuildRequestBody = req => new
                {
                    model = req.Model,
                    messages = req.Messages,
                    temperature = req.Temperature,
                    max_tokens = req.MaxTokens
                }
            },

            ["Ollama"] = new ModelProtocol
            {
                DisplayName = "Ollama",
                DefaultApiUrl = "http://localhost:11434/v1/chat/completions",
                DefaultModel = "llama3.2",
                TokenDescription = "Ollama (use 'ollama' or any dummy value)",
                ResponseContentPaths = new[] { "choices[0].message.content" },
                BuildRequestBody = req => new
                {
                    model = req.Model,
                    messages = req.Messages,
                    temperature = req.Temperature,
                    max_tokens = req.MaxTokens,
                    stream = false
                }
            },

            ["DeepSeek vLLM"] = new ModelProtocol
            {
                DisplayName = "DeepSeek vLLM",
                DefaultApiUrl = "http://192.168.0.179:8000/v1/chat/completions",
                DefaultModel = "deepseek-v4-flash",
                TokenDescription = "vLLM endpoint (any dummy value for local)",
                ResponseContentPaths = new[] { "choices[0].message.content", "choices[0].message.reasoning" },
                BuildRequestBody = req => new
                {
                    model = req.Model,
                    messages = req.Messages,
                    temperature = req.Temperature,
                    max_tokens = req.MaxTokens,
                    reasoning_effort = "none"
                }
            },

            ["LM Studio"] = new ModelProtocol
            {
                DisplayName = "LM Studio",
                DefaultApiUrl = "http://localhost:1234/v1/chat/completions",
                DefaultModel = "local-model",
                TokenDescription = "LM Studio (use 'lmstudio' or any dummy value)",
                ResponseContentPaths = new[] { "choices[0].message.content" },
                BuildRequestBody = req => new
                {
                    model = req.Model,
                    messages = req.Messages,
                    temperature = req.Temperature,
                    max_tokens = req.MaxTokens,
                    stream = false
                }
            }
        };

        public static ModelProtocol Get(string protocolName)
        {
            return _protocols.TryGetValue(protocolName, out ModelProtocol proto)
                ? proto
                : _protocols["OpenAI Compatible"];
        }

        public static string[] GetProtocolNames()
        {
            return _protocols.Keys.ToArray();
        }

        public static ModelProtocol GetDeepSeekProtocol()
        {
            return _protocols["DeepSeek vLLM"];
        }
    }
}
