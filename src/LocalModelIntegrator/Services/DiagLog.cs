using System;
using System.IO;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace LocalModelIntegrator.Services
{
    /// <summary>
    /// Best-effort diagnostics log, available in Release as well as Debug so end users can
    /// capture a repro when reporting issues. It is OFF unless the user turns on the "Enable
    /// Logging" option (default false), and every caller gates on options.EnableLogging, so
    /// nothing is written until the user opts in. When on, writes go to two fail-safe sinks:
    ///   1. a VS Output pane ("Local Model Integrator") for live viewing inside VS, and
    ///   2. a file at %TEMP%\LocalModelIntegrator\diag.log so the contents can be read outside VS.
    /// The file is truncated once per process. Writes never throw and never disrupt the extension.
    ///
    /// Privacy: the log contains the endpoint URL and the full request/response bodies (prompts,
    /// model output, and any code the agent read) - useful for debugging, and the reason it is
    /// off by default. The API key is NOT logged: it travels only in the Authorization header,
    /// which is never written here.
    /// </summary>
    public static class DiagLog
    {
        private static readonly Guid PaneGuid = new Guid("7F9C2A14-3B6E-4D58-9E1A-2C7B8D4F0E63");
        private static volatile IVsOutputWindowPane _pane;

        private static readonly string LogFilePath = ComputeLogPath();
        private static readonly object _fileGate = new object();
        private static bool _fileStarted;

        /// <summary>Absolute path of the log file (or null if it could not be created).</summary>
        public static string FilePath => LogFilePath;

        public static void Write(string category, string message)
        {
            string line;
            try
            {
                string stamp = DateTime.Now.ToString("HH:mm:ss.fff");
                line = $"[{stamp}] [{category}] {message}{Environment.NewLine}";
            }
            catch
            {
                return;
            }

            try { GetPane()?.OutputStringThreadSafe(line); } catch { /* pane sink best-effort */ }
            WriteFile(line);
        }

        private static string ComputeLogPath()
        {
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "LocalModelIntegrator");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "diag.log");
            }
            catch
            {
                return null;
            }
        }

        private static void WriteFile(string line)
        {
            if (LogFilePath == null)
                return;

            lock (_fileGate)
            {
                try
                {
                    if (!_fileStarted)
                    {
                        File.WriteAllText(LogFilePath,
                            $"=== Local Model Integrator log - started {DateTime.Now} ==={Environment.NewLine}");
                        _fileStarted = true;
                    }
                    File.AppendAllText(LogFilePath, line);
                }
                catch
                {
                    // file sink best-effort
                }
            }
        }

        private static IVsOutputWindowPane GetPane()
        {
            IVsOutputWindowPane existing = _pane;
            if (existing != null)
                return existing;

            try
            {
                ThreadHelper.JoinableTaskFactory.Run(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (Package.GetGlobalService(typeof(SVsOutputWindow)) is IVsOutputWindow outputWindow)
                    {
                        Guid guid = PaneGuid;
                        // visible:1, clearWithSolution:0 - keep history across solution switches.
                        outputWindow.CreatePane(ref guid, "Local Model Integrator", 1, 0);
                        outputWindow.GetPane(ref guid, out IVsOutputWindowPane pane);
                        _pane = pane;
                    }
                });
            }
            catch
            {
                // UI/service not ready yet - a later call will retry.
            }
            return _pane;
        }
    }
}
