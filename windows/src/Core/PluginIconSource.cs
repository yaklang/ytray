#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Newtonsoft.Json.Linq;
using YTray.Models;

namespace YTray.Core
{
    /// <summary>
    /// Resolves and safely decodes the icon declared by an unpacked Chromium extension.
    /// Paths are constrained to the extension directory so a malformed manifest cannot make
    /// the UI read an arbitrary file elsewhere on disk.
    /// </summary>
    public static class PluginIconSource
    {
        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, ImageSource> Cache =
            new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        public static string ResolveIconPath(string pluginDirectory)
        {
            if (string.IsNullOrWhiteSpace(pluginDirectory) || !Directory.Exists(pluginDirectory))
                return "";

            try
            {
                var root = Path.GetFullPath(pluginDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var manifestPath = Path.Combine(root, "manifest.json");
                if (!File.Exists(manifestPath)) return "";

                var manifest = JObject.Parse(File.ReadAllText(manifestPath));
                foreach (var relativePath in IconCandidates(manifest))
                {
                    var normalized = (relativePath ?? "").Trim()
                        .TrimStart('/', '\\')
                        .Replace('/', Path.DirectorySeparatorChar);
                    if (string.IsNullOrWhiteSpace(normalized)) continue;

                    var candidate = Path.GetFullPath(Path.Combine(root, normalized));
                    var rootPrefix = root + Path.DirectorySeparatorChar;
                    if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch
            {
                // A missing or unsupported icon is cosmetic. The plugin remains usable and the
                // views fall back to the neutral extension glyph.
            }
            return "";
        }

        public static ImageSource? FromPlugin(BrowserPlugin? plugin) =>
            plugin == null ? null : FromPath(plugin.IconPath, plugin.Path);

        public static ImageSource? FromDirectory(string pluginDirectory) =>
            FromPath("", pluginDirectory);

        private static ImageSource? FromPath(string? savedIconPath, string pluginDirectory)
        {
            var iconPath = IsInsidePluginDirectory(savedIconPath, pluginDirectory)
                ? savedIconPath!
                : ResolveIconPath(pluginDirectory);
            if (string.IsNullOrWhiteSpace(iconPath) || !File.Exists(iconPath)) return null;

            string cacheKey;
            try { cacheKey = Path.GetFullPath(iconPath) + "|" + File.GetLastWriteTimeUtc(iconPath).Ticks; }
            catch { return null; }

            lock (CacheLock)
            {
                if (Cache.TryGetValue(cacheKey, out var cached)) return cached;
            }

            ImageSource? source = null;
            try
            {
                using (var stream = new FileStream(iconPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = 128;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    source = bitmap;
                }
            }
            catch
            {
                // WPF does not decode SVG extension icons. Keep the generic glyph in that case.
            }

            if (source != null)
            {
                lock (CacheLock)
                {
                    if (Cache.Count >= 64) Cache.Clear();
                    Cache[cacheKey] = source;
                }
            }
            return source;
        }

        private static bool IsInsidePluginDirectory(string? candidatePath, string pluginDirectory)
        {
            if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(pluginDirectory)
                || !File.Exists(candidatePath) || !Directory.Exists(pluginDirectory))
                return false;
            try
            {
                var root = Path.GetFullPath(pluginDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                return Path.GetFullPath(candidatePath).StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static IEnumerable<string> IconCandidates(JObject manifest)
        {
            var iconSets = new[]
            {
                manifest["icons"],
                manifest["action"]?["default_icon"],
                manifest["browser_action"]?["default_icon"],
                manifest["page_action"]?["default_icon"],
            };

            foreach (var iconSet in iconSets)
            {
                if (iconSet is JValue value && value.Type == JTokenType.String)
                {
                    yield return value.Value<string>() ?? "";
                    continue;
                }
                if (!(iconSet is JObject iconObject)) continue;

                foreach (var property in iconObject.Properties()
                    .OrderByDescending(property => ParseIconSize(property.Name)))
                    yield return property.Value.Value<string>() ?? "";
            }
        }

        private static int ParseIconSize(string value) =>
            int.TryParse(value, out var size) ? size : 0;
    }
}
