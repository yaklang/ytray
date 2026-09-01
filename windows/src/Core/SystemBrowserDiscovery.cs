#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using YTray.Models;

namespace YTray.Core
{
    /// <summary>
    /// Discovers installed Chromium-based browsers on Windows (mirrors macOS SystemBrowserDiscovery).
    /// Scans Program Files, the registry App Paths, and reads --version.
    /// </summary>
    public static class SystemBrowserDiscovery
    {
        private class Candidate
        {
            public string DisplayName = "";
            public string ExeName = "";
            public BrowserKind Kind;
            public string[] RegistryAppPathKeys = Array.Empty<string>();
        }

        private static readonly Candidate[] _candidates =
        {
            new Candidate { DisplayName = "Google Chrome",            ExeName = "chrome.exe",  Kind = BrowserKind.Chrome,           RegistryAppPathKeys = new[] { @"Google\Chrome\Application\chrome.exe" } },
            new Candidate { DisplayName = "Google Chrome Beta",       ExeName = "chrome.exe",  Kind = BrowserKind.ChromeBeta,       RegistryAppPathKeys = new[] { @"Google\Chrome Beta\Application\chrome.exe" } },
            new Candidate { DisplayName = "Google Chrome Canary",     ExeName = "chrome.exe",  Kind = BrowserKind.ChromeCanary,     RegistryAppPathKeys = new[] { @"Google\Chrome SxS\Application\chrome.exe" } },
            new Candidate { DisplayName = "Chromium",                ExeName = "chromium.exe",Kind = BrowserKind.Chromium,         RegistryAppPathKeys = new[] { @"Chromium\Application\chromium.exe" } },
            new Candidate { DisplayName = "Microsoft Edge",           ExeName = "msedge.exe",  Kind = BrowserKind.Edge,             RegistryAppPathKeys = new[] { @"Microsoft\Edge\Application\msedge.exe" } },
        };

        public static List<BrowserRuntime> Discover()
        {
            var found = new Dictionary<string, BrowserRuntime>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in _candidates)
            {
                foreach (var exe in ResolveExecutablePaths(c))
                {
                    if (!File.Exists(exe)) continue;
                    if (found.ContainsKey(exe)) continue;
                    var versionInfo = ReadVersionInfo(exe);
                    var kind = BrowserKindExtensions.Infer(c.DisplayName, exe,
                        versionInfo?.ProductName, versionInfo?.FileDescription, c.Kind);
                    found[exe] = new BrowserRuntime
                    {
                        Name = kind.Title(),
                        Version = ReadVersion(versionInfo) ?? "版本未知",
                        Architecture = ReadArchitecture(exe),
                        ExecutablePath = exe,
                        Source = RuntimeSource.System,
                        BrowserKind = kind,
                    };
                }
            }
            return found.Values.ToList();
        }

