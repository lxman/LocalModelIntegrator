using EnvDTE;
using Microsoft;
using Microsoft.VisualStudio.Shell;
using System;
using System.IO;
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

        // The metadata below walks the whole solution tree (project count), which is far too
        // expensive to repeat on every chat turn - it is computed once per solution and reused.
        private WorkspaceInfo _infoCache;
        private string _infoCacheDir;

        /// <summary>
        /// Gets workspace metadata for display in chat. Computed once per solution (the values
        /// are display-only and rarely change), not on every call.
        /// </summary>
        public async Task<WorkspaceInfo> GetWorkspaceInfoAsync()
        {
            string solutionDir = GetSolutionDirectory();
            if (_infoCache != null && string.Equals(_infoCacheDir, solutionDir, StringComparison.OrdinalIgnoreCase))
                return _infoCache;

            WorkspaceInfo info = await Task.Run(() =>
            {
                var result = new WorkspaceInfo
                {
                    Name = GetSolutionName(),
                    Path = solutionDir ?? "(no solution)"
                };

                if (solutionDir == null) return result;
                result.HasGit = Directory.Exists(Path.Combine(solutionDir, ".git"));
                result.ProjectCount = 0;

                // Count .csproj files
                try
                {
                    result.ProjectCount = Directory.GetFiles(solutionDir, "*.csproj", SearchOption.AllDirectories).Length;
                }
                catch { /* ignore access errors */ }

                return result;
            });

            _infoCache = info;
            _infoCacheDir = solutionDir;
            return info;
        }
    }

    public class WorkspaceInfo
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public bool HasGit { get; set; }
        public int ProjectCount { get; set; }
    }
}
