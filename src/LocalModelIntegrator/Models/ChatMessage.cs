using Newtonsoft.Json.Linq;

namespace LocalModelIntegrator.Models
{
    public class ChatMessage
    {
        public string Role { get; set; }
        public string Content { get; set; }

        /// <summary>For an assistant turn that requested native tools: the raw tool_calls array to echo back.</summary>
        public JArray ToolCalls { get; set; }

        /// <summary>For a role="tool" result message: the id of the tool_call it answers.</summary>
        public string ToolCallId { get; set; }

        public ChatMessage(string role, string content)
        {
            Role = role;
            Content = content;
        }

        // NOTE: how a message is serialized onto the wire is the dialect's business
        // (OpenAIChatDialect.ToMessageJson) - this type is transport-format-free.
    }
}
