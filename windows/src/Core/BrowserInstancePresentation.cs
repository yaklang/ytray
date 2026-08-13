#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YTray.Models;

namespace YTray.Core
{
    /// <summary>
    /// Presentation snapshot shared by the full instance page and the compact floating panel.
    /// It loads thumbnails without WPF's URI cache/file lock, so overwriting the same preview
    /// path every few seconds is reflected immediately.
    /// </summary>
    public sealed class BrowserInstancePresentation
    {
        public BrowserInstance Instance { get; }
        public ImageSource? ThumbnailSource { get; }
        public bool HasThumbnail => ThumbnailSource != null;
        public bool HasThumbnailArtifact { get; }
        public bool IsThumbnailLoading { get; }

        public string Name => Instance.Name;
        public string? LastPageTitle => Instance.LastPageTitle;
        public string? LastPageURL => Instance.LastPageURL;
        public int ProcessID => Instance.ProcessID;
        public int DebugPort => Instance.DebugPort;
        public string? AppUserModelId => Instance.AppUserModelId;
        public string? DockBadge => Instance.DockBadge;
        public ImageSource? RuntimeIconSource { get; }
        public ImageSource? InstanceIconSource { get; }
        public string RuntimeTitle => !string.IsNullOrWhiteSpace(Instance.RuntimeName)
            ? Instance.RuntimeName
            : (Instance.RuntimeKind?.Title() ?? "Chromium");
        public string RuntimeVersion => string.IsNullOrWhiteSpace(Instance.RuntimeVersion) ? "版本未知" : Instance.RuntimeVersion!;
        public string PageTitle => string.IsNullOrWhiteSpace(Instance.LastPageTitle) ? "新标签页" : Instance.LastPageTitle!;
        public string PageUrl => string.IsNullOrWhiteSpace(Instance.LastPageURL)
            ? (string.IsNullOrWhiteSpace(Instance.StartURL) ? "chrome://newtab" : Instance.StartURL)
            : Instance.LastPageURL!;
        public bool UsesProxy => !string.IsNullOrWhiteSpace(Instance.SettingsSnapshot?.ProxyServer);
        public string NetworkMode => UsesProxy ? "HTTP 代理" : "无代理";
        public string NetworkAddress => UsesProxy
            ? (Instance.SettingsSnapshot?.ProxyServer ?? "").Replace("http://", "").Replace("https://", "")
            : "直连";
        public string DebugAddress => "127.0.0.1:" + Instance.DebugPort;
        public string Duration => FormatDuration(DateTime.Now - Instance.StartedAt);
        public string LastUsed => FormatRelativeTime(DateTime.Now - Instance.StartedAt);
        public string ProfilePath => Instance.ProfilePath;
        public string AppUserModelIdText => string.IsNullOrWhiteSpace(Instance.AppUserModelId) ? "尚未分配" : Instance.AppUserModelId!;
        public string PluginCount => $"插件 {(Instance.PluginIDs ?? new List<Guid>()).Count} 个";

        public bool CanCapture => !Instance.IsCapturing && !Instance.IsStopping;
        public bool CanStop => !Instance.IsStopping;
        public string CaptureButtonText => Instance.IsCapturing ? "截图中…" : "截图";
        public string StopButtonText => Instance.IsStopping ? "正在停止…" : "停止";
        public string CaptureToolTip => Instance.IsCapturing ? "正在保存截图" : "保存当前页面截图";
        public string StopToolTip => Instance.IsStopping ? "正在停止浏览器" : "停止浏览器";
        public string PreviewMessage => Instance.IsCapturing
            ? "正在生成预览…"
            : (!string.IsNullOrWhiteSpace(Instance.PreviewError)
                ? Instance.PreviewError ?? "预览暂不可用"
                : (IsThumbnailLoading
                    ? "正在加载页面预览…"
                    : (HasThumbnailArtifact && !HasThumbnail ? "预览图片加载失败" : "正在获取页面预览…")));

        public BrowserInstancePresentation(BrowserInstance instance, BrowserRuntime? runtime = null)
        {
            Instance = instance ?? throw new ArgumentNullException(nameof(instance));
            RuntimeIconSource = runtime == null ? null : BrowserIconSource.FromExecutable(runtime.ExecutablePath);
            InstanceIconSource = runtime == null
                ? RuntimeIconSource
                : BrowserIconSource.FromExecutableWithBadge(runtime.ExecutablePath, instance.DockBadge);
            HasThumbnailArtifact = HasUsableThumbnailFile(instance.ThumbnailPath);
            ThumbnailSource = InstanceThumbnailImageSource.FromFile(instance.ThumbnailPath);
            IsThumbnailLoading = ThumbnailSource == null
                && InstanceThumbnailImageSource.IsLoading(instance.ThumbnailPath);
        }

