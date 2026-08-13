#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
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

        /// <summary>
        /// Builds the same top-left instance badge used by the live taskbar icon, but keeps the
        /// result in memory for dense instance lists and details. Stopped history rows therefore
        /// retain their visual identity even after their temporary process icon files are removed.
        /// </summary>
        public static ImageSource? FromExecutableWithBadge(string executablePath, string? badge)
        {
            if (string.IsNullOrWhiteSpace(badge)) return FromExecutable(executablePath);

            string label;
            try { label = DockBadgeLabel.Normalize(badge!); }
            catch { return FromExecutable(executablePath); }

            var cacheKey = executablePath + "|badge=" + label;
            lock (CacheLock)
            {
                if (Cache.TryGetValue(cacheKey, out var cached)) return cached;
            }

            ImageSource? source = null;
            try
            {
                using (var icon = BrowserProcessIcon.ExtractLargeIcon(executablePath))
                using (var rendered = BrowserProcessIcon.RenderIcon(icon, label, 64))
                using (var stream = new MemoryStream())
                {
                    rendered.Save(stream, ImageFormat.Png);
                    stream.Position = 0;
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    source = bitmap;
                }
            }
            catch { }

            if (source != null)
            {
                lock (CacheLock)
                {
                    if (Cache.Count >= 64) Cache.Clear();
                    Cache[cacheKey] = source;
                }
            }
            return source ?? FromExecutable(executablePath);
        }
    }
}
