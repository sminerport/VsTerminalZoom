# CLAUDE.md

Classic VSIX (VS SDK 17.14, .NET Framework 4.8) that gives the Visual Studio 2026 integrated terminal font zoom, a Shift+Enter newline, new/close/split terminal, focus switching, keyboard scrollback and Ctrl+letter passthrough. README.md explains each command and why it exists; keep it in step with the code.

## Build and install

- Build: `MSBuild.exe VsTerminalZoom.sln /restore /t:Rebuild /p:Configuration=Release /nowarn:MSB3277`. Output is `bin\Release\VsTerminalZoom.vsix`. The MSB3277 StreamJsonRpc conflicts come from the Terminal reference and are noise.
- Bump `Version` in `source.extension.vsixmanifest` for every build you install, and build with `/t:Rebuild` afterwards: an incremental build can package the previous manifest.
- Install with Visual Studio closed: `VSIXInstaller.exe /quiet bin\Release\VsTerminalZoom.vsix`. While devenv.exe runs it aborts after about a minute with `BlockingProcessesException` (log in `%TEMP%\dd_VSIXInstaller_*.log`). Start Visual Studio only after VSIXInstaller.exe has exited.
- `Microsoft.VisualStudio.Terminal.dll` is not on NuGet; the csproj references the installed copy through `$(VsIdeDir)`.
- There are no tests. Verify in Visual Studio: every command writes its steps to `%LOCALAPPDATA%\VsTerminalZoom\VsTerminalZoom.log`, so read that file instead of judging from the screen.

## How it works

- `VsTerminalZoomPackage.cs` registers the commands; `VsTerminalZoom.vsct` declares them and their key bindings. A new command needs an id constant in the package, a `Button`, `KeyBinding` and `IDSymbol` in the vsct, a README table row and a version bump.
- Key scopes: everything is bound in `guidTerminalScope` (the Terminal tool window) except `Terminal.New`, which is Global. A Terminal-scope binding beats Visual Studio's Global one; that is how the `Terminal.SendCtrl<letter>` commands take keys back. Their ids are `CmdIdFirstPassthrough` plus the index into `PassthroughLetters`, and the vsct symbols must stay in the same order.
- Writing to the terminal (`TerminalInputSender`): the view `Microsoft.VisualStudio.Terminal.TerminalControl` has a public `WriteInput(string)`. It lives in the tool window's own `HwndSource`, not under `Application.Current.Windows`; find it by climbing from `GetFocus()` through `GetParent()`.
- Docking (`FrameDocker`): there is no public API. It reflects into `Microsoft.VisualStudio.Shell.ViewManager`: `WindowFrame.FrameView`, then `DockOperations.Dock(target, docking, DockDirection)` with `Right` for a split and `Fill` for a tab. Find that assembly by walking the view's base types to `ViewElement`; the view's own type lives elsewhere.
- Scrolling (`TerminalScroller`): the Windows Terminal WPF control's `ScrollBar` plus the non-public `TerminalContainer.UserScroll(int)`. With no scrollback (alternate-screen programs) the key's xterm sequence is written to the program instead.
- All of this leans on Visual Studio internals. Look members up by reflection at run time, report a failed lookup in the status bar and the log, and keep the part of the command that uses public API working when the internal part fails.
- To inspect Visual Studio's types, non-public members included, use Windows PowerShell 5.1 `[Reflection.Assembly]::ReflectionOnlyLoadFrom` with a `ReflectionOnlyAssemblyResolve` handler over `Common7\IDE`, its `PublicAssemblies` and `PrivateAssemblies`, and `CommonExtensions\Microsoft\Terminal`.

## Branches

- `dev` is the default branch. Branch from `dev`, open the PR into `dev`, merge with a merge commit, and promote with a `dev`-to-`main` PR whose title states the manifest version.
