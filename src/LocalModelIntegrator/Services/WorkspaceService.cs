using EnvDTE;
using Microsoft;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace LocalModelIntegrator.Services
{
    /// <summary>
    /// Provides workspace context from the active Visual Studio solution.
    /// </summary>
    public class WorkspaceService
    {
        private readonly IServiceProvider _serviceProvider;

        public WorkspaceService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        /// <summary>
        /// Gets the current solution directory, or null if no solution is open.
        /// </summary>
        public string GetSolutionDirectory()
        {
            return ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = _serviceProvider.GetService(typeof(DTE)) as DTE;
                Assumes.Present(dte);
                return Path.GetDirectoryName(dte.Solution?.FullName);
            });
        }

        /// <summary>
        /// Gets the current solution name.
        /// </summary>
        public string GetSolutionName()
        {
            return ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = _serviceProvider.GetService(typeof(DTE)) as DTE;
                Assumes.Present(dte);
                return dte.Solution?.FullName != null
                    ? Path.GetFileNameWithoutExtension(dte.Solution.FullName)
                    : "(no solution)";
            });
        }

        /// <summary>
        /// Gets the active document's file path, or null.
        /// </summary>
        public string GetActiveFilePath()
        {
            return ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = _serviceProvider.GetService(typeof(DTE)) as DTE;
                return dte?.ActiveDocument?.FullName;
            });
        }

        /// <summary>
        /// Reads a file relative to the solution directory.
        /// </summary>
        public async Task<string> ReadFileAsync(string relativePath)
        {
            string solutionDir = GetSolutionDirectory();
            if (solutionDir == null)
                throw new InvalidOperationException("No solution is open.");

            string fullPath = Path.GetFullPath(Path.Combine(solutionDir, relativePath));

            // Security: prevent path traversal outside solution
            if (!fullPath.StartsWith(solutionDir, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Access denied: path '{relativePath}' is outside the solution directory.");

            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"File not found: {relativePath}");

            return await Task.Run(() => File.ReadAllText(fullPath));
        }

        /// <summary>
        /// Lists files in a directory relative to the solution directory.
        /// </summary>
        public async Task<List<FileEntry>> ListFilesAsync(string relativePath, bool recursive = false)
        {
            string solutionDir = GetSolutionDirectory();
            if (solutionDir == null)
                throw new InvalidOperationException("No solution is open.");

            string dirPath = string.IsNullOrEmpty(relativePath)
                ? solutionDir
                : Path.GetFullPath(Path.Combine(solutionDir, relativePath));

            if (!dirPath.StartsWith(solutionDir, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Access denied: path '{relativePath}' is outside the solution directory.");

            var result = new List<FileEntry>();
            SearchOption searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            await Task.Run(() =>
            {
                result.AddRange(Directory.GetDirectories(dirPath, "*", searchOption).Select(dir => new FileEntry { Name = Path.GetFileName(dir), FullPath = dir, Type = FileEntryType.Directory }));
                result.AddRange(Directory.GetFiles(dirPath, "*", searchOption).Select(file => new FileEntry { Name = Path.GetFileName(file), FullPath = file, Type = FileEntryType.File }));
            });

            return result.OrderBy(e => e.Type).ThenBy(e => e.Name).ToList();
        }

        /// <summary>
        /// Gets workspace metadata for display in chat.
        /// </summary>
        public async Task<WorkspaceInfo> GetWorkspaceInfoAsync()
        {
            string solutionDir = GetSolutionDirectory();
            return await Task.Run(() =>
            {
                var info = new WorkspaceInfo
                {
                    Name = GetSolutionName(),
                    Path = solutionDir ?? "(no solution)"
                };

                if (solutionDir == null) return info;
                info.HasGit = Directory.Exists(Path.Combine(solutionDir, ".git"));
                info.HasPackagesConfig = File.Exists(Path.Combine(solutionDir, "packages.config"));
                info.ProjectCount = 0;

                // Count .csproj files
                try
                {
                    info.ProjectCount = Directory.GetFiles(solutionDir, "*.csproj", SearchOption.AllDirectories).Length;
                }
                catch { /* ignore access errors */ }

                return info;
            });
        }
    }

    public class FileEntry
    {
        public string Name { get; set; }
        public string FullPath { get; set; }
        public FileEntryType Type { get; set; }
    }

    public enum FileEntryType
    {
        File,
        Directory
    }

    public class WorkspaceInfo
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public bool HasGit { get; set; }
        public bool HasPackagesConfig { get; set; }
        public int ProjectCount { get; set; }
    }
}