        private static string FormatDuration(TimeSpan value)
        {
            if (value < TimeSpan.Zero) value = TimeSpan.Zero;
            if (value.TotalDays >= 1) return $"{(int)value.TotalDays}天 {value.Hours:00}:{value.Minutes:00}";
            return $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";
        }

        private static string FormatRelativeTime(TimeSpan value)
        {
            if (value < TimeSpan.Zero || value.TotalSeconds < 45) return "刚刚";
            if (value.TotalMinutes < 60) return $"{Math.Max(1, (int)value.TotalMinutes)} 分钟前";
            if (value.TotalHours < 24) return $"{Math.Max(1, (int)value.TotalHours)} 小时前";
            if (value.TotalDays < 7) return $"{Math.Max(1, (int)value.TotalDays)} 天前";
            return DateTime.Now.Subtract(value).ToString("yyyy-MM-dd");
        }

        private static bool HasUsableThumbnailFile(string? path)
        {
            try { return !string.IsNullOrWhiteSpace(path) && new FileInfo(path).Length > 0; }
            catch { return false; }
        }
    }

    internal static class InstanceThumbnailImageSource
    {
        private sealed class CacheEntry
        {
            public long Stamp { get; set; }
            public long Length { get; set; }
            public ImageSource Source { get; set; } = null!;
            public long LastAccess { get; set; }
        }

        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, CacheEntry> Cache =
            new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Loading =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly System.Threading.SemaphoreSlim DecodeSlots =
            new System.Threading.SemaphoreSlim(2, 2);

        public static event EventHandler? ImageLoaded;

        internal static bool IsLoading(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            lock (CacheLock) return Loading.Contains(path!);
        }

        public static ImageSource? FromFile(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var filePath = path!;
            try
            {
                var info = new FileInfo(filePath);
                var stamp = info.LastWriteTimeUtc.Ticks;
                var length = info.Length;
                ImageSource? staleSource = null;
                lock (CacheLock)
                {
                    if (Cache.TryGetValue(filePath, out var entry)
                        && entry.Stamp == stamp && entry.Length == length)
                    {
                        entry.LastAccess = DateTime.UtcNow.Ticks;
                        return entry.Source;
                    }
                    if (entry != null) staleSource = entry.Source;
                }

                // Do not synchronously decode a 1–4MP browser screenshot while WPF is measuring a
                // page. The first render uses the lightweight placeholder; a frozen 480px bitmap
                // is decoded in the background and published once for all UI surfaces.
                lock (CacheLock)
                {
                    if (!Loading.Add(filePath)) return staleSource;
                }
                CrashGuard.Observe(LoadAsync(filePath, stamp, length), "decode-thumbnail");
                return staleSource;
            }
            catch
            {
                // The capture may be replacing the file at this exact moment. The next store
                // refresh will retry without surfacing a broken-image glyph.
                return null;
            }
        }

        internal static ImageSource LoadNowForTests(string path) => Decode(path);

        private static async Task LoadAsync(string path, long stamp, long length)
        {
            ImageSource? source = null;
            await DecodeSlots.WaitAsync().ConfigureAwait(false);
            try { source = await Task.Run(() => Decode(path)).ConfigureAwait(false); }
            catch { }
            finally
            {
                DecodeSlots.Release();
                lock (CacheLock)
                {
                    Loading.Remove(path);
                    if (source != null)
                    {
                        Cache[path] = new CacheEntry
                        {
                            Stamp = stamp,
                            Length = length,
                            Source = source,
                            LastAccess = DateTime.UtcNow.Ticks,
                        };
                        TrimCache(path);
                    }
                }
            }
            if (source == null) return;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) ImageLoaded?.Invoke(null, EventArgs.Empty);
            else _ = dispatcher.BeginInvoke(new Action(() => ImageLoaded?.Invoke(null, EventArgs.Empty)));
        }

        private static ImageSource Decode(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                // IgnoreImageCache is for URI-backed BitmapImage. Combining it with StreamSource
                // throws ArgumentNullException("key") in .NET Framework WPF.
                image.DecodePixelWidth = 480;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
        }

        private static void TrimCache(string keepPath)
        {
            const int maximumEntries = 32;
            while (Cache.Count > maximumEntries)
            {
                var oldest = Cache
                    .Where(pair => !string.Equals(pair.Key, keepPath, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(pair => pair.Value.LastAccess)
                    .Select(pair => pair.Key)
                    .FirstOrDefault();
                if (oldest == null) break;
                Cache.Remove(oldest);
            }
        }
    }
}
