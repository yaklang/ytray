#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using YTray.Models;

namespace YTray.Core
{
    /// <summary>
    /// Validates and releases the Yakit browser extension embedded by the build workflow.
    /// Network access belongs to the preparation script, never the installed application.
    /// </summary>
    public static class ExtensionInstaller
    {
        public const string EnterpriseVariant = "chrome-enterprise";
        // Identifies the managed plugin across upgrades; the extension's manifest name
        // ("Yakit Browser Agent") stays stable, so match on it instead of tracking paths.
        public const string ExtensionName = "Yakit Browser Agent";

        private sealed class BundledExtensionDescriptor
        {
            public string Version { get; set; } = "";
            public string Sha256 { get; set; } = "";
            public long Size { get; set; }
            public string Variant { get; set; } = "";
        }

        public static string PluginsRoot(string applicationDirectory) =>
            Path.Combine(applicationDirectory, "Plugins");

        public static string PluginDirectory(string applicationDirectory, string version) =>
            Path.Combine(PluginsRoot(applicationDirectory), "yakit-browser-agent", version);

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
            out string version, bool replaceExisting = false)
        {
            directory = "";
            version = "";

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

        public static void ClearManagedExtensionRemoved(string applicationDirectory)
        {
            // Cleanup for preview builds that allowed removing the managed plugin entirely.
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
