using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;

namespace VsTerminalZoom
{
    /// <summary>
    /// Writes text into the Terminal tool window that currently has keyboard focus, as if the user typed it.
    /// Visual Studio's terminal view (Microsoft.VisualStudio.Terminal.TerminalControl, a public WPF UserControl
    /// in Microsoft.VisualStudio.Terminal.Implementation.dll) is the Windows Terminal control's
    /// ITerminalConnection: its public WriteInput(string) raises UserInputReceived, which the tool window
    /// forwards to the pty. The view is NOT reachable from Application.Current.Windows: VS hosts each tool
    /// window's content in its own HwndSource (HwndContentControl), so the search starts at the focused HWND
    /// (the Windows Terminal child window), climbs to the first ancestor HWND that owns a WPF HwndSource, and
    /// walks that source's visual tree.
    /// </summary>
    internal sealed class TerminalInputSender
    {
        private const string VsTerminalControlTypeName = "Microsoft.VisualStudio.Terminal.TerminalControl";

        [DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

        /// <summary>Why the last <see cref="TrySend"/> returned false, or null.</summary>
        public string LastFailure { get; private set; }

        /// <summary>True when keyboard focus is inside a terminal view.</summary>
        public bool HasFocusedTerminal()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return FindFocusedTerminal() != null;
        }

        /// <summary>Sends <paramref name="text"/> to the focused terminal. Returns false when no terminal was found.</summary>
        public bool TrySend(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            LastFailure = null;

            FrameworkElement terminal = FindFocusedTerminal();
            if (terminal == null)
            {
                LastFailure = "no focused terminal view found";
                return false;
            }

            if (!TryWriteInput(terminal, text, out string failure))
            {
                LastFailure = failure;
                return false;
            }

            return true;
        }

        /// <summary>Feeds <paramref name="text"/> to the terminal view's pty through its public WriteInput(string).</summary>
        internal static bool TryWriteInput(FrameworkElement terminal, string text, out string failure)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            MethodInfo write = terminal.GetType().GetMethod(
                "WriteInput",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(string) },
                null);
            if (write == null)
            {
                failure = terminal.GetType().FullName + " has no public WriteInput(string)";
                return false;
            }

            try
            {
                write.Invoke(terminal, new object[] { text });
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.GetBaseException();
                failure = "WriteInput threw " + inner.GetType().Name + ": " + inner.Message;
                return false;
            }

            failure = null;
            return true;
        }

        internal static FrameworkElement FindFocusedTerminal()
        {
            IntPtr focus = GetFocus();

            // 1. Climb from the focused HWND to the nearest HWND that hosts WPF content (the tool window's
            //    HwndContentControl source) and look for the terminal view in that tree. Hidden terminal tabs
            //    share the tree but are not visible, so the visible one is the active tab.
            for (IntPtr hwnd = focus; hwnd != IntPtr.Zero; hwnd = GetParent(hwnd))
            {
                HwndSource source = HwndSource.FromHwnd(hwnd);
                if (source?.RootVisual == null)
                {
                    continue;
                }

                FrameworkElement terminal = PickTerminal(Descendants(source.RootVisual, VsTerminalControlTypeName), focus, true);
                if (terminal != null)
                {
                    return terminal;
                }
            }

            // 2. Fallback: every WPF source in the process (main window, floating and docked tool windows).
            //    Take a view that owns focus, else the only visible one.
            var all = new List<FrameworkElement>();
            foreach (PresentationSource source in PresentationSource.CurrentSources)
            {
                if (source.RootVisual != null)
                {
                    all.AddRange(Descendants(source.RootVisual, VsTerminalControlTypeName));
                }
            }

            return PickTerminal(all, focus, false);
        }

        private static FrameworkElement PickTerminal(IEnumerable<FrameworkElement> candidates, IntPtr focus, bool sameSource)
        {
            var visible = new List<FrameworkElement>();
            foreach (FrameworkElement candidate in candidates)
            {
                if (candidate.IsKeyboardFocusWithin || OwnsFocus(candidate, focus))
                {
                    return candidate;
                }

                if (candidate.IsVisible)
                {
                    visible.Add(candidate);
                }
            }

            // Inside the focused tool window's own source any visible view is the right one;
            // across the whole process only accept an unambiguous single visible view.
            if (visible.Count == 1 || (sameSource && visible.Count > 0))
            {
                return visible[0];
            }

            return null;
        }

        private static bool OwnsFocus(FrameworkElement terminal, IntPtr focus)
        {
            if (focus == IntPtr.Zero || !terminal.IsVisible)
            {
                return false;
            }

            foreach (HwndHost host in DescendantsOfType<HwndHost>(terminal))
            {
                IntPtr handle = host.Handle;
                if (handle != IntPtr.Zero && (handle == focus || IsChild(handle, focus)))
                {
                    return true;
                }
            }

            return false;
        }

        internal static IEnumerable<FrameworkElement> Descendants(DependencyObject root, string typeFullName)
        {
            foreach (DependencyObject node in Walk(root))
            {
                if (node is FrameworkElement element && element.GetType().FullName == typeFullName)
                {
                    yield return element;
                }
            }
        }

        internal static IEnumerable<T> DescendantsOfType<T>(DependencyObject root) where T : DependencyObject
        {
            foreach (DependencyObject node in Walk(root))
            {
                if (node is T match)
                {
                    yield return match;
                }
            }
        }

        private static IEnumerable<DependencyObject> Walk(DependencyObject root)
        {
            var stack = new Stack<DependencyObject>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                DependencyObject current = stack.Pop();
                yield return current;

                if (!(current is Visual) && !(current is System.Windows.Media.Media3D.Visual3D))
                {
                    continue;
                }

                int count = VisualTreeHelper.GetChildrenCount(current);
                for (int i = count - 1; i >= 0; i--)
                {
                    stack.Push(VisualTreeHelper.GetChild(current, i));
                }
            }
        }
    }
}
