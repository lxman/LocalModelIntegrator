using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
// Alias EnvDTE types to avoid colliding with Microsoft.CodeAnalysis.Project/Document/Solution.
using DTE = EnvDTE.DTE;
using EnvProject = EnvDTE.Project;
using EnvProjectItem = EnvDTE.ProjectItem;
using EnvDocument = EnvDTE.Document;
using EnvTextDocument = EnvDTE.TextDocument;

namespace LocalModelIntegrator.Services
{
    /// <summary>A file that belongs to the open solution (as shown in Solution Explorer).</summary>
    public sealed class SolutionFileInfo
    {
        /// <summary>Absolute path on disk (used internally for reads).</summary>
        public string Path { get; }

        /// <summary>Project name (TFM suffix stripped).</summary>
        public string Project { get; }

        public bool IsCode { get; }

        /// <summary>Unique, compact identifier shown to the agent/user: "ProjectName/relative/path".</summary>
        public string DisplayPath { get; }

        public SolutionFileInfo(string path, string project, bool isCode, string displayPath)
        {
            Path = path;
            Project = project;
            IsCode = isCode;
            DisplayPath = displayPath;
        }
    }

    /// <summary>
    /// File access for the agent loop. The file LIST mirrors Solution Explorer with "Show All Files"
    /// off (project items, via DTE) - not a disk walk and not Roslyn-only. File CONTENT is read
    /// never-stale: Roslyn workspace for C#/VB (live buffer when open, disk when closed) -> Running
    /// Document Table live buffer for other open files -> disk. Outlines are C#/VB only.
    /// </summary>
    [Export(typeof(SolutionFileService))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    public sealed class SolutionFileService
    {
        private const int MaxFileBytes = 256 * 1024;
        private const int MaxListedFiles = 5000;
        private const string SolutionFolderKind = "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}";
        // EnvDTE.Constants.vsProjectKindMisc - the "Miscellaneous Files" project that hosts loose
        // files opened outside any real project. DTE enumerates it like any other project, so it must
        // be excluded or those loose files would count as in-scope solution members.
        private const string MiscFilesKind = "{66A26722-8FB5-11D2-AA7E-00C04F688DDE}";

        private static readonly HashSet<string> CodeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".vb", ".fs", ".fsx", ".fsi",
            ".cpp", ".cxx", ".cc", ".c", ".h", ".hpp", ".hxx",
            ".ts", ".tsx", ".js", ".jsx", ".py",
            ".razor", ".cshtml", ".vbhtml", ".xaml"
        };

        private static readonly HashSet<string> BinaryExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".tif", ".tiff",
            ".dll", ".exe", ".pdb", ".so", ".dylib", ".lib", ".bin",
            ".zip", ".7z", ".gz", ".tar", ".nupkg", ".snupkg",
            ".snk", ".pfx", ".ttf", ".otf", ".woff", ".woff2", ".eot",
            ".mp3", ".mp4", ".mov", ".avi", ".pdf"
        };

        private readonly VisualStudioWorkspace _workspace;
        private readonly RoslynContextService _roslyn;

        private List<SolutionFileInfo> _cache;
        private DateTime _cacheStampUtc;

        [ImportingConstructor]
        public SolutionFileService(VisualStudioWorkspace workspace, RoslynContextService roslyn)
        {
            _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
            _roslyn = roslyn ?? throw new ArgumentNullException(nameof(roslyn));
        }

