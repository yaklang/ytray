#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using YTray.Core;

namespace YTray.Native
{
    /// <summary>
    /// Applies Windows 11 DWM immersive dark title-bar and rounded window corners
    /// dynamically across theme transitions without failing on older Windows 10 builds.
    /// </summary>
    internal static class WindowChromeHelper
    {
        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaUseImmersiveDarkModeLegacy = 19;
        private const int DwmwaWindowCornerPreference = 33;
        private const int DwmwcpRound = 2;

        internal static void Attach(Window window, bool roundCorners = true)
        {
            if (window == null) return;

            void Apply()
            {
                var handle = new WindowInteropHelper(window).Handle;
                if (handle == IntPtr.Zero) return;
                var dark = ThemeManager.IsDark ? 1 : 0;
                if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
                    _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeLegacy, ref dark, sizeof(int));
                if (roundCorners)
                {
                    var corner = DwmwcpRound;
                    _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));
                }
            }

            EventHandler onThemeChanged = (_, __) => window.Dispatcher.BeginInvoke(new Action(Apply));
            window.SourceInitialized += (_, __) => Apply();
            ThemeManager.ThemeChanged += onThemeChanged;
            window.Closed += (_, __) => ThemeManager.ThemeChanged -= onThemeChanged;
            if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
                Apply();
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    }
}
