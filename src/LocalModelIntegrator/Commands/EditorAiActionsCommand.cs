using System;
using System.ComponentModel.Design;
using EnvDTE;
using LocalModelIntegrator.Services;
using LocalModelIntegrator.ToolWindows;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace LocalModelIntegrator.Commands
{
    /// <summary>
    /// Editor right-click "Local Model AI" actions. Operates on the current selection (or the whole
    /// active document) and routes a templated prompt into the chat window, streaming the answer.
    /// Lives on the code-window context menu, which ReSharper does not intercept.
    /// </summary>
    internal sealed class EditorAiActionsCommand
    {
        public static readonly Guid CommandSet = new Guid("C3D4E5F6-A7B8-4C9D-8E0F-1A2B3C4D5E6F");

        public const int ExplainId = 0x0110;
        public const int RefactorId = 0x0111;
        public const int GenerateTestsId = 0x0112;
        public const int AddDocCommentsId = 0x0113;

        // The "Local Model AI" flyout id (from the .vsct). Registered so we can hide the flyout
        // itself - not just its buttons - when the action doesn't apply.
        public const int LocalModelSubMenuId = 0x1031;

        private readonly AsyncPackage _package;

        private EditorAiActionsCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            AddCommand(commandService, ExplainId);
            AddCommand(commandService, RefactorId);
            AddCommand(commandService, GenerateTestsId);
            AddCommand(commandService, AddDocCommentsId);

            // Hide the flyout itself when out of scope. A submenu's visibility is otherwise driven
            // only by the declarative VisibilityConstraint (solution open), which is too coarse.
            var submenu = new OleMenuCommand((s, e) => { }, new CommandID(CommandSet, LocalModelSubMenuId));
            submenu.BeforeQueryStatus += OnBeforeQueryStatus;
            commandService.AddCommand(submenu);
        }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            _ = new EditorAiActionsCommand(package, commandService);
        }

        private void AddCommand(OleMenuCommandService commandService, int id)
        {
            var menuItem = new OleMenuCommand(Execute, new CommandID(CommandSet, id));
            // Hide the entry entirely when no solution is open, so the right-click menu has no
            // "Local Model AI" item at all (rather than showing it and then refusing).
            menuItem.BeforeQueryStatus += OnBeforeQueryStatus;
            commandService.AddCommand(menuItem);
        }

        private void OnBeforeQueryStatus(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(sender is OleMenuCommand cmd))
                return;

            SolutionFileService files = GetSolutionFileService();
            if (files == null || !files.IsSolutionOpen())
            {
                cmd.Visible = false;
                return;
            }

            // Hide when the active document isn't part of the solution (e.g. a loose file open
            // alongside it), since the action ships that document's text to the model.
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            string activeFile = dte?.ActiveDocument?.FullName;
            cmd.Visible = !string.IsNullOrEmpty(activeFile) && files.IsInScope(activeFile);
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            int id = ((MenuCommand)sender).CommandID.ID;
            _ = ExecuteAsync(id);
        }

        private async Task ExecuteAsync(int commandId)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                SolutionFileService files = GetSolutionFileService();
                if (files == null || !files.IsSolutionOpen())
                    return; // the menu entry is hidden without a solution; this is just a safety net

                // The action ships the active file's text to the model, so it must be in the solution -
                // the active document can be a loose file outside the open solution.
                var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
                string activeFile = dte?.ActiveDocument?.FullName;
                if (string.IsNullOrEmpty(activeFile) || !files.IsInScope(activeFile))
                {
                    System.Windows.MessageBox.Show(
                        "This file isn't part of the open solution. Local Model AI actions are limited to files in the open solution.",
                        "Local Model Integrator");
                    return;
                }

                string code = GetSelectedOrAllCode(out string fileName);
                if (string.IsNullOrWhiteSpace(code))
                {
                    System.Windows.MessageBox.Show("Open a code file (and optionally select some code) first.",
                        "Local Model Integrator");
                    return;
                }

                string prompt = BuildPrompt(commandId, code);

                ToolWindowPane window = _package.FindToolWindow(typeof(ChatWindow), 0, true);
                if (window?.Frame == null)
                    return;

                var frame = (IVsWindowFrame)window.Frame;
                Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());

                if (window.Content is ChatWindowControl control)
                    await control.SubmitPromptAsync(prompt);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Local Model action failed: " + ex.Message, "Local Model Integrator");
            }
        }

        private static SolutionFileService GetSolutionFileService()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var cm = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
            return cm?.GetService<SolutionFileService>();
        }

        private static string GetSelectedOrAllCode(out string fileName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            fileName = null;

            var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            Document doc = dte?.ActiveDocument;
            if (doc == null)
                return null;

            fileName = doc.Name;

            if (doc.Selection is TextSelection selection && !string.IsNullOrEmpty(selection.Text))
                return selection.Text;

            // No selection: use the whole document.
            if (doc.Object("TextDocument") is TextDocument textDocument)
            {
                EditPoint start = textDocument.StartPoint.CreateEditPoint();
                return start.GetText(textDocument.EndPoint);
            }

            return null;
        }

        private static string BuildPrompt(int commandId, string code)
        {
            string fenced = "```csharp\n" + code + "\n```";
            switch (commandId)
            {
                case ExplainId:
                    return "Explain what the following C# code does, concisely:\n\n" + fenced;
                case RefactorId:
                    return "Refactor the following C# code for clarity, correctness, and idiomatic style. " +
                           "Show the improved version and briefly note what changed:\n\n" + fenced;
                case GenerateTestsId:
                    return "Write thorough unit tests (xUnit) for the following C# code:\n\n" + fenced;
                case AddDocCommentsId:
                    return "Add XML documentation comments to the following C# members. " +
                           "Return the same code with doc comments added and no other changes:\n\n" + fenced;
                default:
                    return fenced;
            }
        }
    }
}
