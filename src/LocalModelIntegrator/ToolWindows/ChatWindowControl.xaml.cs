using LocalModelIntegrator.Agent;
using LocalModelIntegrator.Models;
using LocalModelIntegrator.Options;
using LocalModelIntegrator.Services;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Task = System.Threading.Tasks.Task;

namespace LocalModelIntegrator.ToolWindows
{
    public partial class ChatWindowControl : UserControl
    {
        private readonly ObservableCollection<MessageDisplay> _displayMessages = new ObservableCollection<MessageDisplay>();
        private readonly List<ChatMessage> _chatMessages = new List<ChatMessage>();
        private readonly LLMService _llmService = new LLMService();
        private WorkspaceService _workspaceService;
        private RoslynContextService _roslynService;
        private SolutionFileService _solutionFileService;
        private bool _contextEnabled = true;
        private ModelCapabilities _lastCaps;
        private List<ChatMessage> _agentChat;   // persistent agentic-chat transcript (tools + memory)
        // Context queued for the agent transcript (e.g. "Send File to Chat") - drained at the start
        // of the next agent turn, after the transcript has been (re)built, so it survives the lazy
        // first-turn construction and the stale-transcript drop on a solution change.
        private readonly List<ChatMessage> _pendingAgentContext = new List<ChatMessage>();
        private CancellationTokenSource _activeCts;
        private bool _noSolutionNoticeShown;
        private string _agentChatSolutionId;
        private readonly List<string> _promptHistory = new List<string>();
        private int _historyIndex;          // == _promptHistory.Count means "the live draft"
        private string _historyDraft;       // in-progress text saved when history navigation starts

        public ChatWindowControl()
        {
            InitializeComponent();
            MessagesContainer.ItemsSource = _displayMessages;
            ResetConversation();

            // Wire up events (done in code to avoid XAML resolution issues)
            ClearButton.Click += ClearButton_Click;
            SendButton.Click += SendButton_Click;
            StopButton.Click += StopButton_Click;
            InputTextBox.PreviewKeyDown += InputTextBox_PreviewKeyDown;
            EndpointAckButton.Click += EndpointAckButton_Click;
            EndpointSettingsButton.Click += EndpointSettingsButton_Click;
            EndpointChip.MouseLeftButtonUp += (s, e) => OpenOptions();
            UpdateEndpointChip();
        }

        private GeneralOptions GetOptions()
        {
            var package = LocalModelIntegratorPackage.Instance;
            return (GeneralOptions)package.GetDialogPage(typeof(GeneralOptions));
        }

        private WorkspaceService GetWorkspaceService()
        {
            if (_workspaceService != null) return _workspaceService;
            var package = LocalModelIntegratorPackage.Instance;
            _workspaceService = new WorkspaceService(package);
            return _workspaceService;
        }

