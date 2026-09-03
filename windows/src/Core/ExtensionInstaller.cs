#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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

        private sealed class BundledExtensionDescriptor
        {
            public string Version { get; set; } = "";
            public string Sha256 { get; set; } = "";
            public long Size { get; set; }
            public string Variant { get; set; } = "";
        }

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

        public static string? ChromiumExtensionId(BrowserPlugin plugin)
        {
            if (plugin == null || string.IsNullOrWhiteSpace(plugin.Path)) return null;
            try
            {
                string? key = null;
                var manifestPath = Path.Combine(plugin.Path, "manifest.json");
                if (File.Exists(manifestPath))
                    key = JsonConvert.DeserializeObject<PluginManifest>(File.ReadAllText(manifestPath))?.Key;
                return ChromiumExtensionId(plugin.Path, key);
            }
            catch { return null; }
        }

        internal static string ChromiumExtensionId(string extensionPath, string? manifestKey)
        {
            var identity = string.IsNullOrWhiteSpace(manifestKey)
                ? Encoding.Unicode.GetBytes(Path.GetFullPath(extensionPath))
                : Convert.FromBase64String(manifestKey);
            using (var sha = SHA256.Create())
            {
                var digest = sha.ComputeHash(identity);
                var id = new StringBuilder(32);
                for (var i = 0; i < 16; i++)
                {
                    id.Append((char)('a' + (digest[i] >> 4)));
                    id.Append((char)('a' + (digest[i] & 15)));
                }
                return id.ToString();
            }
        }

        private static string ManagedExtensionOptOutPath(string applicationDirectory) =>
            Path.Combine(PluginsRoot(applicationDirectory), ".yakit-browser-agent-removed");

        public static bool TryGetBundledVersion(out string version)
        {
            version = "";
            try
            {
                var package = ReadBundledPackage(includeArchive: false);
                if (package.Descriptor == null) return false;
                version = package.Descriptor.Version;
                return !string.IsNullOrWhiteSpace(version);
            }
            catch { return false; }
        }

        /// <summary>
        /// Extracts the release archive embedded by the packaging workflow. The archive is
        /// revalidated at runtime and expanded beneath the normal per-user Plugins directory.
        /// </summary>
        public static bool TryInstallBundled(string applicationDirectory, out string directory,
            out string version, bool ignoreOptOut = false, bool replaceExisting = false)
        {
            directory = "";
            version = "";
            if (!ignoreOptOut && File.Exists(ManagedExtensionOptOutPath(applicationDirectory))) return false;

            var package = ReadBundledPackage(includeArchive: true);
            if (package.Descriptor == null || package.Archive == null) return false;
            version = package.Descriptor.Version;
            ValidateBundledPackage(package.Descriptor, package.Archive);

            var destination = PluginDirectory(applicationDirectory, version);
            try
            {
                if (!replaceExisting)
                {
                    directory = ResolveExtensionRoot(destination);
                    return true;
                }
            }
            catch { }

            var temporary = destination + ".partial-" + Guid.NewGuid().ToString("N");
            try
            {
                ExtractArchiveSafely(package.Archive, temporary);
                _ = ResolveExtensionRoot(temporary);
                if (Directory.Exists(destination)) Directory.Delete(destination, true);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                Directory.Move(temporary, destination);
                directory = ResolveExtensionRoot(destination);
                return true;
            }
            finally
            {
                try { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); } catch { }
            }
        }

        public static void MarkManagedExtensionRemoved(string applicationDirectory)
        {
            try
            {
                Directory.CreateDirectory(PluginsRoot(applicationDirectory));
                File.WriteAllText(ManagedExtensionOptOutPath(applicationDirectory), DateTime.UtcNow.ToString("O"));
            }
            catch { }
        }

        public static void ClearManagedExtensionRemoved(string applicationDirectory)
        {
            try
            {
                var marker = ManagedExtensionOptOutPath(applicationDirectory);
                if (File.Exists(marker)) File.Delete(marker);
            }
            catch { }
        }

        private static (BundledExtensionDescriptor? Descriptor, byte[]? Archive) ReadBundledPackage(bool includeArchive)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var names = assembly.GetManifestResourceNames();
            var descriptorName = names.FirstOrDefault(name =>
                name.EndsWith("BundledExtension.bundled-extension.json", StringComparison.OrdinalIgnoreCase));
            if (descriptorName == null) return (null, null);

            BundledExtensionDescriptor? descriptor;
            using (var stream = assembly.GetManifestResourceStream(descriptorName))
            using (var reader = stream == null ? null : new StreamReader(stream, Encoding.UTF8, true))
                descriptor = reader == null ? null : JsonConvert.DeserializeObject<BundledExtensionDescriptor>(reader.ReadToEnd());
            if (!includeArchive || descriptor == null) return (descriptor, null);

            var archiveName = names.FirstOrDefault(name =>
                name.EndsWith("BundledExtension.yakit-browser-agent.zip", StringComparison.OrdinalIgnoreCase));
            if (archiveName == null) return (descriptor, null);
            using (var stream = assembly.GetManifestResourceStream(archiveName))
            using (var memory = new MemoryStream())
            {
                if (stream == null) return (descriptor, null);
                stream.CopyTo(memory);
                return (descriptor, memory.ToArray());
            }
        }

        private static void ValidateBundledPackage(BundledExtensionDescriptor descriptor, byte[] archive)
        {
            if (!Regex.IsMatch(descriptor.Version ?? "", @"^[0-9]+(?:\.[0-9]+)*$", RegexOptions.CultureInvariant)
                || !string.Equals(descriptor.Variant, EnterpriseVariant, StringComparison.OrdinalIgnoreCase))
                throw new YTrayException(YTrayError.ExtensionInstallFailed, "内置插件元数据无效");
            if (archive.LongLength != descriptor.Size)
                throw new YTrayException(YTrayError.ExtensionInstallFailed, "内置插件大小校验失败");
            using (var sha = SHA256.Create())
            {
                var actual = string.Concat(sha.ComputeHash(archive).Select(value => value.ToString("x2")));
                if (!string.Equals(actual, descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new YTrayException(YTrayError.ExtensionInstallFailed, "内置插件 SHA-256 校验失败");
            }
        }

        private static void ExtractArchiveSafely(byte[] archive, string destination)
        {
            Directory.CreateDirectory(destination);
            var destinationRoot = Path.GetFullPath(destination)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            using (var memory = new MemoryStream(archive, writable: false))
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Read))
            {
                foreach (var entry in zip.Entries)
                {
                    var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    var output = Path.GetFullPath(Path.Combine(destination, relative));
                    if (!output.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                        throw new YTrayException(YTrayError.ExtensionInstallFailed, "内置插件包含不安全路径");
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(output);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    using (var input = entry.Open())
                    using (var file = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None))
                        input.CopyTo(file);
                }
            }
        }

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
        /// Returns true only when an embedded plugin should be installed automatically.
        /// A missing local plugin is initialized from the bundle; an existing plugin is
        /// upgraded only when the embedded version is strictly newer. Equal versions are
        /// left untouched and a newer local version is never downgraded.
        /// </summary>
        public static bool ShouldInstallBundledVersion(string? bundledVersion, string? installedVersion,
            bool allowSameVersion = false)
        {
            if (string.IsNullOrWhiteSpace(bundledVersion)) return false;
            if (string.IsNullOrWhiteSpace(installedVersion)) return true;
            var comparison = CompareVersions(bundledVersion, installedVersion);
            return comparison > 0 || (allowSameVersion && comparison == 0);
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
