using LocalModelIntegrator.Commands;
using LocalModelIntegrator.Options;
using LocalModelIntegrator.ToolWindows;
using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Task = System.Threading.Tasks.Task;

namespace LocalModelIntegrator
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("Local Model Integrator",
        "Chat with locally-hosted or remote AI models inside Visual Studio.",
        "1.0.5")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [Guid(PackageGuids.PackageString)]
    [ProvideOptionPage(typeof(GeneralOptions), "Local Model Integrator", "General", 0, 0, true)]
    [ProvideToolWindow(typeof(ChatWindow), Style = VsDockStyle.Tabbed, Window = "DocumentWell")]
    // Load in the background once a solution is open so editor features (completions, Roslyn
    // context) can read options via the package without the user opening the chat first.
    // GUID is UICONTEXT.SolutionExists.
    [ProvideAutoLoad("f1536ef8-92ec-443c-9ed7-fdadf150da82", PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class LocalModelIntegratorPackage : AsyncPackage
    {
        public static LocalModelIntegratorPackage Instance { get; private set; }

        /// <summary>Opens the General options page (used by the chat window's endpoint chip/banner).</summary>
        public void ShowOptions() => ShowOptionPage(typeof(GeneralOptions));

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            Instance = this;

            await OpenChatWindowCommand.InitializeAsync(this);
            await ClearConversationCommand.InitializeAsync(this);
            await SendFileToChatCommand.InitializeAsync(this);
            await EditorAiActionsCommand.InitializeAsync(this);
        }
    }

    public static class PackageGuids
    {
        public const string PackageString = "9719D000-2E35-4BBA-96B2-2454631E5CDC";
        public static readonly Guid Package = new Guid(PackageString);
    }
}
