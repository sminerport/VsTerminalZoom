using System;
using System.IO;

namespace VsTerminalZoom
{
    /// <summary>
    /// Appends one line per event to %LOCALAPPDATA%\VsTerminalZoom\VsTerminalZoom.log. Visual Studio only
    /// writes its ActivityLog when started with /log, and status bar text is overwritten within seconds, so
    /// this is the place to look when a command did not do what you expected.
    /// </summary>
    internal static class Log
    {
        public static readonly string Path = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VsTerminalZoom", "VsTerminalZoom.log");

        public static void Write(string message)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                File.AppendAllText(Path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine);
            }
            catch (Exception)
            {
                // Logging must never break a command.
            }
        }
    }
}
