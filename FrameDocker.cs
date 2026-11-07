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
    /// Everything is looked up at run time, every step is written to <see cref="Log"/>, and the method throws
    /// when the engine leaves the view where it was.
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

            object targetView = GetFrameView(target);
            object dockingView = GetFrameView(docking);
            Assembly viewManager = FindViewManagerAssembly(targetView.GetType());

            Type dockOperations = viewManager.GetType(ShellNamespace + "DockOperations", true);
            Type dockDirection = viewManager.GetType(ShellNamespace + "DockDirection", true);
            Type viewElement = viewManager.GetType(ShellNamespace + "ViewElement", true);
            Type viewGroup = viewManager.GetType(ShellNamespace + "ViewGroup", true);
            object direction = Enum.Parse(dockDirection, right ? "Right" : "Left");

            PropertyInfo parentProperty = viewElement.GetProperty("Parent", PublicInstance)
                ?? throw new MissingMemberException(viewElement.FullName, "Parent");
            MethodInfo dock = dockOperations.GetMethod("Dock", PublicStatic, null, new[] { viewElement, viewElement, dockDirection }, null)
                ?? throw new MissingMethodException(dockOperations.FullName, "Dock(ViewElement, ViewElement, DockDirection)");
            MethodInfo dockOutside = dockOperations.GetMethod("DockOutside", PublicStatic, null, new[] { viewGroup, viewElement, dockDirection }, null);
            MethodInfo isDockingViewValid = dockOperations.GetMethod("IsDockingViewValid", PublicStatic, null, new[] { viewElement }, null);
            MethodInfo detach = viewElement.GetMethod("Detach", PublicInstance, null, Type.EmptyTypes, null);
            MethodInfo showInFront = targetView.GetType().GetMethod("ShowInFront", PublicInstance, null, Type.EmptyTypes, null);

            object targetParent = parentProperty.GetValue(targetView);
            Log.Write("dock: target " + Describe(targetView) + " in " + Describe(targetParent)
                + "; docking " + Describe(dockingView) + " in " + Describe(parentProperty.GetValue(dockingView))
                + "; IsDockingViewValid=" + Invoke(isDockingViewValid, dockingView));

            CopyDockedWidth(viewElement, targetView, dockingView);

            string how = null;

            // Attempt 1: hand the attached view straight to Dock, the way a drop target does.
            dock.Invoke(null, new[] { targetView, dockingView, direction });
            if (IsBeside(parentProperty, targetView, dockingView))
            {
                how = "docked directly";
            }
            else
            {
                Log.Write("dock: direct Dock left the view in " + Describe(parentProperty.GetValue(dockingView)));

                // Attempt 2: detach from its tab group first, then Dock.
                detach?.Invoke(dockingView, null);
                Log.Write("dock: after Detach parent=" + Describe(parentProperty.GetValue(dockingView))
                    + " IsDockingViewValid=" + Invoke(isDockingViewValid, dockingView));
                dock.Invoke(null, new[] { targetView, dockingView, direction });
                if (IsBeside(parentProperty, targetView, dockingView))
                {
                    how = "docked after detach";
                }
                else if (dockOutside != null && targetParent != null && viewGroup.IsInstanceOfType(targetParent))
                {
                    Log.Write("dock: Dock after Detach left the view in " + Describe(parentProperty.GetValue(dockingView)) + "; trying DockOutside");

                    // Attempt 3: dock outside the target's whole group.
                    dockOutside.Invoke(null, new[] { targetParent, dockingView, direction });
                    if (IsBeside(parentProperty, targetView, dockingView))
                    {
                        how = "docked outside the target group";
                    }
                }
            }

            if (how == null)
            {
                throw new InvalidOperationException("the docking engine left the new view in " + Describe(parentProperty.GetValue(dockingView)));
            }

            Log.Write("dock: " + how + "; docking parent now " + Describe(parentProperty.GetValue(dockingView))
                + ", target parent " + Describe(parentProperty.GetValue(targetView)));
            showInFront?.Invoke(dockingView, null);
            return how;
        }

        private static bool IsBeside(PropertyInfo parentProperty, object targetView, object dockingView)
        {
            object dockingParent = parentProperty.GetValue(dockingView);
            return dockingParent != null && !ReferenceEquals(dockingParent, parentProperty.GetValue(targetView));
        }

        private static void CopyDockedWidth(Type viewElement, object targetView, object dockingView)
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

        private static object Invoke(MethodInfo method, object argument)
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
