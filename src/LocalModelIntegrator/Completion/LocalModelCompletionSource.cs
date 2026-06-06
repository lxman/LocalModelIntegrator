using System;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using LocalModelIntegrator.Options;
using LocalModelIntegrator.Services;
using Microsoft.VisualStudio.Core.Imaging;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace LocalModelIntegrator.Completion
{
    /// <summary>
    /// MEF factory that supplies the local-model completion source for C# editors.
    /// </summary>
    [Export(typeof(IAsyncCompletionSourceProvider))]
    [Name("Local Model AI Completion")]
    [ContentType("CSharp")]
    [Order(After = "Roslyn")]
    internal sealed class LocalModelCompletionSourceProvider : IAsyncCompletionSourceProvider
    {
        [Import(AllowDefault = true)]
        internal RoslynContextService RoslynContext { get; set; }

        public IAsyncCompletionSource GetOrCreate(ITextView textView)
        {
            return textView.Properties.GetOrCreateSingletonProperty(
                () => new LocalModelCompletionSource(RoslynContext));
        }
    }

    /// <summary>
    /// Contributes local-model suggestions to the IntelliSense completion list. Participates only
    /// when a completion session already exists (driven by Roslyn), so it never triggers a session
    /// on its own. Off unless "Enable Inline Completions" is set in options.
    /// </summary>
    internal sealed class LocalModelCompletionSource : IAsyncCompletionSource
    {
        private const int PrefixChars = 2000;
        private const int SuffixChars = 500;

        private readonly RoslynContextService _roslyn;
        private readonly LLMService _llm = new LLMService();
        private readonly ImageElement _icon =
            new ImageElement(new ImageId(KnownMonikers.CommentGroup.Guid, KnownMonikers.CommentGroup.Id));

        private GeneralOptions _options;

        public LocalModelCompletionSource(RoslynContextService roslyn)
        {
            _roslyn = roslyn;
        }

        public CompletionStartData InitializeCompletion(CompletionTrigger trigger, SnapshotPoint triggerLocation, CancellationToken token)
        {
            _options = TryGetOptions();
            if (_options == null || !_options.EnableCompletions)
                return CompletionStartData.DoesNotParticipateInCompletion;

            // Provide items (and an applicable span) so the editor actually queries us.
            // NOTE: ParticipatesInCompletionIfAny did not get this source invoked in VS 18.
            // Also note: ReSharper, when active, owns the C# completion list and bypasses
            // VS async-completion sources, so this only shows when ReSharper defers to VS.
            SnapshotSpan applicableSpan = GetApplicableSpan(triggerLocation);
            return new CompletionStartData(CompletionParticipation.ProvidesItems, applicableSpan);
        }

        public async Task<CompletionContext> GetCompletionContextAsync(
            IAsyncCompletionSession session,
            CompletionTrigger trigger,
            SnapshotPoint triggerLocation,
            SnapshotSpan applicableToSpan,
            CancellationToken token)
        {
            GeneralOptions options = _options;
            if (options == null || !options.EnableCompletions)
                return CompletionContext.Empty;

            // No solution open => the model has no code access this session; never send buffer text out.
            if (_roslyn == null || !_roslyn.HasSolution)
                return CompletionContext.Empty;

            // Only complete inside files that belong to the open solution - never send a loose or
            // out-of-scope file's text to the model.
            string docPath = TryGetBufferPath(triggerLocation.Snapshot.TextBuffer);
            if (string.IsNullOrEmpty(docPath) || !_roslyn.IsSolutionDocument(docPath))
                return CompletionContext.Empty;

            try
            {
                // Debounce; if the user keeps typing the session is superseded and the token cancels.
                if (options.CompletionDebounceMs > 0)
                    await Task.Delay(options.CompletionDebounceMs, token).ConfigureAwait(false);

                ITextSnapshot snapshot = triggerLocation.Snapshot;
                int caret = triggerLocation.Position;
                string prefix = ReadBack(snapshot, caret, PrefixChars);
                string suffix = ReadForward(snapshot, caret, SuffixChars);

                string semanticContext = await GetSemanticContextAsync(snapshot.TextBuffer, token).ConfigureAwait(false);

                string completion = await _llm.CompleteCodeAsync(
                    prefix, suffix, semanticContext, options,
                    options.CompletionMaxTokens, options.CompletionTemperature, token).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(completion))
                    return CompletionContext.Empty;

                string typed = applicableToSpan.GetText();
                return BuildContext(completion, typed);
            }
            catch (OperationCanceledException)
            {
                return CompletionContext.Empty;
            }
            catch
            {
                return CompletionContext.Empty;
            }
        }

        public Task<object> GetDescriptionAsync(IAsyncCompletionSession session, CompletionItem item, CancellationToken token)
        {
            return Task.FromResult<object>("Local model suggestion:\n\n" + item.InsertText);
        }

        private CompletionContext BuildContext(string completion, string typed)
        {
            ImmutableArray<CompletionItem>.Builder builder = ImmutableArray.CreateBuilder<CompletionItem>();

            string firstLine = completion;
            int nl = completion.IndexOfAny(new[] { '\r', '\n' });
            if (nl >= 0)
                firstLine = completion.Substring(0, nl);
            firstLine = firstLine.Trim();

            if (firstLine.Length > 0)
                builder.Add(MakeItem(firstLine, ComputeInsert(firstLine, typed)));

            string full = completion.Trim();
            if (full.IndexOf('\n') >= 0 && full.Length > firstLine.Length)
            {
                string label = (firstLine.Length > 0 ? firstLine : "block") + "  …";
                builder.Add(MakeItem(label, ComputeInsert(full, typed)));
            }

            return builder.Count > 0 ? new CompletionContext(builder.ToImmutable()) : CompletionContext.Empty;
        }

        // The committed item replaces the session's applicable span (the partial token already
        // typed). If the model echoed that token, keep its output; otherwise prepend the token so
        // we don't drop characters the user already typed.
        private static string ComputeInsert(string output, string typed)
        {
            if (string.IsNullOrEmpty(typed))
                return output;
            return output.StartsWith(typed, StringComparison.Ordinal) ? output : typed + output;
        }

        private CompletionItem MakeItem(string display, string insert)
        {
            return new CompletionItem(
                display,
                this,
                _icon,
                ImmutableArray<CompletionFilter>.Empty,
                "AI",
                insert,
                display,
                display,
                ImmutableArray<ImageElement>.Empty);
        }

        private static string TryGetBufferPath(ITextBuffer buffer)
        {
            if (buffer != null && buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument doc))
                return doc?.FilePath;
            return null;
        }

        private async Task<string> GetSemanticContextAsync(ITextBuffer buffer, CancellationToken token)
        {
            if (_roslyn == null)
                return null;
            if (buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document) &&
                !string.IsNullOrEmpty(document?.FilePath))
            {
                try
                {
                    return await _roslyn.GetActiveFileContextAsync(document.FilePath, token).ConfigureAwait(false);
                }
                catch
                {
                    return null;
                }
            }
            return null;
        }

        // The word (identifier) ending at the caret, used as the completion's applicable span.
        private static SnapshotSpan GetApplicableSpan(SnapshotPoint triggerLocation)
        {
            ITextSnapshot snapshot = triggerLocation.Snapshot;
            int start = triggerLocation.Position;
            while (start > 0)
            {
                char c = snapshot[start - 1];
                if (!char.IsLetterOrDigit(c) && c != '_')
                    break;
                start--;
            }
            return new SnapshotSpan(snapshot, start, triggerLocation.Position - start);
        }

        private static string ReadBack(ITextSnapshot snapshot, int caret, int maxChars)
        {
            int start = Math.Max(0, caret - maxChars);
            return snapshot.GetText(start, caret - start);
        }

        private static string ReadForward(ITextSnapshot snapshot, int caret, int maxChars)
        {
            int end = Math.Min(snapshot.Length, caret + maxChars);
            return snapshot.GetText(caret, end - caret);
        }

        private static GeneralOptions TryGetOptions()
        {
            try
            {
                return LocalModelIntegratorPackage.Instance?.GetDialogPage(typeof(GeneralOptions)) as GeneralOptions;
            }
            catch
            {
                return null;
            }
        }
    }
}