        public static BrowserRuntime? Inspect(string selectedPath)
        {
            if (string.IsNullOrEmpty(selectedPath) || !File.Exists(selectedPath)) return null;
            var name = Path.GetFileNameWithoutExtension(selectedPath);
            var versionInfo = ReadVersionInfo(selectedPath);
            var kind = BrowserKindExtensions.Infer(name, selectedPath,
                versionInfo?.ProductName, versionInfo?.FileDescription);
            var isSystem = selectedPath.StartsWith(@"C:\Program Files\", StringComparison.OrdinalIgnoreCase)
                || selectedPath.StartsWith(@"C:\Program Files (x86)\", StringComparison.OrdinalIgnoreCase);
            return new BrowserRuntime
            {
                Name = kind.Title(),
                Version = ReadVersion(versionInfo) ?? "版本未知",
                Architecture = ReadArchitecture(selectedPath),
                ExecutablePath = selectedPath,
                Source = isSystem ? RuntimeSource.System : RuntimeSource.Local,
                BrowserKind = kind,
            };
        }

        /// <summary>
        /// Repairs an identity persisted by older YTray versions. In particular, user-downloaded
        /// Chrome for Testing was previously saved as regular Chrome because its official
        /// chrome-win64\chrome.exe path does not contain the product name.
        /// </summary>
        internal static bool NormalizeIdentity(BrowserRuntime runtime)
        {
            if (runtime == null || string.IsNullOrWhiteSpace(runtime.ExecutablePath)
                || !File.Exists(runtime.ExecutablePath)) return false;
            var versionInfo = ReadVersionInfo(runtime.ExecutablePath);
            return NormalizeIdentity(runtime, versionInfo?.ProductName, versionInfo?.FileDescription);
        }

        internal static bool NormalizeIdentity(BrowserRuntime runtime,
            string? productName, string? fileDescription)
        {
            if (runtime == null) return false;
            // State migration is deliberately monotonic: only upgrade an unknown/regular
            // Chrome identity when the executable positively identifies as Testing. Never
            // downgrade an explicitly persisted Testing/Beta/Canary/Edge/Chromium runtime
            // because a damaged or localized version resource could not be read.
            var previousKind = runtime.Kind;
            if (runtime.BrowserKind != null && previousKind != BrowserKind.Chrome) return false;
            var kind = BrowserKindExtensions.Infer(runtime.Name, runtime.ExecutablePath,
                productName, fileDescription, runtime.BrowserKind);
            if (kind != BrowserKind.ChromeForTesting || kind == previousKind) return false;

            runtime.BrowserKind = kind;
            runtime.Name = kind.Title();
            return true;
        }

        private static IEnumerable<string> ResolveExecutablePaths(Candidate c)
        {
            // Program Files (x64 and x86)
            var pf64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            yield return Path.Combine(pf64, c.RegistryAppPathKeys[0]);
            if (pf86 != pf64) yield return Path.Combine(pf86, c.RegistryAppPathKeys[0]);

            // Registry App Paths
            foreach (var keyName in c.RegistryAppPathKeys)
            {
                var path = ReadAppPath(keyName);
                if (!string.IsNullOrEmpty(path)) yield return path!;
            }

            // LocalAppData for per-user installs
            var lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return Path.Combine(lad, c.RegistryAppPathKeys[0]);
        }

        private static string? ReadAppPath(string relativeKey)
        {
            // App Paths live under HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\<exe>
            var name = Path.GetFileName(relativeKey);
            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                try
                {
                    using (var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + name))
                    {
                        if (key?.GetValue(null) is string v && File.Exists(v)) return v;
                    }
                }
                catch { }
                // 32-bit registry view
                try
                {
                    using (var key = root.OpenSubKey(@"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\App Paths\" + name))
                    {
                        if (key?.GetValue(null) is string v && File.Exists(v)) return v;
                    }
                }
                catch { }
            }
            return null;
        }

        private static FileVersionInfo? ReadVersionInfo(string executable)
        {
            try
            {
                return FileVersionInfo.GetVersionInfo(executable);
            }
            catch
            {
                return null;
            }
        }

        private static string? ReadVersion(FileVersionInfo? info)
        {
            // On Windows, launching chrome.exe --version pops up a browser window and
            // doesn't write to stdout (unlike macOS). Read the version from the file's
            // embedded version info instead — no process is started.
            var fileVersion = info?.FileVersion;
            if (!string.IsNullOrEmpty(fileVersion)) return fileVersion!.Trim();
            var productVersion = info?.ProductVersion;
            if (!string.IsNullOrEmpty(productVersion)) return productVersion!.Trim();
            return null;
        }

        /// <summary>Reads the PE COFF machine field without launching the browser process.</summary>
        public static string ReadArchitecture(string executable)
        {
            try
            {
                using (var stream = new FileStream(executable, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new BinaryReader(stream))
                {
                    if (stream.Length < 64 || reader.ReadUInt16() != 0x5A4D) return "unknown";
                    stream.Position = 0x3C;
                    var peOffset = reader.ReadInt32();
                    if (peOffset < 0 || peOffset > stream.Length - 6) return "unknown";
                    stream.Position = peOffset;
                    if (reader.ReadUInt32() != 0x00004550) return "unknown";
                    switch (reader.ReadUInt16())
                    {
                        case 0x014C: return "x86";
                        case 0x8664: return "x64";
                        case 0xAA64: return "arm64";
                        default: return "unknown";
                    }
                }
            }
            catch { return "unknown"; }
        }
    }
}