        private RoslynContextService GetRoslynService()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_roslynService != null) return _roslynService;
            var componentModel = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
            _roslynService = componentModel?.GetService<RoslynContextService>();
            return _roslynService;
        }

        private SolutionFileService GetSolutionFileService()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_solutionFileService != null) return _solutionFileService;
            var componentModel = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
            _solutionFileService = componentModel?.GetService<SolutionFileService>();
            return _solutionFileService;
        }

        // True when a solution is open (the model's code-access gate). UI-thread bound via the MEF
        // service; false if the service isn't composed yet, which also means no solution is loaded.
        private bool IsSolutionAvailable()
        {
            try { return GetSolutionFileService()?.IsSolutionOpen() ?? false; }
            catch { return false; }
        }

        private void NotifyNoSolutionOnce()
        {
            if (_noSolutionNoticeShown) return;
            _noSolutionNoticeShown = true;
            AppendMessage("notice", "No solution is open, so the model has no access to your code - " +
                "answers are general only. Open a solution to enable code-aware chat and tools.");
        }

        // Injected as a system turn when no solution is open, so a tool-less model is told plainly
        // that it cannot see the user's code and must not pretend otherwise.
        private const string NoSolutionSystemNote =
            "IMPORTANT: No Visual Studio solution is currently open. You have NO access to the user's " +
            "code, files, or project this turn, and no investigation tools are available. Answer only " +
            "from general knowledge. Do NOT invent file names, paths, symbols, or code, and do NOT claim " +
            "to have looked at the user's project. If the question needs their actual code, tell them to " +
            "open the solution first.";

        // ---- endpoint disclosure (where the code you send is delivered) ----------------------------

        private static readonly Brush _dotGreen = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
        private static readonly Brush _dotAmber = new SolidColorBrush(Color.FromRgb(0xD2, 0x99, 0x22));
        private static readonly Brush _dotRed = new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49));
        private static readonly Brush _dotGray = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

        // Refresh the always-on chip from the current API URL: a colored dot for the tier
        // (green = this machine, amber = LAN, red = internet) plus the host and http/https.
        private void UpdateEndpointChip()
        {
            EndpointTrust t = EndpointTrust.Classify(GetOptions().ApiUrl);
            if (t == null)
            {
                EndpointDot.Fill = _dotGray;
                EndpointChipText.Text = "no endpoint configured";
                return;
            }
            switch (t.Level)
            {
                case EndpointLevel.Local:
                    EndpointDot.Fill = _dotGreen;
                    EndpointChipText.Text = $"{t.Host} · on this machine";
                    break;
                case EndpointLevel.ExternalSecure:
                    EndpointDot.Fill = _dotAmber;
                    EndpointChipText.Text = $"{t.Host} · external · 🔒 https";
                    break;
                default:
                    EndpointDot.Fill = _dotRed;
                    EndpointChipText.Text = $"{t.Host} · external · ⚠ http";
                    break;
            }
        }

        private static HashSet<string> AckSet(GeneralOptions o) =>
            new HashSet<string>(
                (o.AcknowledgedEndpoints ?? string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase);

        // Acks are keyed scheme://host (not bare host) so a downgrade from https to http on the
        // same host re-prompts once - the banner's encryption claim changed. Bare-host entries
        // recorded by older builds simply never match and get re-acknowledged once.
        private static string AckKey(EndpointTrust t) => t.Scheme + "://" + t.Host;

        private bool NeedsEndpointAck()
        {
            GeneralOptions o = GetOptions();
            EndpointTrust t = EndpointTrust.Classify(o.ApiUrl);
            return t != null && t.NeedsAck && !AckSet(o).Contains(AckKey(t));
        }

        private void RecordEndpointAck()
        {
            GeneralOptions o = GetOptions();
            EndpointTrust t = EndpointTrust.Classify(o.ApiUrl);
            if (t == null) return;
            HashSet<string> set = AckSet(o);
            set.Add(AckKey(t));
            o.AcknowledgedEndpoints = string.Join(";", set);
            o.SaveSettingsToStorage();
        }

        private void ShowEndpointBanner()
        {
            EndpointTrust t = EndpointTrust.Classify(GetOptions().ApiUrl);
            if (t == null) { EndpointBanner.Visibility = Visibility.Collapsed; return; }
            string msg = $"Code and files you send are shared with {t.Host} (off this machine).";
            if (t.IsHttp)
                msg += " This connection is unencrypted (http).";
            EndpointBannerText.Text = msg;
            EndpointBanner.Visibility = Visibility.Visible;
        }

        private string EndpointSummary()
        {
            EndpointTrust t = EndpointTrust.Classify(GetOptions().ApiUrl);
            return t?.Summary ?? "Endpoint: (no valid API URL configured).";
        }

        private void OpenOptions()
        {
            try { LocalModelIntegratorPackage.Instance?.ShowOptions(); } catch { /* best-effort */ }
        }

        private void EndpointAckButton_Click(object sender, RoutedEventArgs e)
        {
            RecordEndpointAck();
            EndpointBanner.Visibility = Visibility.Collapsed;
            UpdateEndpointChip();
            // Resume a normal chat send if a message is still queued in the box.
            if (!string.IsNullOrWhiteSpace(InputTextBox.Text))
                _ = SendMessageAsync();
        }

        private void EndpointSettingsButton_Click(object sender, RoutedEventArgs e) => OpenOptions();

        private void ResetConversation()
        {
            _chatMessages.Clear();
            _agentChat = null;
            _pendingAgentContext.Clear();
            GeneralOptions options = GetOptions();
            _chatMessages.Add(new ChatMessage("system", options.SystemPrompt));
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.DataContext is MessageDisplay msg)
            {
                try
                {
                    Clipboard.SetText(msg.Content);
                }
                catch (System.Runtime.InteropServices.ExternalException)
                {
                    // Another process has the clipboard locked (common over RDP and with
                    // clipboard managers) - don't let the click crash VS.
                    SetStatus("Copy failed — clipboard is busy; try again.");
                }
            }
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e) => ClearConversation();

        /// <summary>
        /// Clears the chat: cancels any in-flight request first (so a running turn cannot keep
        /// streaming into the emptied display), then resets the display and both transcripts.
        /// Shared by the Clear button and the "Clear Conversation" menu command.
        /// </summary>
        public void ClearConversation()
        {
            try { _activeCts?.Cancel(); }
            catch (ObjectDisposedException) { /* request completed between the click and the cancel */ }
            _displayMessages.Clear();
            ResetConversation();
        }

        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            RememberPrompt();
            _ = SendMessageAsync();
        }

        private void InputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+Up / Ctrl+Down recall previously-sent prompts. Plain Up/Down are left alone so they
            // still move the caret within a multi-line entry (PreviewKeyDown tunnels before the TextBox).
            if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key == Key.Up || e.Key == Key.Down))
            {
                NavigateHistory(older: e.Key == Key.Up);
                e.Handled = true;
                return;
            }

            // Plain Enter sends; Shift+Enter falls through and inserts a newline.
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                RememberPrompt();
                e.Handled = true;
                _ = SendMessageAsync();
            }
        }

        // ---- prompt history (Ctrl+Up older / Ctrl+Down newer) -------------------------------------

        // Records the text about to be sent so it can be recalled later. Only the box-submit paths
        // (Enter / Send button) call this, so programmatic prompts (editor actions) don't pollute it.
        private void RememberPrompt()
        {
            string text = InputTextBox.Text?.Trim();
            if (!string.IsNullOrEmpty(text) &&
                (_promptHistory.Count == 0 ||
                 !string.Equals(_promptHistory[_promptHistory.Count - 1], text, StringComparison.Ordinal)))
            {
                _promptHistory.Add(text);
                const int max = 100;
                if (_promptHistory.Count > max)
                    _promptHistory.RemoveRange(0, _promptHistory.Count - max);
            }
            _historyIndex = _promptHistory.Count; // reset to the live draft
            _historyDraft = null;
        }

        private void NavigateHistory(bool older)
        {
            if (_promptHistory.Count == 0)
                return;

            if (older)
            {
                if (_historyIndex == _promptHistory.Count)
                {
                    _historyDraft = InputTextBox.Text;          // leaving the draft - save it
                    _historyIndex = _promptHistory.Count - 1;
                }
                else if (_historyIndex > 0)
                {
                    _historyIndex--;
                }
                else
                {
                    return; // already at the oldest entry
                }
                SetInputText(_promptHistory[_historyIndex]);
            }
            else
            {
                if (_historyIndex >= _promptHistory.Count)
                    return; // already at the live draft
                if (_historyIndex < _promptHistory.Count - 1)
                {
                    _historyIndex++;
                    SetInputText(_promptHistory[_historyIndex]);
                }
                else
                {
                    _historyIndex = _promptHistory.Count;        // stepped past the newest - restore draft
                    SetInputText(_historyDraft ?? string.Empty);
                    _historyDraft = null;
                }
            }
        }

        private void SetInputText(string text)
        {
            InputTextBox.Text = text ?? string.Empty;
            InputTextBox.CaretIndex = InputTextBox.Text.Length;
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_activeCts != null)
                {
                    _activeCts.Cancel();
                    SetStatus("Canceling…");
                }
            }
            catch (ObjectDisposedException)
            {
                // The request completed (and its CTS was disposed) between the click and the cancel.
            }
        }

        private async Task SendMessageAsync()
        {
            string text = InputTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(text))
                return;

            // One request at a time. The input box is disabled while a request runs, but
            // programmatic senders (editor actions, the Acknowledge button) can still get here
            // mid-run; overlapping runs would fight over _activeCts and interleave the transcripts.
            if (_activeCts != null)
            {
                AppendMessage("notice", "A request is already running — wait for it to finish or press Stop.");
                return;
            }

            UpdateEndpointChip();

            // Endpoint acknowledgment gate for normal chat. The banner waits for the user; the text
            // stays in the box so Acknowledge can resume the send. Slash commands gate themselves.
            if (!text.StartsWith("/") && NeedsEndpointAck())
            {
                ShowEndpointBanner();
                return;
            }

            InputTextBox.Text = string.Empty;
            AppendMessage("user", text);

            // Handle slash commands
            if (text.StartsWith("/"))
            {
                await HandleCommandAsync(text);
                return;
            }

            // The model is gated on an open solution. With one, agentic chat (tools) is the default;
            // without one, the model has NO code access - fall through to plain chat with an explicit
            // "no access" system note so it cannot pretend to have inspected the project.
            bool solutionOpen = IsSolutionAvailable();
            if (solutionOpen)
            {
                _noSolutionNoticeShown = false;
                if (GetOptions().AgenticChat)
                {
                    await RunAgentTurnAsync(text);
                    return;
                }
            }
            else
            {
                NotifyNoSolutionOnce();
            }

            _chatMessages.Add(new ChatMessage("user", text));

            MessageDisplay reasoningMessage = null;
            MessageDisplay answerMessage = null;
            try
            {
                SetStatus("Thinking...");
                SetBusy(true);
                _activeCts = new CancellationTokenSource();

                GeneralOptions options = GetOptions();

                // Trim persisted history.
                List<ChatMessage> trimmed = _llmService.TrimMessageHistory(_chatMessages, options.MaxHistoryMessages);
                _chatMessages.Clear();
                _chatMessages.AddRange(trimmed);

                // Build the send payload with optional auto-context. The context message is
                // NOT persisted to history, so it is regenerated fresh each turn.
                var payload = new List<ChatMessage>(_chatMessages);
                if (!solutionOpen)
                {
                    int insertAt = System.Math.Max(0, payload.Count - 1);
                    payload.Insert(insertAt, new ChatMessage("system", NoSolutionSystemNote));
                }
                else if (_contextEnabled)
                {
                    string context = await GatherContextAsync();
                    if (!string.IsNullOrWhiteSpace(context))
                    {
                        int insertAt = System.Math.Max(0, payload.Count - 1);
                        payload.Insert(insertAt, new ChatMessage("system",
                            "Current Visual Studio context (for reference; may be partial):\n\n" + context));
                    }
                }

                string response;
                if (options.EnableStreaming)
                {
                    SetStatus("Streaming...");

                    var progress = new Progress<StreamDelta>(d =>
                    {
                        if (d.IsReasoning)
                        {
                            if (reasoningMessage == null)
                            {
                                reasoningMessage = new MessageDisplay { Role = "thinking", Content = string.Empty };
                                _displayMessages.Add(reasoningMessage);
                            }
                            reasoningMessage.Content += d.Text;
                        }
                        else
                        {
                            if (answerMessage == null)
                            {
                                answerMessage = new MessageDisplay { Role = "assistant", Content = string.Empty };
                                _displayMessages.Add(answerMessage);
                            }
                            answerMessage.Content += d.Text;
                        }
                        MessagesScrollViewer.ScrollToEnd();
                    });

                    response = await _llmService.CallLLMStreamingAsync(payload, options, progress, _activeCts.Token);

                    if (answerMessage != null)
                    {
                        answerMessage.Content = response;
                    }
                    else
                    {
                        // No content arrived, so the response fell back to the reasoning channel -
                        // the thinking bubble already shows this exact text; drop the bubble rather
                        // than display the same text twice (mirrors the agent path's de-dupe).
                        if (reasoningMessage != null && reasoningMessage.Content?.Trim() == response)
                        {
                            _displayMessages.Remove(reasoningMessage);
                            reasoningMessage = null;
                        }
                        AppendMessage("assistant", response);
                    }
                }
                else
                {
                    response = await _llmService.CallLLMAsync(payload, options, _activeCts.Token);
                    AppendMessage("assistant", response);
                }

                _chatMessages.Add(new ChatMessage("assistant", response));

                // NOTE: the model used to be able to auto-create files from ```file blocks in its reply
                // (behind a single Yes/No prompt, no diff). That unreviewed write path is removed - all
                // model-driven file changes will go through the Phase B diff-and-approve gate.

                SetStatus("");
            }
            catch (OperationCanceledException)
            {
                if (answerMessage != null && string.IsNullOrEmpty(answerMessage.Content))
                    _displayMessages.Remove(answerMessage);
                if (reasoningMessage != null && string.IsNullOrEmpty(reasoningMessage.Content))
                    _displayMessages.Remove(reasoningMessage);
                AppendMessage("system", "Request canceled.");
                SetStatus("");
            }
            catch (Exception ex)
            {
                if (answerMessage != null && string.IsNullOrEmpty(answerMessage.Content))
                    _displayMessages.Remove(answerMessage);
                if (reasoningMessage != null && string.IsNullOrEmpty(reasoningMessage.Content))
                    _displayMessages.Remove(reasoningMessage);
                AppendMessage("error", $"Error: {ex.Message}");
                SetStatus("");
            }
            finally
            {
                _activeCts?.Dispose();
                _activeCts = null;
                SetBusy(false);
            }
        }

        /// <summary>
        /// Submits a prompt programmatically (used by the editor context-menu AI actions):
        /// displays it and runs it through the normal streamed send pipeline.
        /// </summary>
        public async Task SubmitPromptAsync(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt))
                return;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            InputTextBox.Text = prompt;
            await SendMessageAsync();
        }

        /// <summary>
        /// Loads a solution file's current content into the conversation as reference context
        /// (used by the "Send File to Chat" menu command). The content joins the plain-chat
        /// history immediately and is queued for the agent transcript, which drains the queue
        /// at the start of the next agent turn - so it reaches the model on either path.
        /// </summary>
        public async Task SendFileToChatAsync(string filePath)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            SolutionFileService svc = GetSolutionFileService();
            if (svc == null || !svc.IsSolutionOpen())
            {
                AppendMessage("notice", "No solution is open - open a solution to send its files to chat.");
                return;
            }
            if (!svc.IsInScope(filePath))
            {
                AppendMessage("notice", "That file isn't part of the open solution, so it can't be sent to the model.");
                return;
            }

            string content = await svc.ReadFileAsync(filePath, 0, 0, CancellationToken.None);
            string display = svc.ToDisplayPath(filePath);

            var note = new ChatMessage("user",
                $"For reference, here is the current content of \"{display}\":\n\n{content}");
            _chatMessages.Add(note);
            _pendingAgentContext.Add(note);

            AppendMessage("system", $"File \"{display}\" added to the conversation context.");
        }

        /// <summary>
        /// Gathers lightweight context for the current turn: solution summary plus the
        /// Roslyn semantic outline of the active file. Returns an empty string if nothing
        /// is available (no solution / no active file / Roslyn not ready).
        /// </summary>
        private async System.Threading.Tasks.Task<string> GatherContextAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var sb = new StringBuilder();

            try
            {
                WorkspaceInfo info = await GetWorkspaceService().GetWorkspaceInfoAsync();
                if (info != null && !string.IsNullOrEmpty(info.Name))
                    sb.AppendLine($"Solution: {info.Name} ({info.ProjectCount} project(s){(info.HasGit ? ", git" : "")})");
            }
            catch { /* solution info is best-effort */ }

            string activeFile = GetWorkspaceService().GetActiveFilePath();
            if (!string.IsNullOrEmpty(activeFile))
            {
                SolutionFileService files = GetSolutionFileService();
                if (files != null && files.IsInScope(activeFile))
                {
                    sb.AppendLine($"Active file: {activeFile}");

                    RoslynContextService roslyn = GetRoslynService();
                    if (roslyn != null)
                    {
                        try
                        {
                            string outline = await roslyn.GetActiveFileContextAsync(activeFile, CancellationToken.None);
                            if (!string.IsNullOrWhiteSpace(outline))
                            {
                                sb.AppendLine();
                                sb.AppendLine(outline);
                            }
                        }
                        catch { /* semantic outline is best-effort */ }
                    }
                }
                else
                {
                    // Active file is outside the open solution - acknowledge it exists, but never
                    // leak its path or contents to the model.
                    sb.AppendLine("A file outside the open solution is active in the editor; it cannot be read.");
                }
            }

            return sb.ToString().TrimEnd();
        }

        private async Task HandleCommandAsync(string command)
        {
            try
            {
                string cmdLine = command.Substring(1).Trim();
                string[] parts = cmdLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) return;

                string cmd = parts[0].ToLower();
                string[] args = parts.Skip(1).ToArray();

                switch (cmd)
                {
                    case "read":
                        await CommandReadFileAsync(args);
                        break;
                    case "list":
                        await CommandListFilesAsync(args);
                        break;
                    case "search":
                        await CommandSearchFilesAsync(args);
                        break;
                    case "workspace":
                    case "info":
                        await CommandWorkspaceInfoAsync();
                        break;
                    case "context":
                        await CommandContextAsync(args);
                        break;
                    case "solfiles":
                        await CommandSolutionFilesAsync();
                        break;
                    case "outline":
                        await CommandOutlineAsync(args);
                        break;
                    case "readsol":
                        await CommandReadSolutionAsync(args);
                        break;
                    case "grep":
                        await CommandGrepAsync(args);
                        break;
                    case "refs":
                    case "usages":
                        await CommandFindReferencesAsync(args);
                        break;
                    case "symbol":
                    case "def":
                        await CommandFindSymbolAsync(args);
                        break;
                    case "readsym":
                        await CommandReadSymbolAsync(args);
                        break;
                    case "agent":
                        await CommandAgentAsync(args);
                        break;
                    case "continue":
                        await CommandAgentContinueAsync();
                        break;
                    case "test":
                        await CommandTestConnectionAsync();
                        break;
                    case "caps":
                        CommandCaps();
                        break;
                    case "help":
                        CommandHelp();
                        break;
                    default:
                        AppendMessage("system", $"Unknown command: /{cmd}\nType /help for available commands.");
                        break;
                }
            }
            catch (Exception ex)
            {
                AppendMessage("error", $"Command error: {ex.Message}");
            }
        }

        private async Task CommandReadFileAsync(string[] args)
        {
            if (args.Length == 0)
            {
                AppendMessage("system", "Usage: /read <file-path>\nExample: /read src/Program.cs");
                return;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            SolutionFileService svc = GetSolutionFileService();
            if (svc == null || !svc.IsSolutionOpen())
            {
                AppendMessage("system", "No solution is open - open a solution to read its files.");
                return;
            }

            // Route through the scoped, solution-membership reader (same boundary the agent uses)
            // rather than the old solution-directory disk read.
            string filePath = string.Join(" ", args);
            string content = await svc.ReadFileAsync(filePath, 0, 0, System.Threading.CancellationToken.None);

            AppendMessage("system", $"File \"{filePath}\":\n\n{content}");
            var note = new ChatMessage("user",
                $"I'm showing you the content of file \"{filePath}\":\n\n{content}");
            _chatMessages.Add(note);
            // Agentic chat (the default) reads _agentChat, not _chatMessages - queue the content
            // for it too, so /read reaches the model on either path.
            _pendingAgentContext.Add(note);
        }

        private async Task CommandListFilesAsync(string[] args)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            SolutionFileService svc = GetSolutionFileService();
            if (svc == null || !svc.IsSolutionOpen())
            {
                AppendMessage("system", "No solution is open.");
                return;
            }

            // Solution membership only (no disk walk, no absolute paths) - optionally filtered by substring.
            string filter = args.Length > 0 ? string.Join(" ", args).Replace('\\', '/') : null;
            List<SolutionFileInfo> files = svc.ListFiles()
                .Where(f => filter == null || f.DisplayPath.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            if (files.Count == 0)
            {
                AppendMessage("system", filter == null ? "No files in the solution." : $"No solution files matching \"{filter}\".");
                return;
            }

            var sb = new StringBuilder($"Solution files{(filter == null ? "" : $" matching \"{filter}\"")} ({files.Count}):\n\n");
            foreach (SolutionFileInfo f in files.Take(200))
                sb.AppendLine(f.DisplayPath);
            if (files.Count > 200)
                sb.AppendLine($"... and {files.Count - 200} more");
            AppendMessage("system", sb.ToString());
        }

        private async Task CommandSearchFilesAsync(string[] args)
        {
            if (args.Length == 0)
            {
                AppendMessage("system", "Usage: /search <pattern>\nExample: /search *.cs");
                return;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            SolutionFileService svc = GetSolutionFileService();
            if (svc == null || !svc.IsSolutionOpen())
            {
                AppendMessage("system", "No solution is open.");
                return;
            }

            // Name search over the solution's own files only (project-scoped ids, no disk walk).
            string pattern = string.Join(" ", args).Trim('*').Replace('\\', '/');
            List<SolutionFileInfo> matching = svc.ListFiles()
                .Where(f => f.DisplayPath.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                .Take(50)
                .ToList();

            if (matching.Count == 0)
            {
                AppendMessage("system", $"No solution files matching \"{pattern}\".");
                return;
            }

            var sb = new StringBuilder($"Solution files matching \"{pattern}\" ({matching.Count}):\n\n");
            foreach (SolutionFileInfo f in matching)
                sb.AppendLine(f.DisplayPath);
            AppendMessage("system", sb.ToString());
        }

        private async Task CommandWorkspaceInfoAsync()
        {
            WorkspaceInfo info = await GetWorkspaceService().GetWorkspaceInfoAsync();

            string output = $"Workspace: {info.Name}\n" +
                            $"Path: {info.Path}\n" +
                            $"Git: {(info.HasGit ? "yes" : "no")}\n" +
                            $"Projects: {info.ProjectCount}";

            AppendMessage("system", output);
        }

        private async Task CommandContextAsync(string[] args)
        {
            if (args.Length > 0)
            {
                string toggle = args[0].ToLowerInvariant();
                if (toggle == "on" || toggle == "off")
                {
                    _contextEnabled = toggle == "on";
                    AppendMessage("system", $"Automatic file/solution context: {(_contextEnabled ? "ON" : "OFF")}");
                    return;
                }
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            string activeFile = GetWorkspaceService().GetActiveFilePath();
            if (string.IsNullOrEmpty(activeFile))
            {
                AppendMessage("system", "No active document open.");
                return;
            }

            RoslynContextService roslyn = GetRoslynService();
            if (roslyn == null)
            {
                AppendMessage("error", "Roslyn context service is not available (MEF component not composed).");
                return;
            }

            string context = await roslyn.GetActiveFileContextAsync(activeFile, System.Threading.CancellationToken.None);
            AppendMessage("system", $"Semantic context for {System.IO.Path.GetFileName(activeFile)}:\n\n{context}");
        }

        private async Task CommandSolutionFilesAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            SolutionFileService svc = GetSolutionFileService();
            if (svc == null)
            {
                AppendMessage("error", "Solution file service is not available (MEF not composed / no solution).");
                return;
            }

            System.Collections.Generic.IReadOnlyList<SolutionFileInfo> files = svc.ListFiles();
            if (files.Count == 0)
            {
                AppendMessage("system", "No solution files found (is a solution open?).");
                return;
            }

            var sb = new StringBuilder($"Solution Explorer files ({files.Count}, \"Show All Files\" off):\n\n");
            foreach (SolutionFileInfo f in files.Take(200))
                sb.AppendLine($"{(f.IsCode ? "[cs]" : "[ ]")} {f.DisplayPath}");
            if (files.Count > 200)
                sb.AppendLine($"... and {files.Count - 200} more");

            AppendMessage("system", sb.ToString());
        }

        private async Task CommandOutlineAsync(string[] args)
        {
            if (args.Length == 0)
            {
                AppendMessage("system", "Usage: /outline <path>");
                return;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            SolutionFileService svc = GetSolutionFileService();
            if (svc == null)
            {
                AppendMessage("error", "Solution file service is not available.");
                return;
            }

            string path = string.Join(" ", args);
            string outline = await svc.GetOutlineAsync(path, System.Threading.CancellationToken.None);
            AppendMessage("system", $"Outline of {path}:\n\n{outline}");
        }

        private async Task CommandReadSolutionAsync(string[] args)
        {
            if (args.Length == 0)
            {
                AppendMessage("system", "Usage: /readsol <path> [start-end]   (fresh read; optional line range, e.g. /readsol Foo.cs 40-80)");
                return;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            SolutionFileService svc = GetSolutionFileService();
            if (svc == null)
            {
                AppendMessage("error", "Solution file service is not available.");
                return;
            }

            string joined = string.Join(" ", args);
            string path = joined;
            int start = 0, end = 0;
            System.Text.RegularExpressions.Match range =
                System.Text.RegularExpressions.Regex.Match(joined, @"^(.*\S)[\s:](\d+)\s*-\s*(\d+)\s*$");
            if (range.Success)
            {
                path = range.Groups[1].Value.Trim();
                int.TryParse(range.Groups[2].Value, out start);
                int.TryParse(range.Groups[3].Value, out end);
            }

            string content = await svc.ReadFileAsync(path, start, end, System.Threading.CancellationToken.None);
            AppendMessage("system", $"{path} (fresh read):\n\n{content}");
        }

        private async Task CommandGrepAsync(string[] args)
        {
            if (args.Length == 0)
            {
                AppendMessage("system", "Usage: /grep <text>   (prefix with re: for regex, e.g. /grep re:Order\\w+)");
                return;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            SolutionFileService svc = GetSolutionFileService();
            if (svc == null)
            {
                AppendMessage("error", "Solution file service is not available.");
                return;
            }

            string raw = string.Join(" ", args);
            bool useRegex = false;
            string query = raw;
            if (raw.StartsWith("re:", StringComparison.OrdinalIgnoreCase))
            {
                useRegex = true;
                query = raw.Substring(3);
            }

            try
            {
                System.Collections.Generic.IReadOnlyList<SearchMatch> matches =
                    await svc.SearchContentAsync(query, useRegex, System.Threading.CancellationToken.None);
                if (matches.Count == 0)
                {
                    AppendMessage("system", $"No matches for \"{query}\".");
                    return;
                }

                var sb = new StringBuilder($"{matches.Count} match(es) for \"{query}\":\n\n");
                foreach (SearchMatch m in matches)
                    sb.AppendLine($"{m.DisplayPath}:{m.Line}: {m.Text}");
                AppendMessage("system", sb.ToString());
            }
            catch (ArgumentException ex)
            {
                AppendMessage("error", "Invalid regex: " + ex.Message);
            }
        }

        private async Task CommandFindReferencesAsync(string[] args)
        {
            if (args.Length == 0)
            {
                AppendMessage("system", "Usage: /refs <SymbolName>");
                return;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            RoslynContextService roslyn = GetRoslynService();
            SolutionFileService files = GetSolutionFileService();
            if (roslyn == null)
            {
                AppendMessage("error", "Roslyn context service is not available.");
                return;
            }

            FindReferencesResult result =
                await roslyn.FindReferencesAsync(args[0], System.Threading.CancellationToken.None);
            if (!string.IsNullOrEmpty(result.Note))
            {
                AppendMessage("system", result.Note);
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("Matched: " + string.Join(", ", result.MatchedSymbols));
            sb.AppendLine($"{result.Hits.Count} location(s)" + (result.Truncated ? " (truncated)" : "") + ":");
            sb.AppendLine();
            foreach (ReferenceHit h in result.Hits)
            {
                string disp = files != null ? files.ToDisplayPath(h.FilePath) : h.FilePath;
                sb.AppendLine($"{(h.IsDefinition ? "[def] " : "      ")}{disp}:{h.Line}: {h.Snippet}");
            }
            AppendMessage("system", sb.ToString());
        }

        private async Task CommandFindSymbolAsync(string[] args)
        {
            if (args.Length == 0)
            {
                AppendMessage("system", "Usage: /symbol <Name>");
                return;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            RoslynContextService roslyn = GetRoslynService();
            SolutionFileService files = GetSolutionFileService();
            if (roslyn == null)
            {
                AppendMessage("error", "Roslyn context service is not available.");
                return;
            }

            System.Collections.Generic.List<SymbolHit> hits =
                await roslyn.FindSymbolAsync(args[0], System.Threading.CancellationToken.None);
            if (hits.Count == 0)
            {
                AppendMessage("system", $"No C#/VB symbol named '{args[0]}' found.");
                return;
            }

            var sb = new StringBuilder($"{hits.Count} declaration(s):\n\n");
            foreach (SymbolHit h in hits)
            {
                string disp = files != null ? files.ToDisplayPath(h.FilePath) : h.FilePath;
                sb.AppendLine(h.Description);
                sb.AppendLine("    " + disp + ":" + h.Line);
            }
            AppendMessage("system", sb.ToString());
        }

        private async Task CommandReadSymbolAsync(string[] args)
        {
            if (args.Length == 0)
            {
                AppendMessage("system", "Usage: /readsym <Name>   (source of one C#/VB member or type)");
                return;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            RoslynContextService roslyn = GetRoslynService();
            if (roslyn == null)
            {
                AppendMessage("error", "Roslyn context service is not available.");
                return;
            }

            string code = await roslyn.ReadSymbolAsync(args[0], System.Threading.CancellationToken.None);
            AppendMessage("system", code);
        }

        private async Task CommandAgentAsync(string[] args)
        {
            if (args.Length == 0)
            {
                AppendMessage("system", "Usage: /agent <goal or question>   (chat is already agentic; this is the same thing)");
                return;
            }
            await RunAgentTurnAsync(string.Join(" ", args));
        }

        private async Task CommandAgentContinueAsync()
        {
            if (_agentChat == null || _agentChat.Count == 0)
            {
                AppendMessage("system", "Nothing to continue yet. Ask a question first.");
                return;
            }
            await RunAgentTurnAsync("Continue the task. Use more tools only if you still need information, then give your final answer.");
        }

        // Runs one agentic chat turn over the persistent transcript: the model can call tools to inspect
        // the code, remembers prior turns, and streams reasoning. Shared by normal chat, /agent and /continue.
        private async Task RunAgentTurnAsync(string userMessage)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            SolutionFileService files = GetSolutionFileService();
            RoslynContextService roslyn = GetRoslynService();
            if (files == null || roslyn == null)
            {
                AppendMessage("error", "Agent services are not available (no solution open / MEF not composed).");
                return;
            }
            if (!files.IsSolutionOpen())
            {
                AppendMessage("notice", "No solution is open, so the agent's code tools are unavailable. " +
                    "Open a solution to investigate code.");
                return;
            }

            if (NeedsEndpointAck())
            {
                ShowEndpointBanner();
                AppendMessage("notice", "Acknowledge the endpoint shown above, then run your request again.");
                return;
            }

            GeneralOptions options = GetOptions();

            // Drop a stale transcript if the solution changed/closed since it was built, so another
            // solution's orientation and findings don't bleed into this one.
            string solutionId = files.CurrentSolutionId();
            if (_agentChat != null && !string.Equals(solutionId, _agentChatSolutionId, StringComparison.OrdinalIgnoreCase))
            {
                _agentChat = null;
                _displayMessages.Add(new MessageDisplay { Role = "notice", Content = "Solution changed — starting a fresh agent conversation." });
            }

            // Per-step UI state: reasoning streams into one collapsible bubble until a tool call or
            // the final answer ends the step; then the next step starts a fresh bubble.
            MessageDisplay reasoning = null;
            var progress = new Progress<AgentUpdate>(u =>
            {
                switch (u.Kind)
                {
                    case AgentUpdateKind.Status:
                        SetStatus(u.Text);
                        break;

                    case AgentUpdateKind.ReasoningDelta:
                        if (reasoning == null)
                        {
                            reasoning = new MessageDisplay { Role = "thinking", Content = string.Empty, Header = "💭 Thinking…", IsActive = true };
                            _displayMessages.Add(reasoning);
                        }
                        reasoning.Content += u.Text;
                        reasoning.Header = "💭 " + Peek(reasoning.Content);
                        break;

                    case AgentUpdateKind.ToolUsed:
                        if (reasoning != null) reasoning.IsActive = false;
                        reasoning = null;
                        _displayMessages.Add(new MessageDisplay { Role = "tool", Content = u.Text });
                        break;

                    case AgentUpdateKind.Answer:
                        // If the model emitted no content and we fell back to the reasoning channel,
                        // the answer IS the reasoning we just streamed into the bubble - remove the
                        // duplicate so it doesn't appear both as "thinking" and as the answer.
                        if (reasoning != null)
                        {
                            reasoning.IsActive = false;
                            if (!string.IsNullOrEmpty(reasoning.Content) && reasoning.Content.Trim() == u.Text)
                                _displayMessages.Remove(reasoning);
                        }
                        reasoning = null;
                        _displayMessages.Add(new MessageDisplay { Role = "assistant", Content = u.Text });
                        break;

                    case AgentUpdateKind.Error:
                        if (reasoning != null) reasoning.IsActive = false;
                        reasoning = null;
                        _displayMessages.Add(new MessageDisplay { Role = "error", Content = u.Text });
                        break;

                    case AgentUpdateKind.Notice:
                        _displayMessages.Add(new MessageDisplay { Role = "notice", Content = u.Text });
                        break;
                }
                MessagesScrollViewer.ScrollToEnd();
            });

            try
            {
                SetBusy(true);
                _activeCts = new CancellationTokenSource();

                // Probe once per config so the agent can use native tool-calling when the endpoint
                // supports it. Only a SUCCESSFUL probe counts as cached: a failed one (server down
                // or mid-restart, or the user pressed Stop) is probed again on the next message, so
                // a transient failure cannot disable agent chat for the rest of the session.
                if (_lastCaps == null || !_lastCaps.Chat ||
                    !_lastCaps.MatchesConfig(options.ApiUrl, options.ModelName, options.Protocol))
                {
                    SetStatus("Checking endpoint capabilities…");
                    try { _lastCaps = await CapabilityProbe.ProbeAsync(options, _activeCts.Token); }
                    catch (OperationCanceledException) { throw; }
                    catch { _lastCaps = null; }
                }
                if (_lastCaps == null || !_lastCaps.Chat)
                {
                    AppendMessage("error", _lastCaps?.Report
                        ?? "Could not check the endpoint. Verify the API URL in options, or run /test.");
                    return;
                }

                var loop = new AgentLoop(_llmService, options, files, roslyn, _lastCaps)
                {
                    MaxIterations = options.AgentMaxSteps
                };

                if (_agentChat == null)
                {
                    string orientation = null;
                    try { orientation = await GatherContextAsync(); }
                    catch { /* orientation is best-effort */ }
                    _agentChat = loop.NewTranscript(orientation);
                    _agentChatSolutionId = solutionId;
                }
                else
                {
                    // Keep the model aware of which file is open now (it may have changed between
                    // turns), but never leak the path of a file outside the open solution.
                    string active = GetWorkspaceService().GetActiveFilePath();
                    if (!string.IsNullOrEmpty(active))
                        _agentChat.Add(files.IsInScope(active)
                            ? new ChatMessage("system", "User's current active file: " + active)
                            : new ChatMessage("system", "A file outside the open solution is active in the editor; it cannot be read."));
                }

                // Context queued before the transcript existed, or while it belonged to another
                // solution (e.g. "Send File to Chat"), joins the conversation ahead of this message.
                if (_pendingAgentContext.Count > 0)
                {
                    _agentChat.AddRange(_pendingAgentContext);
                    _pendingAgentContext.Clear();
                }

                _agentChat.Add(new ChatMessage("user", userMessage));

                SetStatus(_lastCaps != null && _lastCaps.NativeTools == Tristate.Yes
                    ? "Agent running (native tools)…"
                    : "Agent running…");
                await loop.RunTurnAsync(_agentChat, progress, _activeCts.Token);
            }
            catch (OperationCanceledException)
            {
                AppendMessage("system", "Request canceled.");
            }
            catch (Exception ex)
            {
                if (options.EnableLogging)
                    DiagLog.Write("agent", "ERROR: " + ex);
                AppendMessage("error", "Agent error: " + ex.Message);
            }
            finally
            {
                if (reasoning != null) reasoning.IsActive = false;
                _activeCts?.Dispose();
                _activeCts = null;
                SetBusy(false);
                SetStatus("");
            }
        }

        // First line of the reasoning, capped - shown as the collapsed thinking-bubble caption.
        private static string Peek(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "Thinking…";
            string s = text.TrimStart();
            int nl = s.IndexOfAny(new[] { '\r', '\n' });
            if (nl >= 0)
                s = s.Substring(0, nl);
            s = s.Trim();
            const int max = 90;
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        private async Task CommandTestConnectionAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            GeneralOptions options = GetOptions();
            AppendMessage("system", $"Testing connection to {options.ApiUrl} …");

            try
            {
                SetBusy(true);
                SetStatus("Testing connection…");
                ModelCapabilities caps = await CapabilityProbe.ProbeAsync(options, CancellationToken.None);
                _lastCaps = caps;
                AppendMessage(caps.Chat ? "system" : "error", caps.Report);
                AppendMessage("system", EndpointSummary());
            }
            catch (Exception ex)
            {
                AppendMessage("error", "Test failed: " + ex.Message);
            }
            finally
            {
                SetBusy(false);
                SetStatus("");
            }
        }

        private void CommandCaps()
        {
            if (_lastCaps == null)
            {
                AppendMessage("system", "No capability probe yet this session. Click Test or run /test.");
                return;
            }
            AppendMessage("system", _lastCaps.Report + "\n\n" + EndpointSummary());
        }

        private void CommandHelp()
        {
            const string help = "Commands:\n\n" +
                                "  /read <path>     - Read a solution file into context\n" +
                                "  /readsol <path> [a-b] - Read solution file (fresh; optional line range)\n" +
                                "  /outline <path>  - Structural outline of a code file\n" +
                                "  /solfiles        - List files in the solution\n" +
                                "  /grep <text>     - Search file contents (prefix re: for regex)\n" +
                                "  /refs <Symbol>   - Roslyn: find all references to a C#/VB symbol\n" +
                                "  /symbol <Name>   - Roslyn: find where a C#/VB symbol is declared\n" +
                                "  /readsym <Name>  - Roslyn: read the source of one C#/VB member/type\n" +
                                "  /agent <goal>    - Let the model investigate the solution using the tools above\n" +
                                "  /continue        - Continue the last /agent run (e.g. after it hit the step limit)\n" +
                                "  /test            - Probe the endpoint for connectivity and capabilities\n" +
                                "  /caps            - Show the last capability probe result\n" +
                                "  /list [filter]   - List solution files (optionally filtered)\n" +
                                "  /search <name>   - Find solution files by name\n" +
                                "  /workspace       - Show workspace info\n" +
                                "  /context         - Show Roslyn semantic outline of the active file\n" +
                                "  /context on|off  - Toggle auto-injecting file/solution context (default on)\n" +
                                "  /help            - This help\n\n" +
                                "Enter sends message. Shift+Enter for new line.";

            AppendMessage("system", help);
        }

        public void AppendMessage(string role, string content)
        {
            _displayMessages.Add(new MessageDisplay { Role = role, Content = content });

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                MessagesScrollViewer.ScrollToEnd();
            });
        }

        private void SetStatus(string text)
        {
            StatusText.Text = text;
        }

        // Busy = a request is in flight: input and Send are off, Stop is on. (Deliberately a
        // method, not a `new IsEnabled` property - hiding UIElement.IsEnabled meant anything
        // addressing the control as a UIElement bypassed it.)
        private void SetBusy(bool busy)
        {
            InputTextBox.IsEnabled = !busy;
            SendButton.IsEnabled = !busy;
            StopButton.IsEnabled = busy;    // Stop is available exactly while a request is running
        }
    }
}
