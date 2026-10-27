using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VibeCoder.Core.Services;

namespace VibeCoder
{
    /// <summary>
    /// Handles the "Send exception to Vibe Coder" command in the editor context menu.
    /// Sends the exception captured by <see cref="DebuggerExceptionWatcher"/> to the agent chat.
    /// </summary>
    internal sealed class SendExceptionToChatCommand
    {
        /// <summary>Command ID (must match the VSCT IDSymbol).</summary>
        public const int CommandId = 4134;

        /// <summary>Command menu group (command set GUID).</summary>
        public static readonly Guid CommandSet = new Guid("c5bde2f2-66ac-455e-8b60-52f82c989a95");

        private readonly AsyncPackage package;

        private SendExceptionToChatCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var commandId = new CommandID(CommandSet, CommandId);
            var menuItem = new OleMenuCommand(this.Execute, commandId);
            menuItem.BeforeQueryStatus += OnBeforeQueryStatus;
            commandService.AddCommand(menuItem);
        }

        /// <summary>Gets the singleton instance of the command.</summary>
        public static SendExceptionToChatCommand Instance { get; private set; }

        /// <summary>Initializes the singleton instance of the command.</summary>
        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new SendExceptionToChatCommand(package, commandService);
        }

        private void OnBeforeQueryStatus(object sender, EventArgs e)
        {
            if (!(sender is OleMenuCommand menu))
                return;

            menu.Visible = true;
            menu.Enabled = DebuggerExceptionWatcher.Instance?.Pending != null;
        }

        private void Execute(object sender, EventArgs e)
        {
            this.package.JoinableTaskFactory.RunAsync(async delegate
            {
                try
                {
                    await SendAsync();
                }
                catch (Exception ex)
                {
                    await ShowErrorAsync(ex);
                }
            });
        }

        private async Task SendAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(this.package.DisposalToken);

            var snapshot = DebuggerExceptionWatcher.Instance?.Pending;
            var window = await this.package.ShowToolWindowAsync(typeof(VibeCoderToolWindow), 0, true, this.package.DisposalToken) as VibeCoderToolWindow;
            var control = window?.Control;

            if (snapshot == null)
            {
                control?.AppendSystemMessage("Send exception to Vibe Coder: no exception captured.");
                return;
            }

            if (control == null)
                throw new InvalidOperationException("Cannot create the Vibe Coder window.");

            await control.SubmitErrorToChatAsync(ExceptionContextBuilder.Build(snapshot));
            DebuggerExceptionWatcher.Instance?.ClearPending();
        }

        private async Task ShowErrorAsync(Exception ex)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(this.package.DisposalToken);
            try
            {
                var window = await this.package.ShowToolWindowAsync(typeof(VibeCoderToolWindow), 0, true, this.package.DisposalToken) as VibeCoderToolWindow;
                window?.Control?.AppendSystemMessage("⚠ Failed to send the exception to Vibe Coder: " + ex.Message);
            }
            catch
            {
                // Last-resort fallback: write to the activity log.
                ActivityLog.LogError("Vibe Coder", "Failed to send debugger exception: " + ex);
            }
        }
    }
}
