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

        public object ToRequestBody()
        {
            // Plain text turn (the common case) keeps the minimal shape.
            if (ToolCalls == null && ToolCallId == null)
                return new { role = Role, content = Content ?? "" };

            // Native tool-calling turns need the richer OpenAI shape. An assistant turn that carries
            // tool_calls may legitimately have null content; a tool result carries tool_call_id.
            var o = new JObject { ["role"] = Role, ["content"] = Content };
            if (ToolCalls != null)
                o["tool_calls"] = ToolCalls;
            if (ToolCallId != null)
                o["tool_call_id"] = ToolCallId;
            return o;
        }
    }
}