        /// <summary>
        /// Lists the solution's files as Solution Explorer shows them with "Show All Files" off
        /// (project items, including content/markup/config; excluding obj/bin which are not items).
        /// Cached briefly since it walks the DTE project tree on the UI thread.
        /// </summary>
        public IReadOnlyList<SolutionFileInfo> ListFiles()
        {
            if (_cache != null && (DateTime.UtcNow - _cacheStampUtc).TotalSeconds < 5)
                return _cache;

            var result = new List<SolutionFileInfo>();
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // display id -> absolute path

            ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
                if (dte?.Solution == null)
                    return;

                foreach (EnvProject project in dte.Solution.Projects)
                    CollectProject(project, result, seen);
            });

            _cache = result;
            _cacheStampUtc = DateTime.UtcNow;
            return result;
        }

        /// <summary>
        /// True when a Visual Studio solution is open. DTE-based, so it correctly reports "open"
        /// for any solution - including C++/Node/Python solutions the Roslyn workspace (C#/VB only)
        /// does not surface. This is the signal the UI uses to enable or disable the model.
        /// </summary>
        public bool IsSolutionOpen()
        {
            return ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                try
                {
                    var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
                    // Require a real .sln. DTE.Solution.IsOpen is ALSO true for the "Miscellaneous
                    // Files" state (a loose file open with no solution), where FullName is empty -
                    // that must count as "no solution" so the model stays disabled.
                    return dte?.Solution != null && dte.Solution.IsOpen
                        && !string.IsNullOrEmpty(dte.Solution.FullName);
                }
                catch { return false; }
            });
        }

        /// <summary>Identity of the open solution (its .sln path), or "" when none. Used to detect a
        /// solution change so a stale agent transcript can be dropped.</summary>
        public string CurrentSolutionId()
        {
            return ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                try
                {
                    var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
                    return dte?.Solution?.FullName ?? string.Empty;
                }
                catch { return string.Empty; }
            });
        }

        // The allowlist every read is checked against: the canonical, link-resolved path of every
        // file that belongs to the open solution - DTE project items (language-agnostic) unioned
        // with Roslyn documents (catches linked/generated C#/VB files DTE may not list as items).
        // Cached briefly like the file list, since it walks the same trees.
        private HashSet<string> _scopeCache;
        private DateTime _scopeStampUtc;

        private HashSet<string> ScopeSet()
        {
            if (_scopeCache != null && (DateTime.UtcNow - _scopeStampUtc).TotalSeconds < 5)
                return _scopeCache;

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (SolutionFileInfo f in ListFiles())
                AddCanonical(set, f.Path);

            Solution solution = _workspace.CurrentSolution;
            if (solution != null)
            {
                foreach (Project project in solution.Projects)
                {
                    // Skip Roslyn's "Miscellaneous Files" project (and any project with no project
                    // file) - it hosts loose files opened outside the solution, which are not members.
                    if (string.IsNullOrEmpty(project.FilePath))
                        continue;
                    AddCanonical(set, project.FilePath);
                    foreach (Document d in project.Documents)
                        AddCanonical(set, d.FilePath);
                    foreach (TextDocument ad in project.AdditionalDocuments)
                        AddCanonical(set, ad.FilePath);
                }
            }

            _scopeCache = set;
            _scopeStampUtc = DateTime.UtcNow;
            return set;
        }

        private static void AddCanonical(HashSet<string> set, string path)
        {
            string c = PathScope.Canonical(path);
            if (c != null)
                set.Add(c);
        }

        /// <summary>
        /// True when <paramref name="fullPath"/> resolves (symlinks/junctions followed) to a file
        /// that belongs to the open solution. The single authority for "is this in scope," used by
        /// every read path. Replaces the old solution-directory prefix test, which a sibling folder
        /// or a link could slip past.
        /// </summary>
        public bool IsInScope(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
                return false;
            string c = PathScope.Canonical(fullPath);
            return c != null && ScopeSet().Contains(c);
        }

        private void CollectProject(EnvProject project, List<SolutionFileInfo> result, Dictionary<string, string> seen)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (project == null || result.Count >= MaxListedFiles)
                return;

            try
            {
                // Loose files opened outside any project land in the "Miscellaneous Files" project,
                // which DTE enumerates alongside real projects - never treat those as in scope.
                if (string.Equals(project.Kind, MiscFilesKind, StringComparison.OrdinalIgnoreCase))
                    return;

                if (string.Equals(project.Kind, SolutionFolderKind, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (EnvProjectItem item in project.ProjectItems)
                        if (item?.SubProject != null)
                            CollectProject(item.SubProject, result, seen);
                    return;
                }

                // Real projects have a project file on disk. Misc/unmodeled host projects - which can
                // carry loose files opened outside the solution - have no FullName. Excluding by the
                // presence of a project file is more robust than matching the Misc-Files Kind GUID.
                string projectFile = null;
                try { projectFile = project.FullName; } catch { /* unloaded / virtual */ }
                if (string.IsNullOrEmpty(projectFile))
                    return;

                string projectName = CleanProjectName(project.Name);
                string projectDir = null;
                try
                {
                    if (!string.IsNullOrEmpty(project.FullName))
                        projectDir = Path.GetDirectoryName(project.FullName);
                }
                catch { /* unloaded / virtual project */ }

                AddFile(project.FullName, projectName, projectDir, result, seen); // the .csproj node

                foreach (EnvProjectItem item in project.ProjectItems)
                    CollectItem(item, projectName, projectDir, result, seen);
            }
            catch
            {
                // skip projects that throw (unloaded, unsupported)
            }
        }

        private void CollectItem(EnvProjectItem item, string projectName, string projectDir,
            List<SolutionFileInfo> result, Dictionary<string, string> seen)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (item == null || result.Count >= MaxListedFiles)
                return;

            try
            {
                if (item.SubProject != null)
                {
                    CollectProject(item.SubProject, result, seen);
                    return;
                }
            }
            catch { /* SubProject not supported on this item */ }

            try
            {
                for (short i = 1; i <= item.FileCount; i++)
                {
                    string path = item.FileNames[i];
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                        AddFile(path, projectName, projectDir, result, seen);
                }
            }
            catch { /* virtual item with no files */ }

            try
            {
                foreach (EnvProjectItem child in item.ProjectItems)
                    CollectItem(child, projectName, projectDir, result, seen);
            }
            catch { /* no children */ }
        }

        private void AddFile(string path, string projectName, string projectDir,
            List<SolutionFileInfo> result, Dictionary<string, string> seen)
        {
            if (string.IsNullOrEmpty(path) || result.Count >= MaxListedFiles)
                return;

            // Skip binaries (vendored DLLs/EXEs, images, etc.) - they are noise the agent can't read.
            if (BinaryExtensions.Contains(Path.GetExtension(path)))
                return;

            // Linked/out-of-tree items fall back to a bare filename, which can collide with a
            // DIFFERENT file's display id - disambiguate those instead of silently dropping the
            // file. The same physical file reached twice under one id is still listed only once.
            string display = projectName + "/" + RelativeToDir(projectDir, path);
            string unique = display;
            for (int n = 2; seen.TryGetValue(unique, out string existing); n++)
            {
                if (string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
                    return; // true duplicate of an already-listed file
                unique = display + " (" + n + ")";
            }
            seen[unique] = path;
            result.Add(new SolutionFileInfo(path, projectName, IsCodeFile(path), unique));
        }

        /// <summary>
        /// Reads the freshest content of a solution file. Accepts a project-scoped id
        /// ("Project/rel/path"), a path suffix, a bare filename, or an absolute path.
        /// </summary>
        public Task<string> ReadFileAsync(string path, CancellationToken ct) =>
            ReadFileAsync(path, 0, 0, ct);

        /// <summary>
        /// Reads a file, optionally only lines <paramref name="startLine"/>..<paramref name="endLine"/>
        /// (1-based, inclusive; pass 0 for "from start"/"to end"). Content is never-stale, as above.
        /// </summary>
        public async Task<string> ReadFileAsync(string path, int startLine, int endLine, CancellationToken ct)
        {
            string full = ResolvePath(path, out string error);
            if (full == null)
                return error;

            // 1. Roslyn document (C#/VB): live buffer if open, disk if closed.
            Solution solution = _workspace.CurrentSolution;
            DocumentId docId = solution.GetDocumentIdsWithFilePath(full).FirstOrDefault();
            if (docId != null)
            {
                Document doc = solution.GetDocument(docId);
                if (doc != null)
                    return Slice((await doc.GetTextAsync(ct).ConfigureAwait(false)).ToString(), startLine, endLine);

                TextDocument additional = solution.GetAdditionalDocument(docId);
                if (additional != null)
                    return Slice((await additional.GetTextAsync(ct).ConfigureAwait(false)).ToString(), startLine, endLine);
            }

            // Past a Roslyn document (which is by definition a solution member), we are about to
            // touch the live editor buffer or the disk directly - enforce the scope boundary here,
            // so an out-of-solution file that merely happens to be open in an editor stays unreadable.
            if (!IsInScope(full))
                return "(access denied: that file is not part of the open solution)";

            // 2. Open in an editor but not a Roslyn doc - read the live buffer (honors unsaved edits).
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            string live = TryGetOpenDocumentText(full);
            if (live != null)
                return Slice(live, startLine, endLine);

            // 3. Closed: disk (binary-aware, sandboxed). May return a "(status)" string instead of content.
            string disk = ReadFromDisk(full);
            return disk.StartsWith("(", StringComparison.Ordinal) ? disk : Slice(disk, startLine, endLine);
        }

        // Returns the whole content when no range is given, else lines [start..end] (1-based) with a header.
        private static string Slice(string content, int startLine, int endLine)
        {
            if (startLine <= 0 && endLine <= 0)
                return content;

            string[] lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int total = lines.Length;
            int s = startLine < 1 ? 1 : startLine;
            int e = (endLine < 1 || endLine > total) ? total : endLine;
            if (s > total)
                return $"(requested lines {startLine}-{endLine}, but the file has only {total} lines)";
            if (e < s)
                e = s;

            string header = $"(lines {s}-{e} of {total})\n";
            return header + string.Join("\n", lines.Skip(s - 1).Take(e - s + 1));
        }

        /// <summary>Structural outline for a C#/VB file; a note for other file types.</summary>
        public Task<string> GetOutlineAsync(string path, CancellationToken ct)
        {
            string full = ResolvePath(path, out string error);
            if (full == null)
                return Task.FromResult(error);

            string ext = Path.GetExtension(full);
            if (!string.Equals(ext, ".cs", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ext, ".vb", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult("(no semantic outline for this file type - read it to view contents)");

            return _roslyn.GetActiveFileContextAsync(full, ct);
        }

        /// <summary>
        /// Searches the textual content of the solution's files (the same set <see cref="ListFiles"/>
        /// returns, binaries already excluded). Reads from disk for speed, so a very recent unsaved
        /// edit in an open file may not be reflected - use <see cref="ReadFileAsync"/> for exact content.
        /// </summary>
        public async Task<IReadOnlyList<SearchMatch>> SearchContentAsync(
            string query, bool useRegex, CancellationToken ct, int maxMatches = 200)
        {
            if (string.IsNullOrEmpty(query))
                return System.Array.Empty<SearchMatch>();

            // Compiling here (not inside Task.Run) surfaces an invalid pattern via the awaited task.
            // The match timeout bounds model-supplied patterns: catastrophic backtracking would
            // otherwise pin this thread indefinitely, and cancellation cannot interrupt one IsMatch.
            Regex regex = useRegex
                ? new Regex(query, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2))
                : null;

            IReadOnlyList<SolutionFileInfo> files = ListFiles();

            return await Task.Run(() =>
            {
                var matches = new List<SearchMatch>();
                foreach (SolutionFileInfo f in files)
                {
                    if (matches.Count >= maxMatches)
                        break;
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        if (!File.Exists(f.Path) || new FileInfo(f.Path).Length > MaxFileBytes)
                            continue;

                        int lineNo = 0;
                        foreach (string line in File.ReadLines(f.Path))
                        {
                            lineNo++;
                            bool hit = regex != null
                                ? regex.IsMatch(line)
                                : line.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                            if (!hit)
                                continue;

                            matches.Add(new SearchMatch(f.DisplayPath, lineNo, line.Trim()));
                            if (matches.Count >= maxMatches)
                                break;
                        }
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        // A pathological pattern would time out on every file - stop the whole
                        // search and return whatever matched before the timeout.
                        break;
                    }
                    catch { /* skip unreadable files */ }
                }
                return (IReadOnlyList<SearchMatch>)matches;
            }, ct).ConfigureAwait(false);
        }

        /// <summary>Maps an absolute path to its project-scoped display id (or the file name if unknown).</summary>
        public string ToDisplayPath(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
                return fullPath;
            SolutionFileInfo hit = ListFiles()
                .FirstOrDefault(f => string.Equals(f.Path, fullPath, StringComparison.OrdinalIgnoreCase));
            return hit?.DisplayPath ?? Path.GetFileName(fullPath);
        }

        /// <summary>
        /// Resolves an input to a single full path against the solution's files. Reports ambiguity
        /// (multiple matches) and never guesses.
        /// </summary>
        private string ResolvePath(string input, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(input))
            {
                error = "(no path given)";
                return null;
            }

            input = input.Trim().Trim('"');

            // Absolute/rooted path: allowed only if it resolves (links followed) to a file that
            // belongs to the open solution - no unconditional passthrough.
            bool rooted = false;
            try { rooted = Path.IsPathRooted(input); }
            catch { /* malformed - fall through to needle matching */ }
            if (rooted)
            {
                if (IsInScope(input))
                    return PathScope.Canonical(input) ?? input;
                error = "(access denied: that path is not part of the open solution)";
                return null;
            }

            string needle = input.Replace('\\', '/');
            IReadOnlyList<SolutionFileInfo> files = ListFiles();

            // Exact project-scoped id.
            SolutionFileInfo exact = files.FirstOrDefault(
                f => string.Equals(f.DisplayPath, needle, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
                return exact.Path;

            // Path-suffix or bare-filename match.
            List<SolutionFileInfo> matches = files.Where(f => DisplayMatches(f, needle)).ToList();
            if (matches.Count == 1)
                return matches[0].Path;
            if (matches.Count > 1)
            {
                error = "(ambiguous - matches multiple files:\n" +
                        string.Join("\n", matches.Take(10).Select(m => "  " + m.DisplayPath)) + ")";
                return null;
            }

            error = "(file not found in the open solution: " + input + ")";
            return null;
        }

        private static bool DisplayMatches(SolutionFileInfo f, string needle)
        {
            if (needle.IndexOf('/') < 0)
                return string.Equals(Path.GetFileName(f.Path), needle, StringComparison.OrdinalIgnoreCase);

            return f.DisplayPath.EndsWith("/" + needle, StringComparison.OrdinalIgnoreCase);
        }

        private static string CleanProjectName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;
            int paren = name.IndexOf(" (", StringComparison.Ordinal);
            return paren > 0 ? name.Substring(0, paren) : name;
        }

        private static string RelativeToDir(string dir, string fullPath)
        {
            if (!string.IsNullOrEmpty(dir) &&
                fullPath.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return fullPath.Substring(dir.Length + 1).Replace('\\', '/');

            return Path.GetFileName(fullPath);
        }

        private static bool IsCodeFile(string path) => CodeExtensions.Contains(Path.GetExtension(path));

        private string ReadFromDisk(string fullPath)
        {
            if (!IsInScope(fullPath))
                return "(access denied: that file is not part of the open solution)";

            if (!File.Exists(fullPath))
                return "(file not found)";

            var info = new FileInfo(fullPath);
            if (IsBinaryFile(fullPath))
                return $"(binary file, {info.Length} bytes - not shown)";
            if (info.Length > MaxFileBytes)
                return $"(file too large to read inline: {info.Length} bytes)";

            return File.ReadAllText(fullPath);
        }

        private static bool IsBinaryFile(string path)
        {
            if (BinaryExtensions.Contains(Path.GetExtension(path)))
                return true;

            try
            {
                using (FileStream fs = File.OpenRead(path))
                {
                    int len = (int)Math.Min(8192, fs.Length);
                    for (int i = 0; i < len; i++)
                        if (fs.ReadByte() == 0)
                            return true;
                }
            }
            catch { /* treat unreadable as non-binary; disk read will surface the error */ }

            return false;
        }

        // Reads the live (possibly unsaved) text of an open document via DTE. Works for any open text
        // document - code or not (e.g. a .csproj opened with "Edit Project File") - and returns the
        // editor buffer's current contents, not what is on disk. Same pattern used by the editor
        // context-menu actions.
        private static string TryGetOpenDocumentText(string fullPath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            if (dte?.Documents == null)
                return null;

            foreach (EnvDocument doc in dte.Documents)
            {
                string moniker;
                try { moniker = doc.FullName; }
                catch { continue; }

                if (!MonikerMatches(moniker, fullPath))
                    continue;

                try
                {
                    if (doc.Object("TextDocument") is EnvTextDocument textDoc)
                    {
                        EnvDTE.EditPoint start = textDoc.StartPoint.CreateEditPoint();
                        return start.GetText(textDoc.EndPoint);
                    }
                }
                catch { /* open in a non-text editor - fall back to disk */ }

                return null;
            }

            return null;
        }

        private static bool MonikerMatches(string moniker, string fullPath)
        {
            if (string.IsNullOrEmpty(moniker))
                return false;
            if (string.Equals(moniker, fullPath, StringComparison.OrdinalIgnoreCase))
                return true;
            try
            {
                return string.Equals(Path.GetFullPath(moniker), Path.GetFullPath(fullPath),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
