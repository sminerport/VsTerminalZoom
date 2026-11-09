using System;
using System.Collections.Generic;
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
    /// Terminal tool window has focus), Terminal.SendShiftEnter (Shift+Enter there), Terminal.Close (Ctrl+Shift+W
    /// there), Terminal.Split (Ctrl+\ there) and Terminal.New (Ctrl+Shift+` anywhere). The package loads on first
    /// use of one of the commands; nothing runs at startup.
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
        public const int CmdIdSplitTerminal = 0x0106;
        public const int CmdIdFocusNextPane = 0x0107;
        public const int CmdIdFocusPreviousPane = 0x0108;
        public const int CmdIdFocusNext = 0x0109;
        public const int CmdIdFocusPrevious = 0x010A;
        public const int CmdIdScrollLineUp = 0x010B;
        public const int CmdIdScrollLineDown = 0x010C;
        public const int CmdIdScrollPageUp = 0x010D;
        public const int CmdIdScrollPageDown = 0x010E;
        public const int CmdIdScrollToTop = 0x010F;
        public const int CmdIdScrollToBottom = 0x0110;

        // stdidcmd.h: cmdidPaneCloseToolWindow in guidVSStd97, the command behind Window.CloseToolWindow (Shift+Esc).
        private const uint CmdIdPaneCloseToolWindow = 288;

        // ESC CR: what VS Code's /terminal-setup binds Shift+Enter to, and what Claude Code reads as newline.
        private const string MetaEnter = "\u001b\r";

        private const string TerminalPaneTypePrefix = "Microsoft.VisualStudio.Terminal.TerminalWindow";

        // Terminal.Split waits up to NewFrameLookupAttempts x NewFrameLookupDelay (3 seconds) for the new
        // terminal's tool window frame to appear in the shell's frame list.
        private const int NewFrameLookupAttempts = 30;
        private static readonly TimeSpan NewFrameLookupDelay = TimeSpan.FromMilliseconds(100);

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
            commandService.AddCommand(new MenuCommand(OnSplitTerminal, new CommandID(CommandSet, CmdIdSplitTerminal)));
            commandService.AddCommand(new MenuCommand((s, e) => FocusTerminal(+1, true, "Terminal.FocusNextPane"), new CommandID(CommandSet, CmdIdFocusNextPane)));
            commandService.AddCommand(new MenuCommand((s, e) => FocusTerminal(-1, true, "Terminal.FocusPreviousPane"), new CommandID(CommandSet, CmdIdFocusPreviousPane)));
            commandService.AddCommand(new MenuCommand((s, e) => FocusTerminal(+1, false, "Terminal.FocusNext"), new CommandID(CommandSet, CmdIdFocusNext)));
            commandService.AddCommand(new MenuCommand((s, e) => FocusTerminal(-1, false, "Terminal.FocusPrevious"), new CommandID(CommandSet, CmdIdFocusPrevious)));

            // Scrollback keys mirror Windows Terminal's defaults. The passthrough sequences are the xterm encodings of
            // the bound keys (modifier 6 = Ctrl+Shift), sent to the program when the terminal has no scrollback.
            commandService.AddCommand(new MenuCommand((s, e) => ScrollTerminal(ScrollStep.Line, -1, "\u001b[1;6A", "Terminal.ScrollLineUp"), new CommandID(CommandSet, CmdIdScrollLineUp)));
            commandService.AddCommand(new MenuCommand((s, e) => ScrollTerminal(ScrollStep.Line, +1, "\u001b[1;6B", "Terminal.ScrollLineDown"), new CommandID(CommandSet, CmdIdScrollLineDown)));
            commandService.AddCommand(new MenuCommand((s, e) => ScrollTerminal(ScrollStep.Page, -1, "\u001b[5;6~", "Terminal.ScrollPageUp"), new CommandID(CommandSet, CmdIdScrollPageUp)));
            commandService.AddCommand(new MenuCommand((s, e) => ScrollTerminal(ScrollStep.Page, +1, "\u001b[6;6~", "Terminal.ScrollPageDown"), new CommandID(CommandSet, CmdIdScrollPageDown)));
            commandService.AddCommand(new MenuCommand((s, e) => ScrollTerminal(ScrollStep.Edge, -1, "\u001b[1;6H", "Terminal.ScrollToTop"), new CommandID(CommandSet, CmdIdScrollToTop)));
            commandService.AddCommand(new MenuCommand((s, e) => ScrollTerminal(ScrollStep.Edge, +1, "\u001b[1;6F", "Terminal.ScrollToBottom"), new CommandID(CommandSet, CmdIdScrollToBottom)));
        }

        /// <summary>
        /// Scrolls the focused terminal's scrollback (line, page, or to an edge), like Windows Terminal's
        /// Ctrl+Shift+Up/Down, PgUp/PgDn and Home/End. When the terminal has no scrollback, the key is passed to the
        /// program instead so fullscreen programs can scroll themselves.
        /// </summary>
        private void ScrollTerminal(ScrollStep step, int direction, string passthroughSequence, string commandName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                Log.Write(commandName + ": " + TerminalScroller.Scroll(step, direction, passthroughSequence));
            }
            catch (Exception ex)
            {
                ReportFailure(commandName + ": " + ex.GetBaseException().Message);
            }
        }

        /// <summary>
        /// Moves focus to the next or previous terminal. With <paramref name="visibleOnly"/> it cycles through the
        /// terminals currently on screen in left-to-right, top-to-bottom order (VS Code's focusNextPane /
        /// focusPreviousPane for split panes); otherwise through every open terminal, hidden tabs included
        /// (VS Code's focusNext / focusPrevious).
        /// </summary>
        private void FocusTerminal(int step, bool visibleOnly, string commandName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var selection = GetService(typeof(SVsShellMonitorSelection)) as IVsMonitorSelection;
            var uiShell = GetService(typeof(SVsUIShell)) as IVsUIShell;
            if (selection == null || uiShell == null)
            {
                ReportFailure(commandName + ": shell services unavailable.");
                return;
            }

            var candidates = new List<KeyValuePair<IVsWindowFrame, ScreenPosition>>();
            foreach (IVsWindowFrame frame in ToolWindowFrames(uiShell))
            {
                if (!IsTerminalFrame(frame))
                {
                    continue;
                }

                if (visibleOnly && !(ErrorHandler.Succeeded(frame.IsOnScreen(out int onScreen)) && onScreen != 0))
                {
                    continue;
                }

                candidates.Add(new KeyValuePair<IVsWindowFrame, ScreenPosition>(frame, FramePosition(frame)));
            }

            if (visibleOnly)
            {
                // Order by screen position (x, then y) so side-by-side panes cycle left to right.
                candidates.Sort((a, b) => a.Value.X != b.Value.X ? a.Value.X.CompareTo(b.Value.X) : a.Value.Y.CompareTo(b.Value.Y));
            }

            if (candidates.Count < 2)
            {
                ReportFailure(commandName + ": only one terminal " + (visibleOnly ? "visible" : "open") + ".");
                return;
            }

            int activeHr = selection.GetCurrentElementValue((uint)VSConstants.VSSELELEMID.SEID_WindowFrame, out object active);
            if (ErrorHandler.Failed(activeHr))
            {
                ReportFailure(commandName + ": could not read the active window (0x" + activeHr.ToString("X8") + ").");
                return;
            }

            object activePane = active is IVsWindowFrame activeFrame ? GetDocView(activeFrame) : null;
            int current = candidates.FindIndex(c => activePane != null && ReferenceEquals(GetDocView(c.Key), activePane));
            int target = current < 0 ? 0 : (((current + step) % candidates.Count) + candidates.Count) % candidates.Count;

            Log.Write(commandName + ": " + candidates.Count + " candidates, current index " + current + ", showing index " + target);
            int hr = candidates[target].Key.Show();
            if (ErrorHandler.Failed(hr))
            {
                ReportFailure(commandName + ": Show failed (0x" + hr.ToString("X8") + ").");
            }
        }

        private struct ScreenPosition
        {
            public int X;
            public int Y;
        }

        private static ScreenPosition FramePosition(IVsWindowFrame frame)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var mode = new VSSETFRAMEPOS[1];
            if (ErrorHandler.Succeeded(frame.GetFramePos(mode, out Guid _, out int x, out int y, out int _, out int _)))
            {
                return new ScreenPosition { X = x, Y = y };
            }

            return new ScreenPosition { X = int.MaxValue, Y = int.MaxValue };
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

        /// <summary>
        /// Opens a new terminal with the default profile from Tools > Options > Environment > Terminal, the
        /// equivalent of VS Code's workbench.action.terminal.new. Visual Studio exposes no bindable command for
        /// the Terminal window's "+" button, so this goes through the public SVsTerminalService. The new terminal
        /// is then made a tab in the pane of the focused terminal (or of one already on screen): left alone,
        /// Visual Studio opens it wherever the last terminal was docked, which after a Terminal.Split means beside
        /// the others instead of as a tab.
        /// </summary>
        private void OnNewTerminal(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            RunCommand("Terminal.New", NewTerminalAsync);
        }

        private async Task NewTerminalAsync(CancellationToken cancellationToken)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var selection = await GetServiceAsync(typeof(SVsShellMonitorSelection)) as IVsMonitorSelection;
            var uiShell = await GetServiceAsync(typeof(SVsUIShell)) as IVsUIShell;
            Assumes.Present(selection);
            Assumes.Present(uiShell);

            IVsWindowFrame targetFrame = FindTabTarget(selection, uiShell);
            Log.Write("new: tab target " + DescribeFrame(targetFrame));
            HashSet<object> existingPanes = SnapshotPanes(uiShell);

            Guid id = await CreateTerminalAsync(cancellationToken);
            if (targetFrame == null)
            {
                // First terminal: nothing to tab with, Visual Studio's own placement stands.
                return;
            }

            IVsWindowFrame newFrame = await WaitForNewTerminalFrameAsync(uiShell, existingPanes, id, cancellationToken);
            if (newFrame == null)
            {
                ReportFailure("Terminal.New: new terminal opened, but its window was not found; left where Visual Studio put it.");
                return;
            }

            try
            {
                Log.Write("new: " + FrameDocker.DockAsTab(targetFrame, newFrame));
            }
            catch (Exception ex)
            {
                Log.Write("new: tabbing failed: " + ex);
                newFrame.Show();
                ReportFailure("Terminal.New: new terminal opened, but could not be placed as a tab: " + ex.GetBaseException().Message);
            }
        }

        /// <summary>The terminal whose pane a new terminal should join: the focused one, else one on screen.</summary>
        private static IVsWindowFrame FindTabTarget(IVsMonitorSelection selection, IVsUIShell uiShell)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (ErrorHandler.Succeeded(selection.GetCurrentElementValue((uint)VSConstants.VSSELELEMID.SEID_WindowFrame, out object active))
                && active is IVsWindowFrame activeFrame
                && IsTerminalFrame(activeFrame))
            {
                return activeFrame;
            }

            foreach (IVsWindowFrame frame in ToolWindowFrames(uiShell))
            {
                if (IsTerminalFrame(frame) && ErrorHandler.Succeeded(frame.IsOnScreen(out int onScreen)) && onScreen != 0)
                {
                    return frame;
                }
            }

            return null;
        }

        private static HashSet<object> SnapshotPanes(IVsUIShell uiShell)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var panes = new HashSet<object>();
            foreach (IVsWindowFrame frame in ToolWindowFrames(uiShell))
            {
                object pane = GetDocView(frame);
                if (pane != null)
                {
                    panes.Add(pane);
                }
            }

            return panes;
        }

        /// <summary>Polls the shell's tool window list for the frame of the terminal that was just created.</summary>
        private async System.Threading.Tasks.Task<IVsWindowFrame> WaitForNewTerminalFrameAsync(
            IVsUIShell uiShell, HashSet<object> existingPanes, Guid terminalId, CancellationToken cancellationToken)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            for (int attempt = 1; attempt <= NewFrameLookupAttempts; attempt++)
            {
                IVsWindowFrame frame = FindNewTerminalFrame(uiShell, existingPanes, terminalId);
                if (frame != null)
                {
                    Log.Write("new frame " + DescribeFrame(frame) + " found on attempt " + attempt);
                    return frame;
                }

                await Task.Delay(NewFrameLookupDelay, cancellationToken);
                await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            }

            return null;
        }

        /// <summary>
        /// Opens a new default-profile terminal and docks it to the right of the focused one, the equivalent
        /// of VS Code's workbench.action.terminal.split. Docking goes through VS internals (see FrameDocker);
        /// if that part fails the new terminal is still there as a tab and the status bar says why.
        /// </summary>
        private void OnSplitTerminal(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            RunCommand("Terminal.Split", SplitTerminalAsync);
        }

        private void RunCommand(string commandName, Func<CancellationToken, Task> body)
        {
            JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await body(DisposalToken);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
                    ReportFailure(commandName + ": " + ex.GetBaseException().Message);
                }
            }).FileAndForget("vsterminalzoom/" + commandName.ToLowerInvariant());
        }

        private async System.Threading.Tasks.Task<Guid> CreateTerminalAsync(CancellationToken cancellationToken)
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
            return id;
        }

        private async Task SplitTerminalAsync(CancellationToken cancellationToken)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var selection = await GetServiceAsync(typeof(SVsShellMonitorSelection)) as IVsMonitorSelection;
            var uiShell = await GetServiceAsync(typeof(SVsUIShell)) as IVsUIShell;
            Assumes.Present(selection);
            Assumes.Present(uiShell);

            ErrorHandler.ThrowOnFailure(selection.GetCurrentElementValue((uint)VSConstants.VSSELELEMID.SEID_WindowFrame, out object active));
            var targetFrame = active as IVsWindowFrame;
            Log.Write("split: active frame " + DescribeFrame(targetFrame));
            if (targetFrame == null || !IsTerminalFrame(targetFrame))
            {
                ReportFailure("Terminal.Split: the active window is not a terminal; nothing opened.");
                return;
            }

            HashSet<object> existingPanes = SnapshotPanes(uiShell);
            Log.Write("split: " + existingPanes.Count + " tool window panes before creation");
            Guid id = await CreateTerminalAsync(cancellationToken);
            Log.Write("split: created terminal " + id);

            IVsWindowFrame newFrame = await WaitForNewTerminalFrameAsync(uiShell, existingPanes, id, cancellationToken);
            if (newFrame == null)
            {
                ReportFailure("Terminal.Split: new terminal opened, but its window was not found; left where Visual Studio put it.");
                return;
            }

            try
            {
                string how = FrameDocker.DockBeside(targetFrame, newFrame, right: true);
                Log.Write("split: " + how);
                if (await GetServiceAsync(typeof(SVsStatusbar)) is IVsStatusbar statusbar)
                {
                    statusbar.SetText("Terminal.Split: " + how + ".");
                }
            }
            catch (Exception ex)
            {
                Log.Write("split: docking failed: " + ex);
                newFrame.Show();
                ReportFailure("Terminal.Split: new terminal opened as a tab; docking beside the current one failed: " + ex.GetBaseException().Message);
            }
        }

        private static IEnumerable<IVsWindowFrame> ToolWindowFrames(IVsUIShell uiShell)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (ErrorHandler.Failed(uiShell.GetToolWindowEnum(out IEnumWindowFrames frames)) || frames == null)
            {
                yield break;
            }

            var buffer = new IVsWindowFrame[1];
            while (frames.Next(1, buffer, out uint fetched) == VSConstants.S_OK && fetched == 1)
            {
                yield return buffer[0];
            }
        }

        private static string DescribeFrame(IVsWindowFrame frame)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (frame == null)
            {
                return "(null)";
            }

            string caption = ErrorHandler.Succeeded(frame.GetProperty((int)__VSFPROPID.VSFPROPID_Caption, out object value)) ? value as string : null;
            return frame.GetType().FullName + " '" + caption + "' pane=" + (GetDocView(frame)?.GetType().FullName ?? "(none)");
        }

        private static object GetDocView(IVsWindowFrame frame)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return ErrorHandler.Succeeded(frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out object view)) ? view : null;
        }

        private static bool IsTerminalFrame(IVsWindowFrame frame)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string typeName = GetDocView(frame)?.GetType().FullName;
            return typeName != null && typeName.StartsWith(TerminalPaneTypePrefix, StringComparison.Ordinal);
        }

        private static IVsWindowFrame FindNewTerminalFrame(IVsUIShell uiShell, HashSet<object> existingPanes, Guid terminalId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            IVsWindowFrame onlyNew = null;
            int newCount = 0;
            foreach (IVsWindowFrame frame in ToolWindowFrames(uiShell))
            {
                object pane = GetDocView(frame);
                if (pane == null || existingPanes.Contains(pane) || !IsTerminalFrame(frame))
                {
                    continue;
                }

                if (TryReadTerminalGuid(pane, out Guid paneId) && paneId == terminalId)
                {
                    return frame;
                }

                onlyNew = frame;
                newCount++;
            }

            return newCount == 1 ? onlyNew : null;
        }

        /// <summary>
        /// TerminalWindowBase keeps the terminal's guid in a non-public TerminalWindowGuid property. It is an
        /// implementation detail that may change or throw between Visual Studio builds, so a failed read just
        /// means "unknown" and the caller falls back to the only-new-frame rule.
        /// </summary>
        private static bool TryReadTerminalGuid(object pane, out Guid terminalId)
        {
            terminalId = Guid.Empty;
            try
            {
                object value = pane.GetType().GetProperty("TerminalWindowGuid",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(pane);
                return value != null && Guid.TryParse(value.ToString(), out terminalId);
            }
            catch (Exception ex)
            {
                Exception inner = ex.GetBaseException();
                Log.Write("split: could not read TerminalWindowGuid: " + inner.GetType().Name + ": " + inner.Message);
                return false;
            }
        }

        private void ReportFailure(string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Log.Write(message);
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
