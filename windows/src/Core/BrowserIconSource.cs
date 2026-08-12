#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YTray.Core
{
    /// <summary>Loads and freezes the icon embedded in a browser executable for WPF lists.</summary>
    public static class BrowserIconSource
    {
        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, ImageSource> Cache =
            new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        public static ImageSource? FromExecutable(string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath)) return null;
            lock (CacheLock)
            {
                if (Cache.TryGetValue(executablePath, out var cached)) return cached;
            }

            ImageSource? source = null;
            try
            {
                using (var icon = BrowserProcessIcon.ExtractLargeIcon(executablePath))
                {
                    if (icon != null)
                    {
                        var bitmap = Imaging.CreateBitmapSourceFromHIcon(
                            icon.Handle,
                            Int32Rect.Empty,
                            BitmapSizeOptions.FromWidthAndHeight(32, 32));
                        bitmap.Freeze();
                        source = bitmap;
                    }
                }
            }
            catch { }

            if (source != null)
            {
                lock (CacheLock)
                {
                    if (Cache.Count >= 64) Cache.Clear();
                    Cache[executablePath] = source;
                }
            }
            return source;
        }
    }
}
