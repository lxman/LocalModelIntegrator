using Microsoft.VisualStudio.Shell;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LocalModelIntegrator.Options
{
    [ComVisible(true)]
    [Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567891")]
    public class GeneralOptions : DialogPage
    {
        [Category("API Configuration")]
        [DisplayName("API Protocol")]
        [Description("Select the model provider protocol. This determines how requests are built and responses parsed.")]
        [DefaultValue("OpenAI Compatible")]
        [TypeConverter(typeof(ProtocolConverter))]
        public string Protocol { get; set; } = "OpenAI Compatible";

        [Category("API Configuration")]
        [DisplayName("API URL")]
        [Description("Full API endpoint URL. Examples:\n- OpenAI: https://api.openai.com/v1/chat/completions\n- Ollama: http://localhost:11434/v1/chat/completions\n- Local vLLM: http://192.168.0.179:8000/v1/chat/completions")]
        [DefaultValue("http://localhost:11434/v1/chat/completions")]
        public string ApiUrl { get; set; } = "http://localhost:11434/v1/chat/completions";

        [Category("API Configuration")]
        [DisplayName("API Key")]
        [Description("API authentication key/token. For local servers without auth, use any dummy value.")]
        [DefaultValue("sk-dummy")]
        [PasswordPropertyText(true)]
        public string ApiKey { get; set; } = "sk-dummy";

        [Category("API Configuration")]
        [DisplayName("Model Name")]
        [Description("The model identifier sent to the API (e.g., deepseek-v4-flash, llama3.2, gpt-4o)")]
        [DefaultValue("llama3.2")]
        public string ModelName { get; set; } = "llama3.2";

        [Category("Model Parameters")]
        [DisplayName("Temperature")]
        [Description("Sampling temperature (0.0 = deterministic, 2.0 = very random)")]
        [DefaultValue(0.7)]
        public double Temperature { get; set; } = 0.7;

        [Category("Model Parameters")]
        [DisplayName("Max Tokens")]
        [Description("Maximum tokens for model responses")]
        [DefaultValue(4096)]
        public int MaxTokens { get; set; } = 4096;

        [Category("Model Parameters")]
        [DisplayName("Enable Reasoning")]
        [Description("If true and the server supports it, reasoning/thinking tokens are collected alongside the response. If false, reasoning_effort=null is sent.")]
        [DefaultValue(true)]
        public bool EnableReasoning { get; set; } = true;

        [Category("Model Parameters")]
        [DisplayName("System Prompt")]
        [Description("System prompt sent to define AI behavior")]
        [DefaultValue("You are a helpful coding assistant inside Visual Studio. Keep answers concise and accurate.")]
        public string SystemPrompt { get; set; } = "You are a helpful coding assistant inside Visual Studio. Keep answers concise and accurate.";

        [Category("Model Parameters")]
        [DisplayName("Enable Streaming")]
        [Description("If true, responses stream into the chat as they are generated (recommended). If false, the full response appears at once.")]
        [DefaultValue(true)]
        public bool EnableStreaming { get; set; } = true;

        [Category("Completions (preview)")]
        [DisplayName("Enable Inline Completions")]
        [Description("If true, the local model contributes suggestions to the IntelliSense completion list while typing C#. Off by default; local models may add latency.")]
        [DefaultValue(false)]
        public bool EnableCompletions { get; set; } = false;

        [Category("Completions (preview)")]
        [DisplayName("Completion Max Tokens")]
        [Description("Maximum tokens for an inline completion. Keep small for responsiveness.")]
        [DefaultValue(96)]
        public int CompletionMaxTokens { get; set; } = 96;

        [Category("Completions (preview)")]
        [DisplayName("Completion Temperature")]
        [Description("Sampling temperature for completions (lower = more deterministic).")]
        [DefaultValue(0.2)]
        public double CompletionTemperature { get; set; } = 0.2;

        [Category("Completions (preview)")]
        [DisplayName("Completion Debounce (ms)")]
        [Description("Delay after a keystroke before requesting a completion. Cancels in-flight requests when you keep typing.")]
        [DefaultValue(250)]
        public int CompletionDebounceMs { get; set; } = 250;

        [Category("Completions (preview)")]
        [DisplayName("Suppress Reasoning for Completions")]
        [Description("If true, completion requests ask the server to disable 'thinking' (enable_thinking=false / no_think) so a reasoning model returns code directly instead of spending the token budget reasoning. Only effective if the server honors it.")]
        [DefaultValue(true)]
        public bool SuppressCompletionReasoning { get; set; } = true;

        [Category("Conversation")]
        [DisplayName("Max History Messages")]
        [Description("Maximum messages to keep in conversation history")]
        [DefaultValue(50)]
        public int MaxHistoryMessages { get; set; } = 50;

        [Category("Conversation")]
        [DisplayName("Agentic Chat")]
        [Description("When on (default), normal chat messages run the tool-using agent so it can inspect your code on demand (and remembers the conversation). Turn off for plain single-shot chat with no tool access.")]
        [DefaultValue(true)]
        public bool AgenticChat { get; set; } = true;

        [Category("Conversation")]
        [DisplayName("Agent Max Steps")]
        [Description("Maximum tool-calling rounds for a single /agent run before it stops. Set to 0 for unlimited (stop with the Stop button). Use /continue to extend a run that hit the limit.")]
        [DefaultValue(12)]
        public int AgentMaxSteps { get; set; } = 12;

        [Category("Conversation")]
        [DisplayName("Context Window (tokens)")]
        [Description("Your model's context window, used to keep the /agent transcript from overflowing. When set, the agent elides the oldest tool results to fit. Set to 0 to disable proactive trimming (the agent still recovers from an overflow error). Examples: 8192, 32768, 131072, 1000000.")]
        [DefaultValue(0)]
        public int ContextWindowTokens { get; set; } = 0;

        [Category("Network")]
        [DisplayName("Request Timeout (ms)")]
        [Description("Request timeout in milliseconds (default: 120000 = 2 minutes). Set to 0 for no timeout — useful for slow local models; cancel with the Stop button. Completions stay capped at 15s.")]
        [DefaultValue(120000)]
        public int RequestTimeout { get; set; } = 120000;

        [Category("Diagnostics")]
        [DisplayName("Enable Logging")]
        [Description("If true, model requests/responses and agent tool decisions are written to the 'Local Model Integrator' Output pane (View > Output). Useful for debugging; the request body is logged but the API key is not.")]
        [DefaultValue(true)]
        public bool EnableLogging { get; set; } = true;

        // Hosts the user has acknowledged sending code to (semicolon-separated). Hidden from the grid
        // but persisted, so the one-time "this endpoint leaves your machine" prompt doesn't repeat.
        [Browsable(false)]
        public string AcknowledgedEndpoints { get; set; } = "";

        // Replace the default property grid with one that also carries a "Test Connection" button.
        // The hosted grid is still bound to this page, so all the settings above render and persist
        // exactly as before.
        private GeneralOptionsControl _control;

        [Browsable(false)]
        protected override System.Windows.Forms.IWin32Window Window
        {
            get
            {
                if (_control == null)
                    _control = new GeneralOptionsControl(this);
                _control.RefreshGrid();
                return _control;
            }
        }
    }

    /// <summary>
    /// Provides a dropdown of known protocols in the options grid.
    /// </summary>
    public class ProtocolConverter : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext context) => true;
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) => true;

        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
        {
            return new StandardValuesCollection(new[]
            {
                "OpenAI Compatible",
                "Ollama",
                "DeepSeek vLLM",
                "LM Studio"
            });
        }
    }
}
