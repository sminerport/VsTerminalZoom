# VsTerminalZoom

Keyboard font zoom, a Shift+Enter newline and new/close/split-terminal, focus and scrollback keys for the Visual Studio 2026 integrated terminal, matching
the VS Code terminal bindings used in `keybindings.json`:

| Key (terminal focused) | VS Code command                                   | Visual Studio command     |
| ---------------------- | ------------------------------------------------- | ------------------------- |
| `Ctrl+Alt+=`           | `workbench.action.terminal.fontZoomIn`            | `Terminal.FontZoomIn`     |
| `Ctrl+Alt+-`           | `workbench.action.terminal.fontZoomOut`           | `Terminal.FontZoomOut`    |
| `Ctrl+Alt+0`           | `workbench.action.terminal.fontZoomReset`         | `Terminal.FontZoomReset`  |
| `Shift+Enter`          | `terminal.sendSequence` `"\u001b\r"` (`/terminal-setup`) | `Terminal.SendShiftEnter` |
| `Ctrl+Shift+``         | `workbench.action.terminal.new`                   | `Terminal.New`            |
| `Ctrl+Shift+W`         | `workbench.action.terminal.kill`                  | `Terminal.Close`          |
| `Ctrl+\`               | `workbench.action.terminal.split`                 | `Terminal.Split`          |
| `Ctrl+.` / `Ctrl+,`    | `terminal.focusNextPane` / `focusPreviousPane`    | `Terminal.FocusNextPane` / `FocusPreviousPane` |
| `Ctrl+Shift+.` / `Ctrl+Shift+,` | `terminal.focusNext` / `focusPrevious`   | `Terminal.FocusNext` / `FocusPrevious` |
| `Ctrl+Shift+Up` / `Down` | `terminal.scrollUp` / `scrollDown` (Ctrl+Alt+PgUp/PgDn by default) | `Terminal.ScrollLineUp` / `ScrollLineDown` |
| `Ctrl+Shift+PgUp` / `PgDn` | `terminal.scrollUpPage` / `scrollDownPage` (Shift+PgUp/PgDn by default) | `Terminal.ScrollPageUp` / `ScrollPageDown` |
| `Ctrl+Shift+Home` / `End` | `terminal.scrollToTop` / `scrollToBottom`         | `Terminal.ScrollToTop` / `ScrollToBottom` |
| `Ctrl+E`, `Ctrl+F`, `Ctrl+G`, `Ctrl+K`, `Ctrl+N`, `Ctrl+O`, `Ctrl+P`, `Ctrl+R`, `Ctrl+S`, `Ctrl+T`, `Ctrl+W`, `Ctrl+Y` | sent to the shell by default | `Terminal.SendCtrl<letter>` |

## New terminal

Visual Studio has no bindable command behind the Terminal window's "+" button, so `Terminal.New` (Global scope,
Ctrl+Shift+`) calls the public `SVsTerminalService`: `GetDefaultProfileAsync`, then `CreateTerminalWindowAsync`
and `ShowAsync`. The default profile is the one marked default in Tools > Options > Environment > Terminal.

The new terminal is then tabbed into the focused terminal's pane, or one already on screen, with
`DockOperations.Dock(target, new, DockDirection.Fill)`. Left alone, Visual Studio opens a new terminal wherever
the last one was docked, which after a `Terminal.Split` means beside the others instead of as a tab.

The contracts assembly `Microsoft.VisualStudio.Terminal.dll` is not on NuGet; the project references the
installed copy (`$(VSAPPIDDIR)` from a Developer PowerShell, falling back to the Community install path).
Windows Terminal gets the same key through a `Terminal.OpenNewTab` keybinding in its settings.json.

## Split terminal

`Terminal.Split` (Terminal scope, Ctrl+\) opens a new default-profile terminal and docks it to the right of the
focused one. Visual Studio has no public API for docking one tool window beside another (`IVsWindowFrame.SetFramePos`
only moves and resizes), so `FrameDocker` reaches into the docking engine by reflection:
`WindowFrame.FrameView` gives the layout `View`, and `DockOperations.Dock(target, docking, DockDirection.Right)` in
`Microsoft.VisualStudio.Shell.ViewManager.dll` places it. The new terminal's frame is found by diffing the tool
window list before and after creation (and by its `TerminalWindowGuid`). If the docking step fails, the new
terminal is still opened as a tab and the status bar says why.

## Switch between terminals

`Terminal.FocusNextPane` / `FocusPreviousPane` (Ctrl+. / Ctrl+,) cycle through the terminals currently on screen,
sorted by screen position, so two split panes toggle. `Terminal.FocusNext` / `FocusPrevious` (Ctrl+Shift+. /
Ctrl+Shift+,) cycle through every open terminal, hidden tabs included. All four are Terminal scope only, so
Ctrl+, keeps opening Go To All and Ctrl+Shift+. / Ctrl+Shift+, keep switching document tabs everywhere else,
exactly like the `terminalFocus` condition on the VS Code bindings.

## Scroll the scrollback

