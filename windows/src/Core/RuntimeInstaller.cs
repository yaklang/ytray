#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using YTray.Models;

namespace YTray.Core
{
    /// <summary>
    /// Downloads + installs Chrome for Testing from the mirror manifest (mirrors macOS RuntimeInstaller).
    /// </summary>
    public static class RuntimeInstaller
    {
        public sealed class InstallProgress
        {
            public int Percent { get; set; }
            public string Message { get; set; } = "";
            public long BytesReceived { get; set; }
            public long? TotalBytes { get; set; }
        }

        public static readonly Uri ManifestURL = new Uri("https://aliyun-oss.yaklang.com/browsers/chrome/manifest.json");

        public static string Architecture => Environment.Is64BitOperatingSystem ? "x64" : "x86";
        public static string Platform => "windows-" + Architecture;

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        public static async Task<List<MirrorVersion>> FetchVersionsAsync()
        {
            using (var resp = await Http.GetAsync(ManifestURL))
            {
                if (!resp.IsSuccessStatusCode) throw new YTrayException(YTrayError.DownloadFailed, "镜像清单返回异常");
                var json = await resp.Content.ReadAsStringAsync();
                return JsonConvert.DeserializeObject<MirrorManifest>(json)?.Versions ?? new List<MirrorVersion>();
            }
        }

        public static bool IsCompatible(MirrorVersion version) =>
            version?.Artifacts?.Any(a =>
                string.Equals(a.OS, "windows", StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.Arch, Architecture, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.Format, "zip", StringComparison.OrdinalIgnoreCase)) == true;

