using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel.Design;

namespace LocalModelIntegrator.Commands
{
    internal sealed class ClearConversationCommand
    {
        public const int CommandId = 0x0101;
        public static readonly Guid CommandSet = new Guid("C3D4E5F6-A7B8-4C9D-8E0F-1A2B3C4D5E6F");
        private readonly AsyncPackage _package;

        private ClearConversationCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package;
            var menuCommandId = new CommandID(CommandSet, CommandId);
            // Using OleMenuCommand so we can access the tool window from the package
            var menuItem = new OleMenuCommand(Execute, menuCommandId);
            commandService.AddCommand(menuItem);
        }

        public static ClearConversationCommand Instance { get; private set; }

        public static async System.Threading.Tasks.Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new ClearConversationCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ToolWindowPane window = _package.FindToolWindow(typeof(ToolWindows.ChatWindow), 0, false);
            if (window?.Content is ToolWindows.ChatWindowControl control)
            {
                control.ClearConversation();
                control.AppendMessage("system", "Conversation cleared.");
            }
        }
    }
}
