using LocalModelIntegrator.Services;
using LocalModelIntegrator.ToolWindows;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.ComponentModel.Design;
using Task = System.Threading.Tasks.Task;

namespace LocalModelIntegrator.Commands
{
    /// <summary>
    /// Sends the active editor file to the chat tool window as reference context: shows the
    /// window and loads the file's current content into the conversation so the model can use it.
    /// </summary>
    internal sealed class SendFileToChatCommand
    {
        public const int CommandId = 0x0102;
        public static readonly Guid CommandSet = new Guid("C3D4E5F6-A7B8-4C9D-8E0F-1A2B3C4D5E6F");
        private readonly AsyncPackage _package;

        private SendFileToChatCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package;
            var menuCommandId = new CommandID(CommandSet, CommandId);
            var menuItem = new OleMenuCommand(Execute, menuCommandId);
            commandService.AddCommand(menuItem);
        }

        public static SendFileToChatCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new SendFileToChatCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _ = ExecuteAsync();
        }

        private async Task ExecuteAsync()
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // GetGlobalService rather than blocking on GetServiceAsync(...).Result, which can
                // deadlock the UI thread.
                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                string activeFile = dte?.ActiveDocument?.FullName;
                if (string.IsNullOrEmpty(activeFile))
                {
                    System.Windows.MessageBox.Show("No active document open.", "Local Model Integrator",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                    return;
                }

                var componentModel = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
                SolutionFileService files = componentModel?.GetService<SolutionFileService>();
                if (files == null || !files.IsSolutionOpen() || !files.IsInScope(activeFile))
                {
                    System.Windows.MessageBox.Show("This file isn't part of the open solution.", "Local Model Integrator",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                    return;
                }

                // Show the window so the confirmation (and the loaded context) is actually visible.
                ToolWindowPane window = _package.FindToolWindow(typeof(ChatWindow), 0, true);
                if (window?.Frame == null)
                    return;
                Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(((IVsWindowFrame)window.Frame).Show());

                if (window.Content is ChatWindowControl control)
                    await control.SendFileToChatAsync(activeFile);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Send File to Chat failed: " + ex.Message, "Local Model Integrator");
            }
        }
    }
}
