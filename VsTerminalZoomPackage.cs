using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.Settings;
using Microsoft.VisualStudio.Terminal;
using Task = System.Threading.Tasks.Task;

namespace VsTerminalZoom
{
    /// <summary>
    /// Adds Terminal.FontZoomIn / FontZoomOut / FontZoomReset (Ctrl+Alt+= / Ctrl+Alt+- / Ctrl+Alt+0 while the
    /// Terminal tool window has focus), Terminal.SendShiftEnter (Shift+Enter there) and Terminal.New
    /// (Ctrl+Shift+` anywhere). The package loads on first use of one of the commands; nothing runs at startup.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [Guid(PackageGuidString)]
    public sealed class VsTerminalZoomPackage : AsyncPackage
    {
        public const string PackageGuidString = "6f3b9a21-8c4d-4e07-9b5a-2d1e7c8f0a43";

        public static readonly Guid CommandSet = new Guid("c2a7e514-0b9d-4f36-8e61-7a3d5b9c4e28");
        public const int CmdIdZoomIn = 0x0100;
        public const int CmdIdZoomOut = 0x0101;
        public const int CmdIdZoomReset = 0x0102;
        public const int CmdIdSendShiftEnter = 0x0103;
        public const int CmdIdNewTerminal = 0x0104;
        public const int CmdIdCloseTerminal = 0x0105;

        // stdidcmd.h: cmdidPaneCloseToolWindow in guidVSStd97, the command behind Window.CloseToolWindow (Shift+Esc).
        private const uint CmdIdPaneCloseToolWindow = 288;

        // ESC CR: what VS Code's /terminal-setup binds Shift+Enter to, and what Claude Code reads as newline.
        private const string MetaEnter = "\u001b\r";

        private TerminalFontZoom zoom;
        private TerminalInputSender inputSender;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var storage = await GetServiceAsync(typeof(SVsFontAndColorStorage)) as IVsFontAndColorStorage;
            Assumes.Present(storage);

            var commandService = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Assumes.Present(commandService);

            var settingsStore = new ShellSettingsManager(this).GetWritableSettingsStore(SettingsScope.UserSettings);
            zoom = new TerminalFontZoom(storage, settingsStore);
            inputSender = new TerminalInputSender();

            commandService.AddCommand(new MenuCommand(OnZoomIn, new CommandID(CommandSet, CmdIdZoomIn)));
            commandService.AddCommand(new MenuCommand(OnZoomOut, new CommandID(CommandSet, CmdIdZoomOut)));
            commandService.AddCommand(new MenuCommand(OnZoomReset, new CommandID(CommandSet, CmdIdZoomReset)));
            commandService.AddCommand(new MenuCommand(OnSendShiftEnter, new CommandID(CommandSet, CmdIdSendShiftEnter)));
            commandService.AddCommand(new MenuCommand(OnNewTerminal, new CommandID(CommandSet, CmdIdNewTerminal)));
            commandService.AddCommand(new MenuCommand(OnCloseTerminal, new CommandID(CommandSet, CmdIdCloseTerminal)));
        }

        /// <summary>
        /// Closes the focused terminal, the equivalent of VS Code's workbench.action.terminal.kill: each terminal
        /// is its own tool window pane, so Window.CloseToolWindow on it closes the pane and ends the shell.
        /// Bound in the Terminal key scope only, so Ctrl+Shift+W elsewhere keeps closing documents.
        /// </summary>
        private void OnCloseTerminal(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!inputSender.HasFocusedTerminal())
            {
                ReportFailure("Terminal.Close: no focused terminal view; nothing closed.");
                return;
            }

            if (!(GetService(typeof(SUIHostCommandDispatcher)) is IOleCommandTarget dispatcher))
            {
                ReportFailure("Terminal.Close: command dispatcher unavailable.");
                return;
            }

            Guid std97 = VSConstants.GUID_VSStandardCommandSet97;
            int hr = dispatcher.Exec(ref std97, CmdIdPaneCloseToolWindow, (uint)OLECMDEXECOPT.OLECMDEXECOPT_DODEFAULT, IntPtr.Zero, IntPtr.Zero);
            if (ErrorHandler.Failed(hr))
            {
                ReportFailure("Terminal.Close: Window.CloseToolWindow failed (0x" + hr.ToString("X8") + ").");
            }
        }

        private void OnSendShiftEnter(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!inputSender.TrySend(MetaEnter))
            {
                ReportFailure("Terminal.SendShiftEnter: " + (inputSender.LastFailure ?? "failed") + "; key ignored.");
            }
        }

        /// <summary>
        /// Opens a new terminal with the default profile from Tools > Options > Environment > Terminal, the
        /// equivalent of VS Code's workbench.action.terminal.new. Visual Studio exposes no bindable command for
        /// the Terminal window's "+" button, so this goes through the public SVsTerminalService.
        /// </summary>
        private void OnNewTerminal(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await NewTerminalAsync(DisposalToken);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
                    ReportFailure("Terminal.New: " + ex.Message);
                }
            }).FileAndForget("vsterminalzoom/newterminal");
        }

        private async Task NewTerminalAsync(CancellationToken cancellationToken)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var host = await GetServiceAsync(typeof(SVsTerminalService)) as IVsTerminalService;
            Assumes.Present(host);
            ITerminalService terminals = host.CreateTerminalService();

            ITerminalProfile profile = await terminals.GetDefaultProfileAsync(cancellationToken);
            var options = new TerminalWindowOptions
            {
                Name = string.IsNullOrEmpty(profile?.DisplayName) ? "Terminal" : profile.DisplayName,
                Profile = profile,
                Focus = true,
                AllowUserInput = true,
                AutoResize = true,
            };
            Guid id = await terminals.CreateTerminalWindowAsync(cancellationToken, options);
            await terminals.ShowAsync(id, cancellationToken);
        }

        private void ReportFailure(string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ActivityLog.LogWarning(nameof(VsTerminalZoom), message);
            if (GetService(typeof(SVsStatusbar)) is IVsStatusbar statusbar)
            {
                statusbar.SetText(message);
            }
        }

        private void OnZoomIn(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            zoom.Adjust(+1);
        }

        private void OnZoomOut(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            zoom.Adjust(-1);
        }

        private void OnZoomReset(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            zoom.Reset();
        }
    }
}
