using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LocalModelIntegrator.Models;
using LocalModelIntegrator.Options;
using LocalModelIntegrator.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LocalModelIntegrator.Agent
{
    public enum AgentUpdateKind { Status, ReasoningDelta, ToolUsed, Answer, Error, Notice }

    /// <summary>A single progress event emitted by the agent loop for the UI to render.</summary>
    public sealed class AgentUpdate
    {
        public AgentUpdateKind Kind { get; }
        public string Text { get; }

        public AgentUpdate(AgentUpdateKind kind, string text)
        {
            Kind = kind;
            Text = text;
        }
    }

    /// <summary>
    /// Read-only investigation agent. Drives any chat model over the plain messages-in/text-out
    /// substrate (no native tool-calling required): the model emits a text tool action, we run it
    /// against the in-proc services, feed the result back, and loop until the model answers.
    ///
    /// Designed for the lowest common denominator — a model that ignores the protocol still gets a
    /// useful answer from the preloaded orientation; a malformed call degrades to a final answer;
    /// nothing throws at the user. Everything is bounded for small context windows.
    /// </summary>
    public sealed class AgentLoop
    {
        private readonly LLMService _llm;
        private readonly GeneralOptions _options;
        private readonly SolutionFileService _files;
        private readonly RoslynContextService _roslyn;
        private readonly ModelCapabilities _caps;

        public int MaxIterations { get; set; } = 10;

        // MaxIterations <= 0 means no cap: run until the model answers or the user hits Stop.
        private bool Unlimited => MaxIterations <= 0;
        private string StepCapLabel => Unlimited ? "∞" : MaxIterations.ToString();
        public int MaxObservationChars { get; set; } = 6000;
        public int MaxListedFiles { get; set; } = 300;
        public int MaxSearchMatches { get; set; } = 80;

        public AgentLoop(LLMService llm, GeneralOptions options, SolutionFileService files,
            RoslynContextService roslyn, ModelCapabilities caps = null)
        {
            _llm = llm ?? throw new ArgumentNullException(nameof(llm));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _files = files ?? throw new ArgumentNullException(nameof(files));
            _roslyn = roslyn ?? throw new ArgumentNullException(nameof(roslyn));
            _caps = caps;
        }

        /// <summary>The running transcript - exposed so a caller can resume it via <see cref="ContinueAsync"/>.</summary>
        public List<ChatMessage> Messages { get; private set; }

        /// <summary>
        /// Builds a new conversation transcript: the capability-appropriate system prompt plus optional
        /// workspace orientation. The caller owns the list and appends user turns to it over time.
        /// </summary>
        public List<ChatMessage> NewTranscript(string orientation)
        {
            // Native tool-calling only on a positive probe signal; otherwise the universal text protocol.
            bool useNative = _caps != null && _caps.NativeTools == Tristate.Yes;

            var messages = new List<ChatMessage>
            {
                new ChatMessage("system", useNative ? NativeSystemPrompt : SystemPrompt)
            };
            if (!string.IsNullOrWhiteSpace(orientation))
                messages.Add(new ChatMessage("system", "Workspace orientation (for reference, may be partial):\n\n" + orientation));
            return messages;
        }

        /// <summary>
        /// Runs one turn of the agent over an existing transcript (the caller appends the user message
        /// first). Tools are available every turn; the model investigates as needed, then answers.
        /// </summary>
        public async Task RunTurnAsync(List<ChatMessage> messages, IProgress<AgentUpdate> progress, CancellationToken ct)
        {
            Messages = messages;
            await DriveAsync(messages, progress, ct).ConfigureAwait(false);
        }

        private async Task DriveAsync(List<ChatMessage> messages, IProgress<AgentUpdate> progress, CancellationToken ct)
        {
            bool useNative = _caps != null && _caps.NativeTools == Tristate.Yes;
            if (_options.EnableLogging)
                DiagLog.Write("agent", "mode: " + (useNative ? "native tool_calls" : "text protocol"));

            if (useNative)
                await RunNativeAsync(messages, progress, ct).ConfigureAwait(false);
            else
                await RunTextAsync(messages, progress, ct).ConfigureAwait(false);
        }

        // Text tool-protocol loop (universal fallback): the model writes a fenced ```tool block,
        // streamed so reasoning flows live into the thinking bubble; we parse the block from the content.
        private async Task RunTextAsync(List<ChatMessage> messages, IProgress<AgentUpdate> progress, CancellationToken ct)
        {
            for (int step = 1; Unlimited || step <= MaxIterations; step++)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report(new AgentUpdate(AgentUpdateKind.Status, $"Thinking… (step {step}/{StepCapLabel})"));

                string response = await TextCallAsync(messages, progress, ct).ConfigureAwait(false);
                messages.Add(new ChatMessage("assistant", response ?? string.Empty));

                ToolCall call = TryParseToolCall(response);

                if (_options.EnableLogging)
                {
                    DiagLog.Write("agent", $"--- step {step} (text) --- model said:\n{response}");
                    DiagLog.Write("agent", call == null
                        ? "→ parsed: NO tool call (treating as final answer)"
                        : $"→ parsed: tool call = {call.Verb} | arg = {call.Arg}");
                }

                if (call == null)
                {
                    progress.Report(new AgentUpdate(AgentUpdateKind.Answer, StripThink(response)));
                    progress.Report(new AgentUpdate(AgentUpdateKind.Status, string.Empty));
                    return;
                }

                string label = string.IsNullOrEmpty(call.Arg) ? call.Verb : call.Verb + " " + call.Arg;
                string observation = await SafeRunToolAsync(call, ct).ConfigureAwait(false);

                if (_options.EnableLogging)
                    DiagLog.Write("agent", $"tool result ({label}):\n{observation}");

                progress.Report(new AgentUpdate(AgentUpdateKind.ToolUsed, label));
                messages.Add(new ChatMessage("user", $"Result of `{label}`:\n\n{observation}"));
            }

            ReportStepCap(progress);
        }

        // Native tool-calling loop: used when the probe confirms the endpoint returns tool_calls.
        // Non-streaming, so tool_calls arrive complete; reasoning (if present) renders at once.
        private async Task RunNativeAsync(List<ChatMessage> messages, IProgress<AgentUpdate> progress, CancellationToken ct)
        {
            for (int step = 1; Unlimited || step <= MaxIterations; step++)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report(new AgentUpdate(AgentUpdateKind.Status, $"Thinking… (step {step}/{StepCapLabel})"));

                LlmToolTurn turn = await ToolCallAsync(messages, progress, ct).ConfigureAwait(false);

                int callCount = turn.ToolCalls?.Count ?? 0;
                if (_options.EnableLogging)
                    DiagLog.Write("agent", $"--- step {step} (native) --- tool_calls: {callCount}" +
                        (string.IsNullOrEmpty(turn.Content) ? "" : "\ncontent:\n" + turn.Content));

                if (callCount == 0)
                {
                    string answer = StripThink(turn.Content);
                    if (string.IsNullOrEmpty(answer))
                        answer = StripThink(turn.Reasoning);
                    if (string.IsNullOrEmpty(answer))
                        answer = "(the model returned no answer)";
                    progress.Report(new AgentUpdate(AgentUpdateKind.Answer, answer));
                    progress.Report(new AgentUpdate(AgentUpdateKind.Status, string.Empty));
                    return;
                }

                // Echo the assistant's tool-call turn into the conversation, then answer each call.
                messages.Add(new ChatMessage("assistant", turn.Content) { ToolCalls = turn.ToolCalls });

                try
                {
                    foreach (JToken tc in turn.ToolCalls)
                    {
                        ct.ThrowIfCancellationRequested();
                        string id = tc["id"]?.ToString();
                        string name = tc["function"]?["name"]?.ToString() ?? string.Empty;
                        string argsJson = tc["function"]?["arguments"]?.ToString();

                        var call = new ToolCall { Verb = name.ToLowerInvariant(), Arg = ExtractArg(argsJson), RawArgsJson = argsJson };
                        string label = string.IsNullOrEmpty(call.Arg) ? call.Verb : call.Verb + " " + call.Arg;

                        string observation = await SafeRunToolAsync(call, ct).ConfigureAwait(false);
                        if (_options.EnableLogging)
                            DiagLog.Write("agent", $"tool result ({label}):\n{observation}");

                        progress.Report(new AgentUpdate(AgentUpdateKind.ToolUsed, label));
                        messages.Add(new ChatMessage("tool", observation) { ToolCallId = id });
                    }
                }
                catch (OperationCanceledException)
                {
                    // Strict OpenAI-compatible servers reject a conversation in which an assistant
                    // tool_calls turn lacks a matching tool reply, so a cancel mid-step must not
                    // leave the persistent transcript malformed - answer the unanswered calls with
                    // a placeholder before unwinding, or the NEXT turn would fail with a 400.
                    BackfillCanceledToolResults(messages, turn.ToolCalls);
                    throw;
                }
            }

            ReportStepCap(progress);
        }

        // After a cancel mid-step, every tool_call the assistant opened must still get a tool reply
        // (the messages after the assistant echo are exactly this step's tool replies, so a backward
        // scan over the trailing "tool" messages finds the ones already answered).
        private static void BackfillCanceledToolResults(List<ChatMessage> messages, JArray toolCalls)
        {
            var answered = new HashSet<string>();
            for (int i = messages.Count - 1; i >= 0 && messages[i].Role == "tool"; i--)
                answered.Add(messages[i].ToolCallId ?? string.Empty);

            foreach (JToken tc in toolCalls)
            {
                string id = tc["id"]?.ToString();
                if (!answered.Contains(id ?? string.Empty))
                    messages.Add(new ChatMessage("tool", "(canceled by the user before this tool ran)") { ToolCallId = id });
            }
        }

        private async Task<string> SafeRunToolAsync(ToolCall call, CancellationToken ct)
        {
            string observation;
            try
            {
                observation = await RunToolAsync(call, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                observation = "(tool error: " + ex.Message + ")";
            }
            return Truncate(observation, MaxObservationChars);
        }

        // ---- model calls with context budgeting -------------------------------------------------

        private bool _trimNoticeShown;

        private async Task<string> TextCallAsync(List<ChatMessage> messages, IProgress<AgentUpdate> progress, CancellationToken ct)
        {
            var reasoningSink = new ActionProgress<StreamDelta>(d =>
            {
                if (d.IsReasoning && !string.IsNullOrEmpty(d.Text))
                    progress.Report(new AgentUpdate(AgentUpdateKind.ReasoningDelta, d.Text));
            });

            TrimToBudget(messages, progress);
            try
            {
                return await _llm.CallLLMStreamingAsync(messages, _options, reasoningSink, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsOverflowError(ex))
            {
                if (!EmergencyTrim(messages, progress)) throw;
                return await _llm.CallLLMStreamingAsync(messages, _options, reasoningSink, ct).ConfigureAwait(false);
            }
        }

        private async Task<LlmToolTurn> ToolCallAsync(List<ChatMessage> messages, IProgress<AgentUpdate> progress, CancellationToken ct)
        {
            // Stream when the endpoint supports SSE so reasoning flows live; else a single non-streaming call.
            bool stream = _caps == null || _caps.Streaming != Tristate.No;
            ActionProgress<StreamDelta> reasoningSink = stream
                ? new ActionProgress<StreamDelta>(d =>
                {
                    if (d.IsReasoning && !string.IsNullOrEmpty(d.Text))
                        progress.Report(new AgentUpdate(AgentUpdateKind.ReasoningDelta, d.Text));
                })
                : null;

            TrimToBudget(messages, progress);
            LlmToolTurn turn;
            try
            {
                turn = await InvokeToolsAsync(messages, stream, reasoningSink, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsOverflowError(ex))
            {
                if (!EmergencyTrim(messages, progress)) throw;
                turn = await InvokeToolsAsync(messages, stream, reasoningSink, ct).ConfigureAwait(false);
            }

            // Non-streaming returns reasoning all at once; surface it after the call.
            if (!stream && !string.IsNullOrEmpty(turn.Reasoning))
                progress.Report(new AgentUpdate(AgentUpdateKind.ReasoningDelta, turn.Reasoning));
            return turn;
        }

        private Task<LlmToolTurn> InvokeToolsAsync(List<ChatMessage> messages, bool stream, ActionProgress<StreamDelta> sink, CancellationToken ct)
            => stream
                ? _llm.CallLLMWithToolsStreamingAsync(messages, ToolsSchema, _options, sink, ct)
                : _llm.CallLLMWithToolsAsync(messages, ToolsSchema, _options, ct);

        // ---- context budget --------------------------------------------------------------------

        private const string ElidedMarker = "[elided to fit context]";

        private const int MinUsefulBudgetTokens = 4000;
        private bool _budgetTooSmallNotified;

        private int BudgetTokens()
        {
            if (_options.ContextWindowTokens <= 0)
                return 0; // proactive trimming disabled
            int budget = _options.ContextWindowTokens - _options.MaxTokens - 1024;
            // Below this floor, trimming can't even hold the current step's results and would thrash
            // (the model loses its findings and re-reads in a loop), so leave it to the reactive net.
            return budget >= MinUsefulBudgetTokens ? budget : 0;
        }

        private void TrimToBudget(List<ChatMessage> messages, IProgress<AgentUpdate> progress)
        {
            if (_options.ContextWindowTokens > 0 && BudgetTokens() == 0)
            {
                NotifyBudgetTooSmall(progress);
                return;
            }

            int budget = BudgetTokens();
            if (budget <= 0 || EstimateTokens(messages) <= budget)
                return;

            int elided = ElideOldestResults(messages, budget);
            if (elided > 0)
            {
                if (_options.EnableLogging)
                    DiagLog.Write("agent", $"context trim: elided {elided} older tool result(s) to fit ~{budget} tokens");
                NotifyTrim(progress);
            }
        }

        private bool EmergencyTrim(List<ChatMessage> messages, IProgress<AgentUpdate> progress)
        {
            int elided = 0;
            for (int i = 0; i < messages.Count - 1; i++)
            {
                ChatMessage m = messages[i];
                if (IsToolResult(m) && !IsElided(m))
                {
                    m.Content = ElidedPlaceholder(m);
                    elided++;
                }
            }
            if (elided > 0)
            {
                if (_options.EnableLogging)
                    DiagLog.Write("agent", $"context overflow: emergency-elided {elided} tool result(s), retrying");
                NotifyTrim(progress);
            }
            return elided > 0;
        }

        private static int ElideOldestResults(List<ChatMessage> messages, int budget)
        {
            int elided = 0;
            // Oldest first; never touch the last message (the just-added result the model needs now).
            for (int i = 0; i < messages.Count - 1; i++)
            {
                if (EstimateTokens(messages) <= budget)
                    break;
                ChatMessage m = messages[i];
                if (IsToolResult(m) && !IsElided(m))
                {
                    m.Content = ElidedPlaceholder(m);
                    elided++;
                }
            }
            return elided;
        }

        private static int EstimateTokens(List<ChatMessage> messages)
        {
            int chars = 0;
            foreach (ChatMessage m in messages)
            {
                chars += m.Content?.Length ?? 0;
                if (m.ToolCalls != null)
                    chars += m.ToolCalls.ToString().Length;
                chars += 16; // per-message role/formatting overhead
            }
            return (int)(chars / 3.5); // conservative (overestimates tokens) so we trim before overflowing
        }

        private static bool IsToolResult(ChatMessage m) =>
            m.Role == "tool" ||
            (m.Role == "user" && m.Content != null && m.Content.StartsWith("Result of `", StringComparison.Ordinal));

        private static bool IsElided(ChatMessage m) =>
            m.Content != null && m.Content.EndsWith(ElidedMarker, StringComparison.Ordinal);

        private static string ElidedPlaceholder(ChatMessage m)
        {
            string firstLine = m.Content?.Split('\n').FirstOrDefault() ?? string.Empty;
            if (firstLine.Length > 80)
                firstLine = firstLine.Substring(0, 80);
            return string.IsNullOrEmpty(firstLine) ? ElidedMarker : firstLine + " " + ElidedMarker;
        }

        private static bool IsOverflowError(Exception ex)
        {
            string m = ex?.Message ?? string.Empty;
            if (m.IndexOf("context", StringComparison.OrdinalIgnoreCase) >= 0 &&
                (m.IndexOf("length", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 m.IndexOf("maximum", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 m.IndexOf("exceed", StringComparison.OrdinalIgnoreCase) >= 0))
                return true;
            return m.IndexOf("too many tokens", StringComparison.OrdinalIgnoreCase) >= 0
                || m.IndexOf("maximum context length", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void NotifyTrim(IProgress<AgentUpdate> progress)
        {
            if (_trimNoticeShown)
                return;
            _trimNoticeShown = true;
            progress.Report(new AgentUpdate(AgentUpdateKind.Notice,
                "Context is getting large — eliding older tool results to fit the window. " +
                "Set \"Context Window (tokens)\" in options to your model's size for smoother trimming."));
        }

        private void NotifyBudgetTooSmall(IProgress<AgentUpdate> progress)
        {
            if (_budgetTooSmallNotified)
                return;
            _budgetTooSmallNotified = true;
            progress.Report(new AgentUpdate(AgentUpdateKind.Notice,
                $"Context Window ({_options.ContextWindowTokens}) is too small after reserving Max Tokens ({_options.MaxTokens}) " +
                "— proactive trimming is off (it would thrash). Raise Context Window or lower Max Tokens."));
        }

        private static void ReportStepCap(IProgress<AgentUpdate> progress)
        {
            progress.Report(new AgentUpdate(AgentUpdateKind.Status, string.Empty));
            progress.Report(new AgentUpdate(AgentUpdateKind.Error,
                "Stopped at the step limit without a final answer. Type /continue to keep going, or raise \"Agent Max Steps\" in options."));
        }

        // Pulls the single argument value from a tool_call's JSON arguments (each tool takes one string
        // param), tolerant of how the param is named.
        private static readonly Regex ReadRange = new Regex(@"^(.*\S)[\s:](\d+)\s*-\s*(\d+)\s*$", RegexOptions.Compiled);

        // Pulls (id, startLine, endLine) for a read call. Native uses structured args (id/startLine/endLine);
        // text protocol accepts "<id> <a>-<b>" or "<id>:<a>-<b>" or just "<id>".
        private static (string id, int start, int end) ParseReadArgs(ToolCall call)
        {
            if (!string.IsNullOrEmpty(call.RawArgsJson))
            {
                try
                {
                    JObject o = JObject.Parse(call.RawArgsJson);
                    string id = o["id"]?.ToString() ?? o["path"]?.ToString() ?? FirstStringValue(o) ?? string.Empty;
                    int s = (int?)o["startLine"] ?? (int?)o["start"] ?? 0;
                    int e = (int?)o["endLine"] ?? (int?)o["end"] ?? 0;
                    return (id, s, e);
                }
                catch { /* fall through to text parsing */ }
            }

            string arg = call.Arg ?? string.Empty;
            Match m = ReadRange.Match(arg);
            if (m.Success && int.TryParse(m.Groups[2].Value, out int ts) && int.TryParse(m.Groups[3].Value, out int te))
                return (m.Groups[1].Value.Trim(), ts, te);
            return (arg.Trim(), 0, 0);
        }

        private static string FirstStringValue(JObject o)
        {
            foreach (JProperty p in o.Properties())
            {
                string v = p.Value?.ToString();
                if (!string.IsNullOrEmpty(v))
                    return v;
            }
            return null;
        }

        private static string ExtractArg(string argumentsJson)
        {
            if (string.IsNullOrWhiteSpace(argumentsJson))
                return string.Empty;
            try
            {
                foreach (JProperty p in JObject.Parse(argumentsJson).Properties())
                {
                    string v = p.Value?.ToString();
                    if (!string.IsNullOrEmpty(v))
                        return v;
                }
                return string.Empty;
            }
            catch
            {
                return argumentsJson.Trim();
            }
        }

        // ---- tool execution ----------------------------------------------------------------

        private async Task<string> RunToolAsync(ToolCall call, CancellationToken ct)
        {
            switch (call.Verb)
            {
                case "list":
                    return FormatList();

                case "outline":
                    return await _files.GetOutlineAsync(call.Arg, ct).ConfigureAwait(false);

                case "read":
                {
                    (string id, int startLine, int endLine) = ParseReadArgs(call);
                    return await _files.ReadFileAsync(id, startLine, endLine, ct).ConfigureAwait(false);
                }

                case "read_symbol":
                case "readsymbol":
                    return await _roslyn.ReadSymbolAsync(call.Arg, ct).ConfigureAwait(false);

                case "grep":
                case "search":
                {
                    string q = call.Arg ?? string.Empty;
                    bool rx = false;
                    if (q.StartsWith("re:", StringComparison.OrdinalIgnoreCase)) { rx = true; q = q.Substring(3); }
                    try
                    {
                        return FormatSearch(q, await _files.SearchContentAsync(q, rx, ct, MaxSearchMatches).ConfigureAwait(false));
                    }
                    catch (ArgumentException ex)
                    {
                        return "(invalid regex: " + ex.Message + ")";
                    }
                }

                case "refs":
                case "usages":
                case "find_references":
                    return FormatRefs(await _roslyn.FindReferencesAsync(call.Arg, ct).ConfigureAwait(false));

                case "symbol":
                case "def":
                case "find_symbol":
                    return FormatSymbols(call.Arg, await _roslyn.FindSymbolAsync(call.Arg, ct).ConfigureAwait(false));

                default:
                    return "(unknown tool '" + call.Verb +
                           "'. Available: list, outline <id>, read <id>, grep <text>, refs <Symbol>, symbol <Name>)";
            }
        }

        private string FormatList()
        {
            IReadOnlyList<SolutionFileInfo> files = _files.ListFiles();
            if (files.Count == 0)
                return "(no files — is a solution open?)";

            var sb = new StringBuilder($"{files.Count} files:\n");
            int n = 0;
            foreach (SolutionFileInfo f in files)
            {
                sb.AppendLine(f.DisplayPath);
                if (++n >= MaxListedFiles)
                {
                    sb.AppendLine($"... ({files.Count - MaxListedFiles} more — use grep/refs/symbol to find specific files)");
                    break;
                }
            }
            return sb.ToString().TrimEnd();
        }

        private static string FormatSearch(string query, IReadOnlyList<SearchMatch> matches)
        {
            if (matches.Count == 0)
                return $"(no matches for \"{query}\")";

            var sb = new StringBuilder($"{matches.Count} match(es):\n");
            foreach (SearchMatch m in matches)
                sb.AppendLine($"{m.DisplayPath}:{m.Line}: {m.Text}");
            return sb.ToString().TrimEnd();
        }

        private string FormatRefs(FindReferencesResult r)
        {
            if (!string.IsNullOrEmpty(r.Note))
                return r.Note;

            var sb = new StringBuilder();
            sb.AppendLine("Matched: " + string.Join(", ", r.MatchedSymbols));
            sb.AppendLine($"{r.Hits.Count} location(s)" + (r.Truncated ? " (truncated)" : "") + ":");
            foreach (ReferenceHit h in r.Hits)
                sb.AppendLine($"{(h.IsDefinition ? "[def] " : "      ")}{_files.ToDisplayPath(h.FilePath)}:{h.Line}: {h.Snippet}");
            return sb.ToString().TrimEnd();
        }

        private string FormatSymbols(string name, List<SymbolHit> hits)
        {
            if (hits.Count == 0)
                return $"(no C#/VB symbol named '{name}' found)";

            var sb = new StringBuilder($"{hits.Count} declaration(s):\n");
            foreach (SymbolHit h in hits)
                sb.AppendLine($"{h.Description}  ({_files.ToDisplayPath(h.FilePath)}:{h.Line})");
            return sb.ToString().TrimEnd();
        }

        // ---- parsing -----------------------------------------------------------------------

        private sealed class ToolCall
        {
            public string Verb;
            public string Arg;
            public string RawArgsJson;   // native tool_calls: function.arguments JSON (multi-field tools like read)
        }

        // Synchronous IProgress adapter: forwards on the caller's thread (the outer Progress<AgentUpdate>
        // does the UI marshaling), which preserves reasoning-delta order.
        private sealed class ActionProgress<T> : IProgress<T>
        {
            private readonly Action<T> _action;
            public ActionProgress(Action<T> action) { _action = action; }
            public void Report(T value) => _action(value);
        }

        private static readonly Regex ThinkBlock =
            new Regex(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        private static readonly Regex FencedTool =
            new Regex(@"```(?:tool|action|call)[ \t]*\r?\n(.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        private static readonly Regex UnfencedTool =
            new Regex(@"^[ \t]*(?:TOOL|ACTION)[ \t]*[:=][ \t]*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

        private static readonly Regex FunctionStyle =
            new Regex(@"^(\w+)\s*\((.*)\)\s*$", RegexOptions.Singleline);

        private static readonly HashSet<string> KnownVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "list", "outline", "read", "grep", "search", "refs", "usages", "find_references", "symbol", "def", "find_symbol", "read_symbol", "readsymbol"
        };

        private static ToolCall TryParseToolCall(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            text = StripThink(text);

            Match fenced = FencedTool.Match(text);
            if (fenced.Success)
                return ParseCommand(FirstNonEmptyLine(fenced.Groups[1].Value)); // explicit fence: accept even unknown verbs

            Match unfenced = UnfencedTool.Match(text);
            if (unfenced.Success)
            {
                ToolCall c = ParseCommand(unfenced.Groups[1].Value.Trim());
                if (c != null && KnownVerbs.Contains(c.Verb)) // unfenced: require a known verb to avoid prose false positives
                    return c;
            }
            return null;
        }

        private static ToolCall ParseCommand(string cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd))
                return null;
            cmd = cmd.Trim();

            Match fn = FunctionStyle.Match(cmd);
            if (fn.Success)
                return new ToolCall { Verb = fn.Groups[1].Value.ToLowerInvariant(), Arg = Unquote(fn.Groups[2].Value.Trim()) };

            int sp = cmd.IndexOfAny(new[] { ' ', '\t' });
            if (sp < 0)
                return new ToolCall { Verb = cmd.ToLowerInvariant(), Arg = string.Empty };

            return new ToolCall
            {
                Verb = cmd.Substring(0, sp).ToLowerInvariant(),
                Arg = Unquote(cmd.Substring(sp + 1).Trim())
            };
        }

        private static string FirstNonEmptyLine(string body)
        {
            if (body == null)
                return string.Empty;
            foreach (string line in body.Split('\n'))
            {
                string t = line.Trim();
                if (t.Length > 0)
                    return t;
            }
            return string.Empty;
        }

        private static string Unquote(string s)
        {
            if (string.IsNullOrEmpty(s))
                return s;
            s = s.Trim();
            if (s.Length >= 2)
            {
                char a = s[0], b = s[s.Length - 1];
                if ((a == '"' && b == '"') || (a == '\'' && b == '\'') || (a == '`' && b == '`'))
                    return s.Substring(1, s.Length - 2).Trim();
            }
            return s;
        }

        private static string StripThink(string s)
        {
            if (string.IsNullOrEmpty(s))
                return string.Empty;
            s = ThinkBlock.Replace(s, string.Empty);
            int open = s.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
            if (open >= 0) // unclosed reasoning block → drop everything from it on
                s = s.Substring(0, open);
            return s.Trim();
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max)
                return s;
            return s.Substring(0, max) +
                   $"\n… (truncated, {s.Length - max} more chars — narrow with outline/grep, or read a specific file)";
        }

        private const string NativeSystemPrompt =
            "You are a coding assistant working inside Visual Studio with live, read-only access to the " +
            "user's open solution via tools (list, outline, read, grep, refs, symbol, read_symbol). Answer " +
            "general or conceptual questions directly. But for anything about the user's ACTUAL code - what " +
            "exists, where something is used, how their code works - you MUST use the tools to check; never " +
            "guess or rely on training data about similarly-named projects. Choose the tightest tool: to " +
            "show a specific member or type use read_symbol; to inspect a region, use grep/refs to get the " +
            "line number then read with startLine/endLine; use outline for a file's shape; only read a whole " +
            "file when you truly need all of it. Use ids exactly as the tools report them. Never invent file " +
            "contents, symbols, paths, or line numbers - call a tool instead. All tools are limited to " +
            "the open solution; files outside it are refused, so do not try absolute paths or locations " +
            "outside the project. The tools also see ONLY the solution's own source - not referenced NuGet " +
            "packages, the .NET framework, or other external assemblies - so if grep/symbol/refs cannot find " +
            "a type or member, do NOT conclude it is undefined or will not compile; it may be defined in a " +
            "referenced library you cannot see. When you have enough information, answer in plain prose.";

        // OpenAI function-tool schemas for the native path, built once. Each tool takes one string
        // arg. The OpenAI encoding is the dialect contract for DialectRequest.ToolsJson today;
        // when a dialect with a different tool encoding lands, this moves behind IModelDialect (D3).
        private static readonly string ToolsSchema = BuildToolsSchema();

        private static string BuildToolsSchema()
        {
            var tools = new JArray
            {
                Fn("list", "List the solution's files (ids look like \"Project/Folder/File.cs\").", null, null),
                Fn("outline", "Types and member signatures of a C#/VB file (no bodies).", "id", "Project-scoped file id, as reported by list/refs."),
                ReadFn(),
                Fn("grep", "Search file contents across the solution. Prefix the query with re: for a regex.", "query", "Text, or re:regex, to find."),
                Fn("refs", "Find all references to a C#/VB symbol across the solution.", "symbol", "A C#/VB symbol name (simple or Type.Member)."),
                Fn("symbol", "Find where a C#/VB symbol is declared.", "name", "A C#/VB symbol name."),
                Fn("read_symbol", "Read the source of a single C#/VB member or type by name (just that declaration, not the whole file).", "name", "A C#/VB symbol name (simple or Type.Member).")
            };
            return tools.ToString(Formatting.None);
        }

        private static JObject ReadFn()
        {
            var properties = new JObject
            {
                ["id"] = new JObject { ["type"] = "string", ["description"] = "Project-scoped file id, as reported by list/refs." },
                ["startLine"] = new JObject { ["type"] = "integer", ["description"] = "Optional 1-based first line. Omit to read from the start." },
                ["endLine"] = new JObject { ["type"] = "integer", ["description"] = "Optional 1-based last line. Omit to read to the end." }
            };
            return new JObject
            {
                ["type"] = "function",
                ["function"] = new JObject
                {
                    ["name"] = "read",
                    ["description"] = "Read a file's contents. Pass startLine/endLine to read only a section (use the line numbers from grep/refs); omit them to read the whole file.",
                    ["parameters"] = new JObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JArray { "id" } }
                }
            };
        }

        private static JObject Fn(string name, string description, string param, string paramDescription)
        {
            var properties = new JObject();
            var required = new JArray();
            if (param != null)
            {
                properties[param] = new JObject { ["type"] = "string", ["description"] = paramDescription };
                required.Add(param);
            }
            return new JObject
            {
                ["type"] = "function",
                ["function"] = new JObject
                {
                    ["name"] = name,
                    ["description"] = description,
                    ["parameters"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = properties,
                        ["required"] = required
                    }
                }
            };
        }

        private const string SystemPrompt =
            "You are a coding assistant working inside Visual Studio with live, read-only access to the user's open " +
            "solution. Answer general or conceptual questions directly. For anything about the user's ACTUAL code " +
            "(what exists, where something is used, how it works), investigate with the tools below instead of " +
            "guessing - never rely on training data about similarly-named projects. Call tools one at a time.\n\n" +
            "To call a tool, reply with ONLY this block and nothing else:\n" +
            "```tool\n<verb> <argument>\n```\n\n" +
            "Tools:\n" +
            "  list              list the solution's files (ids look like \"Project/Folder/File.cs\")\n" +
            "  outline <id>      types and member signatures of a C#/VB file (no bodies)\n" +
            "  read <id> [a-b]   full text of a file, or only lines a-b (e.g. read Proj/Foo.cs 40-80)\n" +
            "  grep <text>       search file contents; prefix the text with re: for a regex\n" +
            "  refs <Symbol>     exact references to a C#/VB symbol across the solution\n" +
            "  symbol <Name>     where a C#/VB symbol is declared\n" +
            "  read_symbol <Name>  source of one C#/VB member or type (just that declaration)\n\n" +
            "Choose the tightest tool: read_symbol for a specific member or type; grep/refs to get a line number " +
            "then read with a line range for a region; outline for a file's shape; only read a whole file when you " +
            "truly need all of it. Use ids exactly as list/grep/refs report them. After each tool call I reply with " +
            "the result, then you call another tool or answer.\n\n" +
            "When you have enough information, reply with your final answer as plain prose and NO tool block. " +
            "Never invent file contents, symbols, paths, or line numbers — if unsure, use a tool. " +
            "All tools are limited to the open solution; files outside it are refused, so do not try " +
            "absolute paths or locations outside the project. The tools also see ONLY the solution's own " +
            "source - not referenced NuGet packages, the .NET framework, or other external assemblies - so " +
            "if a type or member is not found, do NOT conclude it is undefined or will not compile; it may " +
            "be defined in a referenced library you cannot see.";
    }
}
