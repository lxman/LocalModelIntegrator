namespace LocalModelIntegrator.Models
{
    /// <summary>
    /// Defines the protocol/API shape for a supported model provider.
    /// Each protocol knows how to build the request body and extract the response
    /// text for its specific endpoint.
    /// </summary>
    public class ModelProtocol
    {
        /// <summary>Display name shown in the options dropdown (e.g., "OpenAI Compatible", "Ollama")</summary>
        public string DisplayName { get; set; }

        /// <summary>Default API URL template (e.g., "http://localhost:11434/v1/chat/completions")</summary>
        public string DefaultApiUrl { get; set; }

        /// <summary>Default model name to suggest</summary>
        public string DefaultModel { get; set; }

        /// <summary>Description of the API token field for this provider</summary>
        public string TokenDescription { get; set; }

        /// <summary>
        /// Builds the JSON request body for a chat completion call.
        /// Returns a dictionary of JSON properties to serialize.
        /// </summary>
        public System.Func<ChatCompletionRequest, object> BuildRequestBody { get; set; }

        /// <summary>
        /// Extracts the response text from the JSON response.
        /// First non-null match wins, in order of fields checked.
        /// </summary>
        public string[] ResponseContentPaths { get; set; }
    }

    public class ChatCompletionRequest
    {
        public string Model { get; set; }
        public System.Collections.Generic.List<object> Messages { get; set; }
        public double Temperature { get; set; }
        public int MaxTokens { get; set; }
    }
}
