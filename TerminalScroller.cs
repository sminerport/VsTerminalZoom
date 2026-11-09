using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls.Primitives;
using Microsoft.VisualStudio.Shell;

namespace VsTerminalZoom
{
    internal enum ScrollStep
    {
        Line,
        Page,
        Edge,
    }

    /// <summary>
    /// Scrolls the focused terminal's scrollback from the keyboard. The Windows Terminal WPF control inside the
    /// Visual Studio terminal keeps a ScrollBar whose Value is the viewport's top line and whose Maximum is
    /// bufferSize - viewHeight; its TerminalContainer.UserScroll(int viewTop) (non-public) is what the scroll bar's
    /// Scroll event calls, so this drives the same path. A terminal with no scrollback (Maximum == Minimum), which
    /// is what alternate-screen programs such as Claude Code's fullscreen renderer or vim present, gets the key's
    /// VT sequence written to the program instead, so the program scrolls itself.
    /// </summary>
    internal static class TerminalScroller
    {
        private const string ContainerTypeName = "Microsoft.Terminal.Wpf.TerminalContainer";
        private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        /// <summary>Scrolls by <paramref name="step"/> in <paramref name="direction"/> (-1 up, +1 down). Returns what happened.</summary>
        public static string Scroll(ScrollStep step, int direction, string passthroughSequence)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            FrameworkElement terminal = TerminalInputSender.FindFocusedTerminal()
                ?? throw new InvalidOperationException("no focused terminal view");

            ScrollBar bar = null;
            foreach (ScrollBar candidate in TerminalInputSender.DescendantsOfType<ScrollBar>(terminal))
            {
                bar = candidate;
                break;
            }

            FrameworkElement container = null;
            foreach (FrameworkElement candidate in TerminalInputSender.Descendants(terminal, ContainerTypeName))
            {
                container = candidate;
                break;
            }

            if (bar == null || container == null)
            {
                throw new InvalidOperationException("the terminal view has no scroll bar or terminal container");
            }

            double min = bar.Minimum;
            double max = bar.Maximum;
            double current = bar.Value;
            if (max <= min)
            {
                if (!TerminalInputSender.TryWriteInput(terminal, passthroughSequence, out string failure))
                {
                    throw new InvalidOperationException(failure);
                }

                return "no scrollback here; key passed to the program";
            }

            double rows = bar.ViewportSize > 0 ? bar.ViewportSize : ReadRows(container);
            double target;
            switch (step)
            {
                case ScrollStep.Line:
                    target = current + direction;
                    break;
                case ScrollStep.Page:
                    target = current + direction * Math.Max(1, rows - 1);
                    break;
                default:
                    target = direction < 0 ? min : max;
                    break;
            }

            target = Math.Max(min, Math.Min(max, Math.Round(target)));

            MethodInfo userScroll = container.GetType().GetMethod("UserScroll", AnyInstance, null, new[] { typeof(int) }, null)
                ?? throw new MissingMethodException(container.GetType().FullName, "UserScroll(int)");
            userScroll.Invoke(container, new object[] { (int)target });
            bar.Value = target;

            return "scrolled to line " + (int)target + " of " + (int)max;
        }

        private static double ReadRows(object container)
        {
            PropertyInfo rows = container.GetType().GetProperty("Rows", AnyInstance);
            try
            {
                return rows != null ? Convert.ToDouble(rows.GetValue(container)) : 24;
            }
            catch (Exception)
            {
                return 24;
            }
        }
    }
}
