using System;
using System.Reflection;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace VsTerminalZoom
{
    /// <summary>
    /// Places one tool window frame relative to another through Visual Studio's docking engine.
    /// The SDK has no public API for relative docking (IVsWindowFrame.SetFramePos only moves and resizes),
    /// so this reaches into Microsoft.VisualStudio.Shell.ViewManager.dll by reflection:
    /// WindowFrame.FrameView gives the layout View, and
    /// DockOperations.Dock(ViewElement target, ViewElement docking, DockDirection direction) places it
    /// (Left/Right/Top/Bottom beside the target, Fill as a tab in the target's group).
    /// Everything is looked up at run time, every step is written to <see cref="Log"/>, and the methods throw
    /// when the engine leaves the view somewhere else.
    /// </summary>
    internal static class FrameDocker
    {
        private const string ShellNamespace = "Microsoft.VisualStudio.PlatformUI.Shell.";
        private const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static;
        private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

        /// <summary>Docks <paramref name="docking"/> to the right (or left) of <paramref name="target"/>. Returns how.</summary>
        public static string DockBeside(IVsWindowFrame target, IVsWindowFrame docking, bool right)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var engine = new Engine(target, docking);
            object direction = engine.Direction(right ? "Right" : "Left");
            engine.LogStart("dock");
            engine.CopyDockedWidth();

            string how = null;

            // Attempt 1: hand the attached view straight to Dock, the way a drop target does.
            engine.Dock(direction);
            if (engine.IsBeside())
            {
                how = "docked directly";
            }
            else
            {
                Log.Write("dock: direct Dock left the view in " + Describe(engine.DockingParent));

                // Attempt 2: detach from its group first, then Dock.
                engine.Detach();
                engine.Dock(direction);
                if (engine.IsBeside())
                {
                    how = "docked after detach";
                }
                else if (engine.CanDockOutside)
                {
                    Log.Write("dock: Dock after Detach left the view in " + Describe(engine.DockingParent) + "; trying DockOutside");

                    // Attempt 3: dock outside the target's whole group.
                    engine.DockOutsideTargetGroup(direction);
                    if (engine.IsBeside())
                    {
                        how = "docked outside the target group";
                    }
                }
            }

            if (how == null)
            {
                throw new InvalidOperationException("the docking engine left the new view in " + Describe(engine.DockingParent));
            }

            Log.Write("dock: " + how + "; docking parent now " + Describe(engine.DockingParent) + ", target parent " + Describe(engine.TargetParent));
            engine.ShowDockingInFront();
            return how;
        }

        /// <summary>
        /// Puts <paramref name="docking"/> in the same tab group as <paramref name="target"/>, so it shows up as a
        /// tab in the target's pane. Visual Studio otherwise opens a new terminal wherever the last one was docked,
        /// which after a split means beside the others instead of as a tab. Returns how.
        /// </summary>
        public static string DockAsTab(IVsWindowFrame target, IVsWindowFrame docking)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var engine = new Engine(target, docking);
            engine.LogStart("tab");

            if (engine.SharesTabGroup())
            {
                engine.ShowDockingInFront();
                return "already a tab in the target's pane";
            }

            object fill = engine.Direction("Fill");
            string how = null;

            engine.Dock(fill);
            if (engine.SharesTabGroup())
            {
                how = "tabbed directly";
            }
            else
            {
                Log.Write("tab: direct Dock(Fill) left the view in " + Describe(engine.DockingParent));
                engine.Detach();
                engine.Dock(fill);
                if (engine.SharesTabGroup())
                {
                    how = "tabbed after detach";
                }
            }

            if (how == null)
            {
                throw new InvalidOperationException("the docking engine left the new view in " + Describe(engine.DockingParent));
            }

            Log.Write("tab: " + how + "; both in " + Describe(engine.DockingParent));
            engine.ShowDockingInFront();
            return how;
        }

        /// <summary>The reflected docking-engine members plus the two views being arranged.</summary>
        private sealed class Engine
        {
            private readonly object targetView;
            private readonly object dockingView;
            private readonly Type dockDirection;
            private readonly Type viewElement;
            private readonly Type viewGroup;
            private readonly PropertyInfo parentProperty;
            private readonly MethodInfo dock;
            private readonly MethodInfo dockOutside;
            private readonly MethodInfo isDockingViewValid;
            private readonly MethodInfo detach;
            private readonly object originalTargetParent;

            public Engine(IVsWindowFrame target, IVsWindowFrame docking)
            {
                targetView = GetFrameView(target);
                dockingView = GetFrameView(docking);
                Assembly viewManager = FindViewManagerAssembly(targetView.GetType());

                Type dockOperations = viewManager.GetType(ShellNamespace + "DockOperations", true);
                dockDirection = viewManager.GetType(ShellNamespace + "DockDirection", true);
                viewElement = viewManager.GetType(ShellNamespace + "ViewElement", true);
                viewGroup = viewManager.GetType(ShellNamespace + "ViewGroup", true);

                parentProperty = viewElement.GetProperty("Parent", PublicInstance)
                    ?? throw new MissingMemberException(viewElement.FullName, "Parent");
                dock = dockOperations.GetMethod("Dock", PublicStatic, null, new[] { viewElement, viewElement, dockDirection }, null)
                    ?? throw new MissingMethodException(dockOperations.FullName, "Dock(ViewElement, ViewElement, DockDirection)");
                dockOutside = dockOperations.GetMethod("DockOutside", PublicStatic, null, new[] { viewGroup, viewElement, dockDirection }, null);
                isDockingViewValid = dockOperations.GetMethod("IsDockingViewValid", PublicStatic, null, new[] { viewElement }, null);
                detach = viewElement.GetMethod("Detach", PublicInstance, null, Type.EmptyTypes, null);
                originalTargetParent = parentProperty.GetValue(targetView);
            }

            public object TargetParent => parentProperty.GetValue(targetView);

            public object DockingParent => parentProperty.GetValue(dockingView);

            public bool CanDockOutside => dockOutside != null && originalTargetParent != null && viewGroup.IsInstanceOfType(originalTargetParent);

            public object Direction(string name) => Enum.Parse(dockDirection, name);

            public void LogStart(string verb)
            {
                Log.Write(verb + ": target " + Describe(targetView) + " in " + Describe(TargetParent)
                    + "; docking " + Describe(dockingView) + " in " + Describe(DockingParent)
                    + "; IsDockingViewValid=" + InvokeStatic(isDockingViewValid, dockingView));
            }

            public void Dock(object direction) => dock.Invoke(null, new[] { targetView, dockingView, direction });

            public void DockOutsideTargetGroup(object direction) => dockOutside.Invoke(null, new[] { originalTargetParent, dockingView, direction });

            public void Detach() => detach?.Invoke(dockingView, null);

            /// <summary>True when the docking view sits in a group of its own rather than the target's.</summary>
            public bool IsBeside()
            {
                object dockingParent = DockingParent;
                return dockingParent != null && !ReferenceEquals(dockingParent, TargetParent);
            }

            /// <summary>True when both views are tabs of one tab group.</summary>
            public bool SharesTabGroup()
            {
                object dockingParent = DockingParent;
                return dockingParent != null
                    && ReferenceEquals(dockingParent, TargetParent)
                    && dockingParent.GetType().Name.IndexOf("TabGroup", StringComparison.Ordinal) >= 0;
            }

            /// <summary>Gives the new view the current one's docked width so a split comes out even.</summary>
            public void CopyDockedWidth()
            {
                PropertyInfo dockedWidth = viewElement.GetProperty("DockedWidth", PublicInstance);
                try
                {
                    dockedWidth?.SetValue(dockingView, dockedWidth.GetValue(targetView));
                }
                catch (Exception ex)
                {
                    Log.Write("dock: could not copy DockedWidth: " + ex.GetBaseException().Message);
                }
            }

            public void ShowDockingInFront()
            {
                MethodInfo showInFront = dockingView.GetType().GetMethod("ShowInFront", PublicInstance, null, Type.EmptyTypes, null);
                showInFront?.Invoke(dockingView, null);
            }
        }

        private static object InvokeStatic(MethodInfo method, object argument)
        {
            try
            {
                return method == null ? "n/a" : method.Invoke(null, new[] { argument });
            }
            catch (Exception ex)
            {
                return "threw " + ex.GetBaseException().GetType().Name;
            }
        }

        private static string Describe(object element)
        {
            if (element == null)
            {
                return "(null)";
            }

            Type type = element.GetType();
            string title = null;
            try
            {
                title = type.GetProperty("Title", PublicInstance)?.GetValue(element)?.ToString();
            }
            catch (Exception)
            {
            }

            string children = null;
            try
            {
                object items = type.GetProperty("Children", PublicInstance)?.GetValue(element);
                if (items is System.Collections.ICollection collection)
                {
                    children = collection.Count.ToString();
                }
            }
            catch (Exception)
            {
            }

            return type.Name
                + (string.IsNullOrEmpty(title) ? string.Empty : " '" + title + "'")
                + (children == null ? string.Empty : " [" + children + " children]")
                + " #" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(element).ToString("x");
        }

        /// <summary>
        /// The frame's view is a WindowManagement subclass of View, so its own assembly is the wrong one;
        /// walk up to ViewElement, which lives in Microsoft.VisualStudio.Shell.ViewManager next to DockOperations.
        /// </summary>
        private static Assembly FindViewManagerAssembly(Type viewType)
        {
            for (Type type = viewType; type != null; type = type.BaseType)
            {
                if (type.FullName == ShellNamespace + "ViewElement")
                {
                    return type.Assembly;
                }
            }

            foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (loaded.GetName().Name == "Microsoft.VisualStudio.Shell.ViewManager")
                {
                    return loaded;
                }
            }

            throw new TypeLoadException("Microsoft.VisualStudio.Shell.ViewManager is not loaded; the frame view is a " + viewType.FullName);
        }

        private static object GetFrameView(IVsWindowFrame frame)
        {
            Type frameType = frame.GetType();
            PropertyInfo frameView = frameType.GetProperty("FrameView", PublicInstance)
                ?? throw new MissingMemberException(frameType.FullName, "FrameView");
            return frameView.GetValue(frame) ?? throw new InvalidOperationException("window frame has no layout view");
        }
    }
}
