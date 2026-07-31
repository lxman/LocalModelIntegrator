using System.Collections.Generic;
using System.Linq;
using LocalModelIntegrator.Models;
using LocalModelIntegrator.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace LocalModelIntegrator.Tests
{
    // Strict chat templates (Qwen among them) reject any request whose system message is
    // not the single first message ("System message must be at the beginning"). The
    // normalizer coalesces every system message into one leading system message.
    public class ChatMessageNormalizerTests
    {
        [Fact]
        public void MidConversationSystemMessageIsMergedIntoTheLeadingOne()
        {
            var messages = new List<ChatMessage>
            {
                new ChatMessage("system", "Base prompt."),
                new ChatMessage("user", "First question"),
                new ChatMessage("assistant", "First answer"),
                new ChatMessage("system", "Current Visual Studio context: Foo.cs"),
                new ChatMessage("user", "Second question"),
            };

            List<ChatMessage> result = ChatMessageNormalizer.CoalesceSystemMessages(messages);

            Assert.Equal(4, result.Count);
            Assert.Equal("system", result[0].Role);
            Assert.Equal("Base prompt.\n\nCurrent Visual Studio context: Foo.cs", result[0].Content);
            Assert.Equal(new[] { "user", "assistant", "user" }, result.Skip(1).Select(m => m.Role));
            Assert.Equal(new[] { "First question", "First answer", "Second question" },
                result.Skip(1).Select(m => m.Content));
        }

        [Fact]
        public void TwoLeadingSystemMessagesBecomeOne()
        {
            var messages = new List<ChatMessage>
            {
                new ChatMessage("system", "Agent prompt."),
                new ChatMessage("system", "Workspace orientation: bar"),
                new ChatMessage("user", "Go"),
            };

            List<ChatMessage> result = ChatMessageNormalizer.CoalesceSystemMessages(messages);

            Assert.Equal(2, result.Count);
            Assert.Equal("system", result[0].Role);
            Assert.Equal("Agent prompt.\n\nWorkspace orientation: bar", result[0].Content);
            Assert.Equal("user", result[1].Role);
        }

        [Fact]
        public void SingleLeadingSystemMessageIsLeftAlone()
        {
            var messages = new List<ChatMessage>
            {
                new ChatMessage("system", "Prompt"),
                new ChatMessage("user", "Hi"),
                new ChatMessage("assistant", "Hello"),
            };

            List<ChatMessage> result = ChatMessageNormalizer.CoalesceSystemMessages(messages);

            Assert.Equal(3, result.Count);
            Assert.Equal(new[] { "system", "user", "assistant" }, result.Select(m => m.Role));
            Assert.Equal(new[] { "Prompt", "Hi", "Hello" }, result.Select(m => m.Content));
        }

        [Fact]
        public void NoSystemMessagesMeansNoChange()
        {
            var messages = new List<ChatMessage>
            {
                new ChatMessage("user", "Hi"),
                new ChatMessage("assistant", "Hello"),
            };

            List<ChatMessage> result = ChatMessageNormalizer.CoalesceSystemMessages(messages);

            Assert.Equal(new[] { "user", "assistant" }, result.Select(m => m.Role));
        }

        [Fact]
        public void InputListAndItsMessagesAreNotMutated()
        {
            var trailingSystem = new ChatMessage("system", "Context note");
            var messages = new List<ChatMessage>
            {
                new ChatMessage("system", "Base"),
                new ChatMessage("user", "Q"),
                trailingSystem,
            };

            ChatMessageNormalizer.CoalesceSystemMessages(messages);

            // Callers persist this list as conversation history; normalization is send-time only.
            Assert.Equal(3, messages.Count);
            Assert.Equal("Base", messages[0].Content);
            Assert.Equal("Context note", trailingSystem.Content);
            Assert.Same(trailingSystem, messages[2]);
        }

        [Fact]
        public void ToolCallMetadataOnNonSystemMessagesIsPreserved()
        {
            var assistant = new ChatMessage("assistant", null)
            {
                ToolCalls = JArray.Parse("[{\"id\":\"call_1\"}]")
            };
            var toolResult = new ChatMessage("tool", "result") { ToolCallId = "call_1" };
            var messages = new List<ChatMessage>
            {
                new ChatMessage("system", "Base"),
                new ChatMessage("user", "Q"),
                assistant,
                toolResult,
                new ChatMessage("system", "Active file: Foo.cs"),
                new ChatMessage("user", "Next"),
            };

            List<ChatMessage> result = ChatMessageNormalizer.CoalesceSystemMessages(messages);

            Assert.Same(assistant, result[2]);
            Assert.Same(toolResult, result[3]);
            Assert.Equal("call_1", result[3].ToolCallId);
            Assert.NotNull(result[2].ToolCalls);
        }

        [Fact]
        public void BlankSystemMessagesAreDroppedNotJoined()
        {
            var messages = new List<ChatMessage>
            {
                new ChatMessage("system", "Base"),
                new ChatMessage("user", "Q"),
                new ChatMessage("system", "   "),
            };

            List<ChatMessage> result = ChatMessageNormalizer.CoalesceSystemMessages(messages);

            Assert.Equal(2, result.Count);
            Assert.Equal("Base", result[0].Content);
        }
    }
}