Visual Studio's terminal only scrolls with the mouse wheel. `Terminal.ScrollLineUp` / `ScrollLineDown`
(Ctrl+Shift+Up / Down), `ScrollPageUp` / `ScrollPageDown` (Ctrl+Shift+PgUp / PgDn) and `ScrollToTop` /
`ScrollToBottom` (Ctrl+Shift+Home / End) add Windows Terminal's keys. They drive the Windows Terminal control's
own scroll bar and its `TerminalContainer.UserScroll(viewTop)`, the path the scroll bar's thumb uses. When the
terminal has no scrollback, which is what alternate-screen programs such as Claude Code's fullscreen renderer
or vim present, the key's xterm sequence (`ESC [1;6A` and friends) is written to the program instead, so the same
keys scroll Claude Code's own viewport there.

## Keys Visual Studio would otherwise take

Visual Studio binds many Ctrl+letter keys globally, and those bindings win even while the terminal has focus:
Ctrl+R is a chord prefix, Ctrl+S saves, Ctrl+T opens Go To All, Ctrl+F opens Find, Ctrl+O opens a file. The
program in the terminal never sees them, so PSReadLine's history search, Claude Code's `Ctrl+R` rename in
`claude agents`, `Ctrl+O` transcript and `Ctrl+T` task list all appear dead.

There is one `Terminal.SendCtrl<letter>` command for each of Ctrl+E, Ctrl+F, Ctrl+G, Ctrl+K, Ctrl+N, Ctrl+O,
Ctrl+P, Ctrl+R, Ctrl+S, Ctrl+T, Ctrl+W and Ctrl+Y. Each is bound to its key in the Terminal scope, where it
beats the Global binding, and writes the key's control character (Ctrl+R is 0x12) to the pty through the same
`WriteInput` path as `Terminal.SendShiftEnter`. Outside the terminal the keys keep their Visual Studio meaning.
To give one back to Visual Studio inside the terminal, remove its shortcut under Tools > Options > Environment >
Keyboard (search `Terminal.SendCtrl`).

## Close terminal

Each terminal is its own tool window pane, so `Terminal.Close` (Terminal scope, Ctrl+Shift+W) runs the shell's
`Window.CloseToolWindow` command on the focused one, which closes the pane and ends its process, like VS Code's
`terminal.kill`. The Terminal-scope binding wins over the Global Ctrl+Shift+W = `Window.CloseDocumentWindow`, so
outside the terminal that key still closes documents. Windows Terminal closes a tab with Ctrl+Shift+W by default.

## Shift+Enter

Claude Code reads ESC CR as "newline, don't submit". Windows Terminal 1.25+ speaks the kitty keyboard
protocol, so Shift+Enter works there with no setup; VS Code gets a `sendSequence` binding from
`/terminal-setup`. Visual Studio ships the Windows Terminal 1.22 control, which has no kitty support
and no key-to-sequence mapping, so plain Shift+Enter reaches Claude Code as Enter and submits.
`Terminal.SendShiftEnter` finds the focused terminal view and writes ESC CR into it through its
public `WriteInput` method (the view is the Windows Terminal control's `ITerminalConnection`, so this is the
same path typed keys take). The view lives in the tool window's own `HwndSource`, not in
`Application.Current.Windows`; 1.1.0 searched the wrong tree, found nothing and silently swallowed the key.
1.1.1 climbs from the focused HWND to that source and reports failures in the status bar.

Trade-off, identical to the VS Code binding: in a plain PowerShell prompt the sequence arrives as
Esc (PSReadLine `RevertLine`, clears the line) followed by Enter, so PSReadLine's own Shift+Enter
(`AddLine`) no longer works in the VS terminal. Unbind `Terminal.SendShiftEnter` in Tools > Options >
Environment > Keyboard if that matters more than the Claude Code newline.

Visual Studio has no built-in terminal zoom: its terminal is the Windows Terminal WPF control, whose
host only scrolls on the mouse wheel and only takes a font from Tools > Options > Environment >
Fonts and Colors > Terminal. This extension changes that Fonts and Colors entry through
`IVsFontAndColorStorage`; the terminal listens for font changes and redraws at once.

Differences from VS Code: the size is persisted (it is the Options value), and Reset restores the size
that was in effect before the first zoom rather than a separate "configured" size. Set a new base
size in Options after a Reset if you want one.

The bindings use the Terminal key scope, so they only fire while the terminal has focus. Rebind them
under Tools > Options > Environment > Keyboard (search `Terminal.FontZoom`). The commands also appear
at the bottom of the Tools menu.

## Troubleshooting

Every command writes what it did to `%LOCALAPPDATA%\VsTerminalZoom\VsTerminalZoom.log` (one line per step, with the
docking engine's view and group types for `Terminal.Split`). Failures also go to the status bar and, when Visual
Studio runs with `/log`, to its ActivityLog.

## Build

Requires the "Visual Studio extension development" workload. From a Developer PowerShell:

```powershell
msbuild VsTerminalZoom.sln /restore /p:Configuration=Release /v:m
```

Output: `bin\Release\VsTerminalZoom.vsix`.

## Install / uninstall

```powershell
& "$env:VSAPPIDDIR\VSIXInstaller.exe" bin\Release\VsTerminalZoom.vsix
```

Close Visual Studio first: `VSIXInstaller /quiet` aborts with `BlockingProcessesException` while devenv.exe runs,
and the interactive installer waits for it. Start Visual Studio again only after the installer has exited, so it
sees the `extensions.configurationchanged` marker and merges the new key bindings. Remove it from Extensions > Manage Extensions > Installed, or with
`VSIXInstaller.exe /uninstall:VsTerminalZoom.sminerport.6f3b9a21-8c4d-4e07-9b5a-2d1e7c8f0a43`.
