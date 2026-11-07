using System;
using System.Reflection;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace VsTerminalZoom
{
    /// <summary>
    /// Docks one tool window frame beside another through Visual Studio's docking engine.
    /// The SDK has no public API for relative docking (IVsWindowFrame.SetFramePos only moves and resizes),
    /// so this reaches into Microsoft.VisualStudio.Shell.ViewManager.dll by reflection:
    /// WindowFrame.FrameView gives the layout View, and
    /// DockOperations.Dock(ViewElement target, ViewElement docking, DockDirection direction) places it.
    /// Everything is looked up at run time and fails soft (exception) when the internals change.
    /// </summary>
    internal static class FrameDocker
    {
        private const string DockOperationsTypeName = "Microsoft.VisualStudio.PlatformUI.Shell.DockOperations";
        private const string DockDirectionTypeName = "Microsoft.VisualStudio.PlatformUI.Shell.DockDirection";
        private const string ViewElementTypeName = "Microsoft.VisualStudio.PlatformUI.Shell.ViewElement";

        /// <summary>Docks <paramref name="docking"/> to the right (or left) of <paramref name="target"/>.</summary>
        public static void DockBeside(IVsWindowFrame target, IVsWindowFrame docking, bool right)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            object targetView = GetFrameView(target);
            object dockingView = GetFrameView(docking);
            Assembly viewManager = targetView.GetType().Assembly;

            Type dockOperations = viewManager.GetType(DockOperationsTypeName, true);
            Type dockDirection = viewManager.GetType(DockDirectionTypeName, true);
            Type viewElement = viewManager.GetType(ViewElementTypeName, true);
            object direction = Enum.Parse(dockDirection, right ? "Right" : "Left");

            MethodInfo dock = dockOperations.GetMethod(
                "Dock",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { viewElement, viewElement, dockDirection },
                null) ?? throw new MissingMethodException(DockOperationsTypeName, "Dock(ViewElement, ViewElement, DockDirection)");

            // Give the new view the current one's docked width so the split comes out even.
            PropertyInfo dockedWidth = viewElement.GetProperty("DockedWidth", BindingFlags.Public | BindingFlags.Instance);
            try
            {
                dockedWidth?.SetValue(dockingView, dockedWidth.GetValue(targetView));
            }
            catch (Exception)
            {
                // Cosmetic only.
            }

            // The new terminal starts as a tab in the same group as the target; pull it out first, the way
            // the drag-and-drop path does before it calls Dock.
            MethodInfo detach = viewElement.GetMethod("Detach", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            detach?.Invoke(dockingView, null);

            dock.Invoke(null, new[] { targetView, dockingView, direction });
        }

        private static object GetFrameView(IVsWindowFrame frame)
        {
            Type frameType = frame.GetType();
            PropertyInfo frameView = frameType.GetProperty("FrameView", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new MissingMemberException(frameType.FullName, "FrameView");
            return frameView.GetValue(frame) ?? throw new InvalidOperationException("window frame has no layout view");
        }
    }
}
