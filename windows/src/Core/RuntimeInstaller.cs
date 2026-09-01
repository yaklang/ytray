#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
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

        private const int MaximumManifestBytes = 4 * 1024 * 1024;
        private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(15);
        private static readonly HttpClient Http = CreateHttpClient();

        public static async Task<List<MirrorVersion>> FetchVersionsAsync()
        {
            var requestUrl = new UriBuilder(ManifestURL)
            {
                Query = "ytray_runtime=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            }.Uri;
            DiagnosticLog.Info("runtime.manifest", $"requesting {ManifestURL.Host}{ManifestURL.AbsolutePath}");
            try
            {
                using (var timeout = new CancellationTokenSource(ManifestTimeout))
                {
                    var versions = await FetchVersionsAsync(Http, requestUrl, timeout.Token).ConfigureAwait(false);
                    DiagnosticLog.Info("runtime.manifest",
                        $"loaded {versions.Count} versions; latest={versions.FirstOrDefault()?.Version ?? "none"}");
                    return versions;
                }
            }
            catch (OperationCanceledException ex)
            {
                DiagnosticLog.Error("runtime.manifest", ex, "manifest request timed out");
                throw new YTrayException(YTrayError.RuntimeManifestFailed, "请求超时，请稍后重试");
            }
            catch (YTrayException ex)
            {
                DiagnosticLog.Error("runtime.manifest", ex);
                throw;
            }
            catch (HttpRequestException ex)
            {
                DiagnosticLog.Error("runtime.manifest", ex);
                throw new YTrayException(YTrayError.RuntimeManifestFailed, "无法获取版本清单，请检查网络后重试");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Error("runtime.manifest", ex);
                throw new YTrayException(YTrayError.RuntimeManifestFailed, "版本清单暂时不可用，请稍后重试");
            }
        }

        internal static async Task<List<MirrorVersion>> FetchVersionsAsync(
            HttpClient http, Uri manifestUrl, CancellationToken cancellationToken)
        {
            if (http == null) throw new ArgumentNullException(nameof(http));
            if (manifestUrl == null) throw new ArgumentNullException(nameof(manifestUrl));
            using (var request = new HttpRequestMessage(HttpMethod.Get, manifestUrl))
            {
                request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
                {
                    NoCache = true,
                    NoStore = true,
                };
                request.Headers.UserAgent.ParseAdd("YTray/" + YTrayBuildInfo.Version);
                using (var response = await http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                        throw new YTrayException(YTrayError.RuntimeManifestFailed,
                            $"浏览器版本清单请求失败（HTTP {(int)response.StatusCode}）");
                    if (response.Content.Headers.ContentLength > MaximumManifestBytes)
                        throw new YTrayException(YTrayError.RuntimeManifestFailed, "版本清单内容异常（文件过大）");

                    var payload = await ReadLimitedAsync(
                        response.Content, MaximumManifestBytes, cancellationToken).ConfigureAwait(false);
                    payload = DecodeCompressedPayload(payload, MaximumManifestBytes);
                    var json = Encoding.UTF8.GetString(payload).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
                    if (json.Length == 0 || json[0] != '{')
                        throw new YTrayException(YTrayError.RuntimeManifestFailed,
                            "版本服务返回了无法识别的内容，请稍后重试");

                    MirrorManifest manifest;
                    try
                    {
                        manifest = JsonConvert.DeserializeObject<MirrorManifest>(json)
                            ?? throw new JsonSerializationException("Manifest is empty.");
                    }
                    catch (JsonException ex)
                    {
                        throw new YTrayException(YTrayError.RuntimeManifestFailed,
                            "版本清单格式无效，请稍后重试", ex);
                    }
                    if (manifest.SchemaVersion != 0 && manifest.SchemaVersion != 1)
                        throw new YTrayException(YTrayError.RuntimeManifestFailed, "版本清单版本不受支持");
                    if (!string.IsNullOrWhiteSpace(manifest.Product)
                        && !string.Equals(manifest.Product, "chrome-for-testing", StringComparison.OrdinalIgnoreCase))
                        throw new YTrayException(YTrayError.RuntimeManifestFailed, "版本清单产品不匹配");

                    var versions = (manifest.Versions ?? new List<MirrorVersion>())
                        .Where(version => version != null && !string.IsNullOrWhiteSpace(version.Version))
                        .ToList();
                    foreach (var version in versions)
                        version.Artifacts = version.Artifacts ?? new List<MirrorArtifact>();
                    if (versions.Count == 0)
                        throw new YTrayException(YTrayError.RuntimeManifestFailed, "版本清单中没有可用版本");
                    return versions;
                }
            }
        }

        internal static HttpClientHandler CreateDefaultHandler() => new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };

        private static HttpClient CreateHttpClient()
        {
            return new HttpClient(CreateDefaultHandler()) { Timeout = TimeSpan.FromMinutes(10) };
        }

        private static async Task<byte[]> ReadLimitedAsync(
            HttpContent content, int maximumBytes, CancellationToken cancellationToken)
        {
            using (var source = await content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var output = new MemoryStream())
            {
                var buffer = new byte[16 * 1024];
                int read;
                while ((read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (output.Length + read > maximumBytes)
                        throw new YTrayException(YTrayError.RuntimeManifestFailed, "版本清单内容异常（文件过大）");
                    output.Write(buffer, 0, read);
                }
                return output.ToArray();
            }
        }

        private static byte[] DecodeCompressedPayload(byte[] payload, int maximumBytes)
        {
            if (payload.Length < 2 || payload[0] != 0x1f || payload[1] != 0x8b) return payload;
            try
            {
                using (var source = new MemoryStream(payload, false))
                using (var gzip = new GZipStream(source, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    var buffer = new byte[16 * 1024];
                    int read;
                    while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (output.Length + read > maximumBytes)
                            throw new YTrayException(YTrayError.RuntimeManifestFailed, "版本清单内容异常（解压后过大）");
                        output.Write(buffer, 0, read);
                    }
                    return output.ToArray();
                }
            }
            catch (YTrayException) { throw; }
            catch (InvalidDataException ex)
            {
                throw new YTrayException(YTrayError.RuntimeManifestFailed, "版本清单压缩格式无效", ex);
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