        public static async Task<BrowserRuntime> InstallAsync(MirrorVersion version, string applicationDirectory,
            IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            if (version == null) throw new ArgumentNullException(nameof(version));
            if (string.IsNullOrWhiteSpace(applicationDirectory))
                throw new ArgumentException("Application directory is required.", nameof(applicationDirectory));
            var artifact = version.Artifacts.FirstOrDefault(a =>
                string.Equals(a.OS, "windows", StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.Arch, Architecture, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.Format, "zip", StringComparison.OrdinalIgnoreCase))
                ?? throw new YTrayException(YTrayError.DownloadFailed, $"版本 {version.Version} 没有 {Platform} ZIP");
            var tmpZip = Path.Combine(Path.GetTempPath(), "ytray-" + Guid.NewGuid() + ".zip");
            try
            {
                progress?.Report(new InstallProgress { Percent = 2, Message = "正在连接下载镜像…" });
                await DownloadAsync(Http, new Uri(artifact.Url), tmpZip, artifact.Size, progress, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new InstallProgress { Percent = 81, Message = "正在校验 SHA-256…" });
                var actualHash = await Sha256Async(tmpZip);
                if (!string.Equals(actualHash, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new YTrayException(YTrayError.DownloadFailed, "SHA-256 校验失败");

                var dest = Path.Combine(applicationDirectory, "Runtimes", version.Version, Platform);
                if (Directory.Exists(dest)) try { Directory.Delete(dest, true); } catch { }
                Directory.CreateDirectory(dest);
                progress?.Report(new InstallProgress { Percent = 84, Message = "正在解压浏览器…" });
                await ExtractAsync(tmpZip, dest, progress, cancellationToken);

                var exe = LocateChrome(dest) ?? throw new YTrayException(YTrayError.DownloadFailed, "ZIP 内未找到 Chrome 可执行文件");
                progress?.Report(new InstallProgress { Percent = 100, Message = $"Chrome for Testing {version.Version} 安装完成" });
                return new BrowserRuntime
                {
                    Name = "Chrome for Testing " + version.Version,
                    Version = version.Version,
                    Architecture = Platform,
                    ExecutablePath = exe,
                    Source = RuntimeSource.Managed,
                    BrowserKind = BrowserKind.ChromeForTesting,
                };
            }
            finally
            {
                try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { }
            }
        }

        public static string? NormalizeExecutable(string selected)
        {
            if (!File.Exists(selected)) return null;
            return selected;
        }

        public static string? LocateChrome(string root)
        {
            try
            {
                return Directory.EnumerateFiles(root, "chrome.exe", SearchOption.AllDirectories).FirstOrDefault();
            }
            catch { return null; }
        }

        private static async Task<string> Sha256Async(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
            {
                var hash = await Task.Run(() => sha.ComputeHash(fs));
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>
        /// Streams a remote file to disk with throttled progress reporting. Shared by the runtime
        /// and extension installers so both report the same download phase mapping (5–78%).
        /// </summary>
        internal static async Task DownloadAsync(HttpClient http, Uri url, string destination, long? fallbackTotal,
            IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
        {
            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                if (!resp.IsSuccessStatusCode) throw new YTrayException(YTrayError.DownloadFailed, "ZIP 下载返回异常");
                var total = resp.Content.Headers.ContentLength ?? fallbackTotal;
                using (var input = await resp.Content.ReadAsStreamAsync())
                using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var buffer = new byte[81920];
                    long received = 0;
                    int read;
                    var lastReportedPercent = -1;
                    var lastReportAt = DateTime.UtcNow;
                    while ((read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                    {
                        await output.WriteAsync(buffer, 0, read, cancellationToken);
                        received += read;
                        var downloadPercent = total.HasValue && total.Value > 0
                            ? Math.Min(78, 5 + (int)(received * 73L / total.Value))
                            : 5;
                        var now = DateTime.UtcNow;
                        var shouldReport = downloadPercent != lastReportedPercent
                            && (now - lastReportAt >= TimeSpan.FromMilliseconds(90)
                                || total.HasValue && received >= total.Value);
                        if (!shouldReport) continue;
                        lastReportedPercent = downloadPercent;
                        lastReportAt = now;
                        progress?.Report(new InstallProgress
                        {
                            Percent = downloadPercent,
                            Message = total.HasValue && total.Value > 0
                                ? $"正在下载 · {FormatBytes(received)} / {FormatBytes(total.Value)}"
                                : $"正在下载 · {FormatBytes(received)}",
                            BytesReceived = received,
                            TotalBytes = total,
                        });
                    }
                }
            }
        }

        internal static Task<string> HashFileAsync(string path) => Sha256Async(path);

        internal static Task ExtractAsync(string zipPath, string destination, IProgress<InstallProgress>? progress,
            CancellationToken cancellationToken, YTrayError error = YTrayError.DownloadFailed)
        {
            return Task.Run(() =>
            {
                var destinationRoot = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                using (var archive = ZipFile.OpenRead(zipPath))
                {
                    var total = Math.Max(archive.Entries.Count, 1);
                    var lastReportedPercent = -1;
                    for (var index = 0; index < archive.Entries.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var entry = archive.Entries[index];
                        var outputPath = Path.GetFullPath(Path.Combine(destination, entry.FullName));
                        if (!outputPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                            throw new YTrayException(error, "ZIP 包含不安全的文件路径");
                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            Directory.CreateDirectory(outputPath);
                        }
                        else
                        {
                            var outputDirectory = Path.GetDirectoryName(outputPath);
                            if (string.IsNullOrWhiteSpace(outputDirectory))
                                throw new YTrayException(error, "ZIP 项目没有有效的目标目录");
                            Directory.CreateDirectory(outputDirectory);
                            entry.ExtractToFile(outputPath, true);
                        }
                        var percent = Math.Min(99, 84 + (index + 1) * 15 / total);
                        if (percent == lastReportedPercent && index + 1 < archive.Entries.Count) continue;
                        lastReportedPercent = percent;
                        progress?.Report(new InstallProgress
                        {
                            Percent = percent,
                            Message = $"正在解压 · {index + 1} / {total}",
                        });
                    }
                }
            }, cancellationToken);
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024L * 1024) return (bytes / 1024d).ToString("0.0") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024d * 1024)).ToString("0.0") + " MB";
            return (bytes / (1024d * 1024 * 1024)).ToString("0.00") + " GB";
        }
    }
}
