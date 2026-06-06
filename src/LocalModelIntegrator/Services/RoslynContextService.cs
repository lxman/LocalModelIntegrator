using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.LanguageServices;

namespace LocalModelIntegrator.Services
{
    /// <summary>
    /// Provides live semantic context from the open solution using Roslyn's
    /// <see cref="VisualStudioWorkspace"/>. Exported as a MEF part so editor
    /// features (and the chat window, via IComponentModel) can consume it in-process.
    /// </summary>
    [Export(typeof(RoslynContextService))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    public sealed class RoslynContextService
    {
        private readonly VisualStudioWorkspace _workspace;

        [ImportingConstructor]
        public RoslynContextService(VisualStudioWorkspace workspace)
        {
            _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        }

        private static readonly SymbolDisplayFormat MemberFormat = new SymbolDisplayFormat(
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            memberOptions: SymbolDisplayMemberOptions.IncludeParameters
                | SymbolDisplayMemberOptions.IncludeType
                | SymbolDisplayMemberOptions.IncludeModifiers
                | SymbolDisplayMemberOptions.IncludeAccessibility,
            parameterOptions: SymbolDisplayParameterOptions.IncludeType
                | SymbolDisplayParameterOptions.IncludeName
                | SymbolDisplayParameterOptions.IncludeParamsRefOut
                | SymbolDisplayParameterOptions.IncludeDefaultValue,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

        private const int MaxLength = 4000;

        /// <summary>True when a Roslyn workspace with at least one project is available.</summary>
        public bool HasSolution => _workspace?.CurrentSolution?.ProjectIds.Count > 0;

        /// <summary>
        /// True when <paramref name="filePath"/> is a document in a REAL (project-file-backed) project
        /// of the open solution. Excludes Roslyn's loose/miscellaneous-files host (which has no project
        /// FilePath), so a C#/VB file opened outside the solution is not treated as a member.
        /// </summary>
        public bool IsSolutionDocument(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return false;
            Solution solution = _workspace.CurrentSolution;
            foreach (DocumentId id in solution.GetDocumentIdsWithFilePath(filePath))
            {
                Project p = solution.GetProject(id.ProjectId);
                if (p != null && !string.IsNullOrEmpty(p.FilePath))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Returns a compact outline (namespace, type and member signatures — no bodies)
        /// of the file at <paramref name="filePath"/> if it belongs to the open solution.
        /// Safe to call off the UI thread.
        /// </summary>
        public async Task<string> GetActiveFileContextAsync(string filePath, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(filePath))
                return "(no active file)";

            Solution solution = _workspace.CurrentSolution;
            DocumentId docId = solution.GetDocumentIdsWithFilePath(filePath).FirstOrDefault();
            if (docId == null)
                return "(file is not part of the open solution)";

            Document document = solution.GetDocument(docId);
            if (document == null)
                return "(document unavailable)";

            SemanticModel model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
            SyntaxNode root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            if (model == null || root == null)
                return "(no semantic model — language not supported or project still loading)";

            var sb = new StringBuilder();
            sb.Append("Project: ").AppendLine(document.Project.Name);
            sb.Append("File: ").AppendLine(document.Name);

            var types = new List<INamedTypeSymbol>();
            var membersByType = new Dictionary<INamedTypeSymbol, List<ISymbol>>(SymbolEqualityComparer.Default);
            var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            string namespaceName = null;

            foreach (SyntaxNode node in root.DescendantNodesAndSelf())
            {
                ct.ThrowIfCancellationRequested();
                ISymbol symbol = model.GetDeclaredSymbol(node, ct);
                if (symbol == null || !seen.Add(symbol))
                    continue;

                switch (symbol.Kind)
                {
                    case SymbolKind.Namespace:
                        if (namespaceName == null && !((INamespaceSymbol)symbol).IsGlobalNamespace)
                            namespaceName = symbol.ToDisplayString();
                        break;
                    case SymbolKind.NamedType:
                        var t = (INamedTypeSymbol)symbol;
                        types.Add(t);
                        if (!membersByType.ContainsKey(t))
                            membersByType[t] = new List<ISymbol>();
                        break;
                    case SymbolKind.Method:
                    case SymbolKind.Property:
                    case SymbolKind.Event:
                    case SymbolKind.Field:
                        INamedTypeSymbol container = symbol.ContainingType;
                        if (container != null)
                        {
                            if (!membersByType.TryGetValue(container, out List<ISymbol> list))
                            {
                                list = new List<ISymbol>();
                                membersByType[container] = list;
                            }
                            list.Add(symbol);
                        }
                        break;
                }
            }

            if (namespaceName != null)
                sb.Append("Namespace: ").AppendLine(namespaceName);
            sb.AppendLine();

            foreach (INamedTypeSymbol type in types)
            {
                sb.AppendLine(DescribeType(type));
                if (membersByType.TryGetValue(type, out List<ISymbol> members))
                {
                    foreach (ISymbol member in members)
                    {
                        if (member.IsImplicitlyDeclared)
                            continue;
                        sb.Append("    ").AppendLine(member.ToDisplayString(MemberFormat));
                        if (sb.Length > MaxLength)
                        {
                            sb.AppendLine("    ... (truncated)");
                            return sb.ToString();
                        }
                    }
                }
                sb.AppendLine();
            }

            if (types.Count == 0)
                sb.AppendLine("(no type declarations found)");

            return sb.ToString().TrimEnd();
        }

        private const int MaxReferenceHits = 300;
        private static readonly SymbolDisplayFormat SignatureFormat = SymbolDisplayFormat.CSharpErrorMessageFormat;

        /// <summary>
        /// Locates C#/VB symbol declarations matching <paramref name="name"/> exactly (case-sensitive),
        /// returning each declaration's signature and source location. Safe off the UI thread.
        /// </summary>
        public async Task<List<SymbolHit>> FindSymbolAsync(string name, CancellationToken ct)
        {
            var hits = new List<SymbolHit>();
            if (string.IsNullOrWhiteSpace(name))
                return hits;

            Solution solution = _workspace.CurrentSolution;
            foreach (ISymbol symbol in await ResolveSymbolsAsync(solution, name, ct).ConfigureAwait(false))
            {
                foreach (Location loc in symbol.Locations)
                {
                    if (!loc.IsInSource)
                        continue;
                    FileLinePositionSpan span = loc.GetLineSpan();
                    hits.Add(new SymbolHit(DescribeSymbol(symbol),
                        loc.SourceTree?.FilePath ?? span.Path,
                        span.StartLinePosition.Line + 1));
                }
            }
            return hits;
        }

        /// <summary>
        /// Finds every reference to the C#/VB symbol(s) named <paramref name="name"/> across the
        /// solution using Roslyn's semantic model — exact, no substring false positives. Declarations
        /// are flagged. Capped at <see cref="MaxReferenceHits"/> locations. Safe off the UI thread.
        /// </summary>
        public async Task<FindReferencesResult> FindReferencesAsync(string name, CancellationToken ct)
        {
            var result = new FindReferencesResult();
            if (string.IsNullOrWhiteSpace(name))
            {
                result.Note = "(no symbol name given)";
                return result;
            }

            name = name.Trim();
            Solution solution = _workspace.CurrentSolution;

            List<ISymbol> symbols = await ResolveSymbolsAsync(solution, name, ct).ConfigureAwait(false);

            if (symbols.Count == 0)
            {
                result.Note = $"(no C#/VB symbol named '{name}' found in the solution)";
                return result;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool stop = false;

            foreach (ISymbol symbol in symbols)
            {
                if (stop) break;
                ct.ThrowIfCancellationRequested();
                result.MatchedSymbols.Add(DescribeSymbol(symbol));

                foreach (Location loc in symbol.Locations.Where(l => l.IsInSource))
                {
                    await AddHitAsync(result, seen, isDefinition: true, loc, null, ct).ConfigureAwait(false);
                    if (result.Truncated) { stop = true; break; }
                }
                if (stop) break;

                IEnumerable<ReferencedSymbol> referenced = await SymbolFinder
                    .FindReferencesAsync(symbol, solution, ct).ConfigureAwait(false);
                foreach (ReferencedSymbol rs in referenced)
                {
                    foreach (ReferenceLocation rl in rs.Locations)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!rl.Location.IsInSource)
                            continue;
                        await AddHitAsync(result, seen, isDefinition: false, rl.Location, rl.Document, ct)
                            .ConfigureAwait(false);
                        if (result.Truncated) { stop = true; break; }
                    }
                    if (stop) break;
                }
            }

            result.Hits.Sort((a, b) =>
            {
                int c = string.Compare(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : a.Line.CompareTo(b.Line);
            });
            return result;
        }

        private static async Task AddHitAsync(FindReferencesResult result, HashSet<string> seen,
            bool isDefinition, Location loc, Document doc, CancellationToken ct)
        {
            SyntaxTree tree = loc.SourceTree;
            FileLinePositionSpan span = loc.GetLineSpan();
            string filePath = tree?.FilePath ?? span.Path;
            int line0 = span.StartLinePosition.Line;

            if (!seen.Add(filePath + ":" + line0))
                return;

            string snippet = string.Empty;
            try
            {
                SourceText text = null;
                if (tree != null)
                    text = await tree.GetTextAsync(ct).ConfigureAwait(false);
                else if (doc != null)
                    text = await doc.GetTextAsync(ct).ConfigureAwait(false);
                if (text != null && line0 >= 0 && line0 < text.Lines.Count)
                    snippet = text.Lines[line0].ToString().Trim();
            }
            catch { /* snippet is best-effort */ }

            result.Hits.Add(new ReferenceHit(isDefinition, filePath, line0 + 1, snippet));
            if (result.Hits.Count >= MaxReferenceHits)
                result.Truncated = true;
        }

        // Resolves a simple ("Name") or qualified ("Type.Member" / "Namespace.Type") name to candidate
        // source symbols. Matches the last segment, then (if qualified) prefers candidates whose
        // container matches the qualifier - so "MongoUtil.FromConnectionString" finds the method on MongoUtil.
        private static async Task<List<ISymbol>> ResolveSymbolsAsync(Solution solution, string name, CancellationToken ct)
        {
            name = (name ?? string.Empty).Trim();
            string simple = name;
            string qualifier = null;
            int dot = name.LastIndexOf('.');
            if (dot > 0 && dot < name.Length - 1)
            {
                qualifier = name.Substring(0, dot);
                simple = name.Substring(dot + 1);
            }

            List<ISymbol> matches = (await SymbolFinder
                .FindSourceDeclarationsAsync(solution, simple, ignoreCase: false, ct)
                .ConfigureAwait(false))
                .Where(s => s.Name == simple)
                .ToList();

            if (qualifier != null && matches.Count > 0)
            {
                string qLast = qualifier.Substring(qualifier.LastIndexOf('.') + 1);
                List<ISymbol> filtered = matches.Where(s =>
                {
                    string container = s.ContainingType?.Name ?? s.ContainingNamespace?.Name;
                    string fullContainer = s.ContainingSymbol?.ToDisplayString();
                    return string.Equals(container, qLast, StringComparison.Ordinal)
                        || (fullContainer != null && fullContainer.EndsWith(qualifier, StringComparison.Ordinal));
                }).ToList();
                if (filtered.Count > 0)
                    matches = filtered;
            }

            return matches;
        }

        /// <summary>
        /// Returns the source text of each C#/VB declaration matching <paramref name="name"/> - the
        /// member/type body itself - so the agent can read one symbol without pulling the whole file.
        /// </summary>
        public async Task<string> ReadSymbolAsync(string name, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "(no symbol name given)";

            Solution solution = _workspace.CurrentSolution;
            List<ISymbol> symbols = await ResolveSymbolsAsync(solution, name.Trim(), ct).ConfigureAwait(false);
            if (symbols.Count == 0)
                return $"(no C#/VB symbol named '{name.Trim()}' found in the solution)";

            var sb = new StringBuilder();
            int shown = 0;
            foreach (ISymbol symbol in symbols)
            {
                foreach (SyntaxReference sref in symbol.DeclaringSyntaxReferences)
                {
                    ct.ThrowIfCancellationRequested();
                    SyntaxNode node = await sref.GetSyntaxAsync(ct).ConfigureAwait(false);
                    SourceText text = await sref.SyntaxTree.GetTextAsync(ct).ConfigureAwait(false);
                    FileLinePositionSpan span = node.GetLocation().GetLineSpan();

                    sb.Append(DescribeSymbol(symbol))
                      .Append("  (").Append(System.IO.Path.GetFileName(sref.SyntaxTree.FilePath))
                      .Append(':').Append(span.StartLinePosition.Line + 1).AppendLine(")");
                    sb.AppendLine(text.ToString(node.Span));
                    sb.AppendLine();

                    if (++shown >= 8)
                    {
                        sb.AppendLine("... (more declarations omitted)");
                        return sb.ToString().TrimEnd();
                    }
                }
            }
            return sb.ToString().TrimEnd();
        }

        private static string DescribeSymbol(ISymbol symbol)
        {
            string kind = symbol is INamedTypeSymbol nt
                ? nt.TypeKind.ToString().ToLowerInvariant()
                : symbol.Kind.ToString().ToLowerInvariant();
            return kind + " " + symbol.ToDisplayString(SignatureFormat);
        }

        private static string DescribeType(INamedTypeSymbol type)
        {
            string kind = type.TypeKind.ToString().ToLowerInvariant();
            string name = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

            var bases = new List<string>();
            if (type.BaseType != null && type.BaseType.SpecialType != SpecialType.System_Object)
                bases.Add(type.BaseType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
            bases.AddRange(type.Interfaces.Select(i => i.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));

            string suffix = bases.Count > 0 ? " : " + string.Join(", ", bases) : string.Empty;
            return $"{kind} {name}{suffix}";
        }
    }
}
