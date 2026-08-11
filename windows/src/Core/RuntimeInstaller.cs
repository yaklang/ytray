using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
        public static readonly Uri ManifestURL = new Uri("https://aliyun-oss.yaklang.com/browsers/chrome/manifest.json");

        public static string Architecture => Environment.Is64BitOperatingSystem ? "x64" : "x86";
        public static string Platform => "windows-" + Architecture;

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        public static async Task<List<MirrorVersion>> FetchVersionsAsync()
        {
            var resp = await Http.GetAsync(ManifestURL);
            if (!resp.IsSuccessStatusCode) throw new YTrayException(YTrayError.DownloadFailed, "镜像清单返回异常");
            var json = await resp.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<MirrorManifest>(json).Versions;
        }

        public static async Task<BrowserRuntime> InstallAsync(MirrorVersion version, string applicationDirectory)
        {
            var artifact = version.Artifacts.FirstOrDefault(a => a.OS == "windows" && a.Arch == Architecture && a.Format == "zip")
                ?? throw new YTrayException(YTrayError.DownloadFailed, $"版本 {version.Version} 没有 {Platform} ZIP");
            var tmpZip = Path.Combine(Path.GetTempPath(), "ytray-" + Guid.NewGuid() + ".zip");
            try
            {
                using (var resp = await Http.GetAsync(new Uri(artifact.Url)))
                {
                    if (!resp.IsSuccessStatusCode) throw new YTrayException(YTrayError.DownloadFailed, "ZIP 下载返回异常");
                    using (var fs = File.Create(tmpZip)) await resp.Content.CopyToAsync(fs);
                }
                var actualHash = await Sha256Async(tmpZip);
                if (!string.Equals(actualHash, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new YTrayException(YTrayError.DownloadFailed, "SHA-256 校验失败");

                var dest = Path.Combine(applicationDirectory, "Runtimes", version.Version, Platform);
                if (Directory.Exists(dest)) try { Directory.Delete(dest, true); } catch { }
                Directory.CreateDirectory(dest);
                ZipFile.ExtractToDirectory(tmpZip, dest);

                var exe = LocateChrome(dest) ?? throw new YTrayException(YTrayError.DownloadFailed, "ZIP 内未找到 Chrome 可执行文件");
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

        public static string NormalizeExecutable(string selected)
        {
            if (!File.Exists(selected)) return null;
            return selected;
        }

        public static string LocateChrome(string root)
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
    }
}