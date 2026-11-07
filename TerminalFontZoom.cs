using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace VsTerminalZoom
{
    /// <summary>
    /// Changes the point size of the "Terminal" entry in Tools > Options > Environment > Fonts and Colors
    /// through IVsFontAndColorStorage. The terminal tool window listens for font changes
    /// (IVsFontAndColorEvents) and re-themes itself immediately, so this behaves like a zoom.
    /// Unlike VS Code's in-memory zoom the new size is persisted; Reset restores the size that was
    /// in effect before the first zoom.
    /// </summary>
    internal sealed class TerminalFontZoom
    {
        // Registered by Microsoft.VisualStudio.Terminal.Implementation.pkgdef ([$RootKey$\FontAndColors\Terminal]).
        private static readonly Guid TerminalCategory = new Guid("24689d95-4f79-475f-81f0-d6e351d7bcd0");

        private const string CollectionPath = "VsTerminalZoom";
        private const string BaselineProperty = "BaselinePointSize";
        private const int MinPointSize = 4;
        private const int MaxPointSize = 72;

        private readonly IVsFontAndColorStorage storage;
        private readonly WritableSettingsStore settings;

        public TerminalFontZoom(IVsFontAndColorStorage storage, WritableSettingsStore settings)
        {
            this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>Grows (positive delta) or shrinks (negative delta) the terminal font by that many points.</summary>
        public void Adjust(int delta)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            FontInfo font = ReadFont();
            RememberBaseline(font.wPointSize);
            int size = Math.Max(MinPointSize, Math.Min(MaxPointSize, font.wPointSize + delta));
            if (size != font.wPointSize)
            {
                WriteFont(font, (ushort)size);
            }
        }

        /// <summary>Restores the point size recorded before the first Adjust call, then forgets it.</summary>
        public void Reset()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!settings.CollectionExists(CollectionPath) || !settings.PropertyExists(CollectionPath, BaselineProperty))
            {
                return;
            }

            int baseline = settings.GetInt32(CollectionPath, BaselineProperty);
            FontInfo font = ReadFont();
            if (baseline >= MinPointSize && baseline <= MaxPointSize && baseline != font.wPointSize)
            {
                WriteFont(font, (ushort)baseline);
            }

            settings.DeleteProperty(CollectionPath, BaselineProperty);
        }

        private void RememberBaseline(ushort current)
        {
            if (!settings.CollectionExists(CollectionPath))
            {
                settings.CreateCollection(CollectionPath);
            }

            if (!settings.PropertyExists(CollectionPath, BaselineProperty))
            {
                settings.SetInt32(CollectionPath, BaselineProperty, current);
            }
        }

        private FontInfo ReadFont()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Guid category = TerminalCategory;
            uint flags = (uint)(__FCSTORAGEFLAGS.FCSF_LOADDEFAULTS | __FCSTORAGEFLAGS.FCSF_READONLY);
            ErrorHandler.ThrowOnFailure(storage.OpenCategory(ref category, flags));
            try
            {
                var logFont = new LOGFONTW[1];
                var info = new FontInfo[1];
                ErrorHandler.ThrowOnFailure(storage.GetFont(logFont, info));
                return info[0];
            }
            finally
            {
                storage.CloseCategory();
            }
        }

        private void WriteFont(FontInfo font, ushort pointSize)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            font.wPointSize = pointSize;
            font.bFaceNameValid = 1;
            font.bPointSizeValid = 1;
            font.bCharSetValid = 1;

            Guid category = TerminalCategory;
            uint flags = (uint)(__FCSTORAGEFLAGS.FCSF_LOADDEFAULTS | __FCSTORAGEFLAGS.FCSF_PROPAGATECHANGES);
            ErrorHandler.ThrowOnFailure(storage.OpenCategory(ref category, flags));
            try
            {
                ErrorHandler.ThrowOnFailure(storage.SetFont(new[] { font }));
            }
            finally
            {
                storage.CloseCategory();
            }
        }
    }
}
