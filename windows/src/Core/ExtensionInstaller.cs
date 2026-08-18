#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using YTray.Models;

namespace YTray.Core
{
    /// <summary>
    /// Downloads + installs the Yakit browser extension from the OSS release manifest
    /// (mirrors macOS ExtensionInstaller). The enterprise variant ships an unpacked
    /// extension whose zip root already contains manifest.json, so the extracted
    /// directory registers directly with InstanceStore.AddPlugin.
    /// </summary>
    public static class ExtensionInstaller
    {
        public const string EnterpriseVariant = "chrome-enterprise";
        // Identifies the managed plugin across upgrades; the extension's manifest name
        // ("Yakit Browser Agent") stays stable, so match on it instead of tracking paths.
        public const string ExtensionName = "Yakit Browser Agent";

        public static readonly Uri ManifestURL = new Uri("https://aliyun-oss.yaklang.com/chrome-extension/manifest.json");

        // The OSS mirror compresses responses for plain .NET Framework HttpClient requests
        // (no Accept-Encoding handling by default), so decode gzip/deflate explicitly.
        private static readonly HttpClient Http = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip
                    | System.Net.DecompressionMethods.Deflate,
            };
            return new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        }

        public static string PluginsRoot(string applicationDirectory) =>
            Path.Combine(applicationDirectory, "Plugins");

        public static string PluginDirectory(string applicationDirectory, string version) =>
            Path.Combine(PluginsRoot(applicationDirectory), "yakit-browser-agent", version);

        public static async Task<ExtensionManifest> FetchManifestAsync()
        {
            using (var resp = await Http.GetAsync(ManifestURL))
            {
                if (!resp.IsSuccessStatusCode) throw new YTrayException(YTrayError.ExtensionInstallFailed, "插件清单返回异常");
                var json = await resp.Content.ReadAsStringAsync();
                var manifest = JsonConvert.DeserializeObject<ExtensionManifest>(json);
                if (manifest == null || string.IsNullOrWhiteSpace(manifest.Latest))
                    throw new YTrayException(YTrayError.ExtensionInstallFailed, "插件清单格式无效");
                return manifest;
            }
        }

        public static ExtensionArtifact? EnterpriseArtifact(ExtensionReleaseVersion version) =>
            version.Artifacts.FirstOrDefault(a =>
                string.Equals(a.Variant, EnterpriseVariant, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(a.Url));

        /// <summary>
        /// Compares dotted numeric versions ("0.2.10" &gt; "0.2.2"). Non-numeric segments
        /// fall back to ordinal comparison; a parse failure never throws.
        /// </summary>
        public static int CompareVersions(string? left, string? right)
        {
            var l = (left ?? "").Trim().TrimStart('v', 'V');
            var r = (right ?? "").Trim().TrimStart('v', 'V');
            var leftParts = l.Split('.');
            var rightParts = r.Split('.');
            var count = Math.Max(leftParts.Length, rightParts.Length);
            for (var i = 0; i < count; i++)
            {
                var lp = i < leftParts.Length ? leftParts[i] : "";
                var rp = i < rightParts.Length ? rightParts[i] : "";
                if (long.TryParse(lp, out var ln) && long.TryParse(rp, out var rn))
                {
                    if (ln != rn) return ln.CompareTo(rn);
                }
                else
                {
                    var ordinal = string.CompareOrdinal(lp, rp);
                    if (ordinal != 0) return ordinal > 0 ? 1 : -1;
                }
            }
            return string.CompareOrdinal(l, r);
        }

        /// <summary>
        /// Installs the given release into Plugins/yakit-browser-agent/{version} and returns
        /// the extracted directory. Existing installs of the same version are reinstalled
        /// (directory replaced), which doubles as a repair path.
        /// </summary>
        public static async Task<string> InstallAsync(ExtensionReleaseVersion version, string applicationDirectory,
            IProgress<RuntimeInstaller.InstallProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            if (version == null) throw new ArgumentNullException(nameof(version));
            var artifact = EnterpriseArtifact(version)
                ?? throw new YTrayException(YTrayError.ExtensionInstallFailed, $"版本 {version.Version} 没有 {EnterpriseVariant} 产物");
            var tmpZip = Path.Combine(Path.GetTempPath(), "ytray-ext-" + Guid.NewGuid() + ".zip");
            try
            {
                progress?.Report(new RuntimeInstaller.InstallProgress { Percent = 2, Message = "正在连接下载镜像…" });
                await RuntimeInstaller.DownloadAsync(Http, new Uri(artifact.Url), tmpZip, artifact.Size, progress, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new RuntimeInstaller.InstallProgress { Percent = 81, Message = "正在校验 SHA-256…" });
                var actualHash = await RuntimeInstaller.HashFileAsync(tmpZip);
                if (!string.IsNullOrEmpty(artifact.Sha256)
                    && !string.Equals(actualHash, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new YTrayException(YTrayError.ExtensionInstallFailed, "SHA-256 校验失败");

                var dest = PluginDirectory(applicationDirectory, version.Version);
                if (Directory.Exists(dest)) try { Directory.Delete(dest, true); } catch { }
                Directory.CreateDirectory(dest);
                progress?.Report(new RuntimeInstaller.InstallProgress { Percent = 84, Message = "正在解压插件…" });
                await RuntimeInstaller.ExtractAsync(tmpZip, dest, progress, cancellationToken, YTrayError.ExtensionInstallFailed);

                if (!File.Exists(Path.Combine(dest, "manifest.json")))
                    throw new YTrayException(YTrayError.ExtensionInstallFailed, "ZIP 根目录缺少 manifest.json");
                return ResolveExtensionRoot(dest);
            }
            finally
            {
                try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { }
            }
        }

        private static string ResolveExtensionRoot(string directory)
        {
            if (File.Exists(Path.Combine(directory, "manifest.json"))) return directory;
            var nested = Directory.EnumerateDirectories(directory).ToList();
            if (nested.Count == 1 && File.Exists(Path.Combine(nested[0], "manifest.json")))
                return nested[0];
            throw new YTrayException(YTrayError.ExtensionInstallFailed, "解压后未找到 manifest.json");
        }

        /// <summary>
        /// Drops version directories of the managed plugin other than the installed one so
        /// upgrades do not accumulate old copies. Best-effort; locked files are ignored.
        /// </summary>
        public static void CleanupOldVersions(string applicationDirectory, string installedVersion)
        {
            try
            {
                var root = Path.Combine(PluginsRoot(applicationDirectory), "yakit-browser-agent");
                if (!Directory.Exists(root)) return;
                foreach (var dir in Directory.EnumerateDirectories(root))
                    if (!string.Equals(Path.GetFileName(dir), installedVersion, StringComparison.OrdinalIgnoreCase))
                        try { Directory.Delete(dir, true); } catch { }
            }
            catch { }
        }
    }
}
