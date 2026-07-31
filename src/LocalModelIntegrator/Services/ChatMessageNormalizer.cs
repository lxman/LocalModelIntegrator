using System.Collections.Generic;
using System.Linq;
using LocalModelIntegrator.Models;

namespace LocalModelIntegrator.Services
{
    /// <summary>
    /// Send-time payload normalization. Strict chat templates (Qwen among them) reject any
    /// request whose system message is not the single first message ("System message must be
    /// at the beginning"); permissive ones (Llama) don't care. Coalescing every system
    /// message into one leading system message satisfies both, so it is applied to every
    /// outgoing request regardless of backend.
    /// </summary>
    public static class ChatMessageNormalizer
    {
        /// <summary>
        /// Returns a payload with all system messages merged (in order of appearance, joined
        /// by a blank line) into a single system message at index 0. Non-system messages keep
        /// their relative order and identity. The input list is never mutated - callers
        /// persist it as conversation history.
        /// </summary>
        public static List<ChatMessage> CoalesceSystemMessages(List<ChatMessage> messages)
        {
            if (messages == null)
                return null;

            List<string> systemParts = messages
                .Where(m => m.Role == "system" && !string.IsNullOrWhiteSpace(m.Content))
                .Select(m => m.Content)
                .ToList();

            // Already canonical (or nothing to do): single non-blank system at index 0, no others.
            int systemCount = messages.Count(m => m.Role == "system");
            if (systemCount == 0 ||
                (systemCount == 1 && systemParts.Count == 1 && messages[0].Role == "system"))
                return messages;

            var result = new List<ChatMessage>(messages.Count);
            if (systemParts.Count > 0)
                result.Add(new ChatMessage("system", string.Join("\n\n", systemParts)));
            result.AddRange(messages.Where(m => m.Role != "system"));
            return result;
        }
    }
}
