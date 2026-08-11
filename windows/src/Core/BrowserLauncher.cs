using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Threading.Tasks;
using YTray.Models;

namespace YTray.Core
{
    /// <summary>
    /// Builds Chrome launch arguments and starts isolated browser instances.
    /// Mirrors macOS BrowserLauncher; on Windows we launch chrome.exe directly (no execv relay)
    /// and rely on the per-instance --user-data-dir + AUMID for taskbar separation.
    /// </summary>
    public static class BrowserLauncher
    {
        public const string ProxyAuthenticationBootstrapURL = "data:text/html,<title>YTray</title>";

        public static bool SupportsCommandLineExtensions(BrowserKind kind)
        {
            switch (kind)
            {
                case BrowserKind.Chrome:
                case BrowserKind.ChromeBeta:
                case BrowserKind.ChromeCanary:
                    return false;
                case BrowserKind.ChromeForTesting:
                case BrowserKind.Chromium:
                case BrowserKind.Edge:
                    return true;
                default:
                    return true;
            }
        }

        public class LaunchResult
        {
            public Process Process;
            public BrowserInstance Instance;
        }

        public static List<string> BuildArguments(LaunchMode mode, LaunchSettings settings,
            string profilePath, int debugPort, List<BrowserPlugin> plugins,
            BrowserKind? runtimeKind = null, List<string> internalExtensionPaths = null,
            bool restoreLastSession = false)
        {
            var arguments = new List<string>
            {
                $"--user-data-dir={profilePath}",
                "--remote-debugging-address=127.0.0.1",
                $"--remote-debugging-port={debugPort}",
                $"--remote-allow-origins=http://127.0.0.1:{debugPort}",
                "--no-first-run",
                "--no-default-browser-check",
            };

            if (runtimeKind == BrowserKind.ChromeForTesting)
                arguments.Add("--disable-infobars");

            if (settings.IgnoreCertificateErrors)
                arguments.Add("--ignore-certificate-errors");

            if (mode != LaunchMode.Isolated)
            {
                arguments.AddRange(new[]
                {
                    "--disable-background-networking", "--disable-component-update",
                    "--disable-default-apps", "--disable-sync", "--disable-translate",
                });
                if (settings.DisableNotifications) arguments.Add("--disable-notifications");
                if (settings.RestrictWebRTC)
                {
                    arguments.AddRange(new[]
                    {
                        "--disable-webrtc",
                        "--force-webrtc-ip-handling-policy=disable_non_proxied_udp",
                        "--webrtc-ip-handling-policy=disable_non_proxied_udp",
                    });
                }
                var proxy = (settings.ProxyServer ?? "").Trim();
                if (string.IsNullOrEmpty(proxy))
                    arguments.Add("--no-proxy-server");
                else
                    arguments.Add($"--proxy-server={proxy}");

                var paths = (internalExtensionPaths ?? new List<string>())
                    .Concat(plugins.Where(p => p.Enabled).Select(p => p.Path)).ToList();
                if (paths.Count > 0)
                {
                    var joined = string.Join(",", paths);
                    arguments.Add($"--disable-extensions-except={joined}");
                    arguments.Add($"--load-extension={joined}");
                }

                foreach (var line in (settings.AdditionalFlags ?? "").Split('\n'))
                {
                    var flag = line.Trim();
                    if (string.IsNullOrEmpty(flag)) continue;
                    if (!flag.StartsWith("--")) throw new YTrayException(YTrayError.InvalidFlag, flag);
                    if (LaunchSettings.BlockedCustomPrefixes.Any(p => flag.StartsWith(p)))
                        throw new YTrayException(YTrayError.InvalidFlag, flag);
                    arguments.Add(flag);
                }
            }

            if (restoreLastSession)
            {
                arguments.Add("--restore-last-session");
                return arguments;
            }

            var target = (settings.HomeURL ?? "").Trim();
            if (string.IsNullOrEmpty(target) || (!target.StartsWith("chrome://") && !Uri.IsWellFormedUriString(target, UriKind.Absolute)))
                throw new YTrayException(YTrayError.InvalidURL, target);
            arguments.Add(target);
            return arguments;
        }

        public static int NextAvailablePort(int startingAt)
        {
            for (var port = Math.Max(1024, startingAt); port <= Math.Min(startingAt + 200, 65535); port++)
                if (CanBind(port)) return port;
            return startingAt;
        }

        private static bool CanBind(int port)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static LaunchResult Launch(BrowserRuntime runtime, LaunchMode mode, LaunchSettings settings,
            List<BrowserPlugin> plugins, string applicationDirectory, int ordinal, string dockBadge,
            BrowserInstance restoring = null, Func<int, Task> onWindowReady = null)
        {
            if (!File.Exists(runtime.ExecutablePath))
                throw new YTrayException(YTrayError.InvalidExecutable, runtime.ExecutablePath);

            var needsCommandLineExtension = mode != LaunchMode.Isolated && (
                plugins.Any(p => p.Enabled)
                || !string.IsNullOrEmpty(settings.ProxyUsername)
                || !string.IsNullOrEmpty(settings.ProxyPassword));
            if (needsCommandLineExtension && !SupportsCommandLineExtensions(runtime.Kind))
                throw new YTrayException(YTrayError.LaunchFailed,
                    $"{runtime.DisplayTitle} 不接受命令行加载本地插件；请在快速配置中选择 Chrome for Testing、Chromium 或 Edge");

            var id = restoring?.Id ?? Guid.NewGuid();
            var normalizedBadge = DockBadgeLabel.Normalize(dockBadge);
            var profile = restoring != null
                ? restoring.ProfilePath
                : Path.Combine(applicationDirectory, "Profiles", "Inst" + normalizedBadge, id.ToString());
            Directory.CreateDirectory(profile);

            var port = NextAvailablePort(Math.Max(1024, settings.DebugPort));

            var usesProxyAuth = !string.IsNullOrEmpty(settings.ProxyUsername) || !string.IsNullOrEmpty(settings.ProxyPassword);
            var launchSettings = settings;
            if (usesProxyAuth)
                launchSettings.HomeURL = ProxyAuthenticationBootstrapURL;

            var proxyAuthExt = ProxyAuthenticationExtension.Write(id, settings.ProxyUsername ?? "", settings.ProxyPassword ?? "", applicationDirectory);
            var internalPaths = new List<string>();
            if (proxyAuthExt != null) internalPaths.Add(proxyAuthExt);

            List<string> arguments;
            try
            {
                arguments = BuildArguments(mode, launchSettings, profile, port, plugins, runtime.Kind,
                    internalPaths, restoring != null && !usesProxyAuth);
            }
            catch (Exception)
            {
                ProxyAuthenticationExtension.Remove(id, applicationDirectory);
                throw;
            }

            // Write the badged icon for the instance.
            string icoPath = null;
            try
            {
                icoPath = BrowserProcessIcon.Write(runtime.ExecutablePath, normalizedBadge, id, applicationDirectory);
            }
            catch
            {
                // Non-fatal: instance can still launch; taskbar grouping still works via AUMID.
            }

            var psi = new ProcessStartInfo(runtime.ExecutablePath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            // .NET Framework 4.8 has no ProcessStartInfo.ArgumentList; build the raw argument string.
            psi.Arguments = string.Join(" ", arguments.Select(a => a.Contains(" ") ? "\"" + a + "\"" : a));

            // Log file
            var logs = Path.Combine(applicationDirectory, "Logs");
            Directory.CreateDirectory(logs);
            var logFile = Path.Combine(logs, id + ".log");
            var logStream = new FileStream(logFile, FileMode.Create, FileAccess.Write, FileShare.Read);

            Process process;
            try
            {
                process = new Process { StartInfo = psi };
                process.OutputDataReceived += (s, e) => { if (e.Data != null) LogLine(logStream, e.Data); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) LogLine(logStream, e.Data); };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                try { logStream.Dispose(); } catch { }
                BrowserProcessIcon.Remove(id, applicationDirectory);
                ProxyAuthenticationExtension.Remove(id, applicationDirectory);
                throw new YTrayException(YTrayError.LaunchFailed, ex.Message);
            }

            // Create the per-instance .lnk carrying the AUMID + badged icon.
            // The AUMID itself is resolved after the window appears (async, in InstanceStore).
            // For now, pre-create the shortcut with the *expected* AUMID so the taskbar can group it.
            try
            {
                var baseAumid = YTray.Native.ShellLink.ResolveBaseAumid(runtime.Kind);
                var profileId = AumidResolver.ComputeProfileId(profile);
                var expectedAumid = AumidResolver.BuildExpectedAumid(baseAumid, profileId);
                var displayName = $"{runtime.DisplayTitle} · {normalizedBadge}";
                BrowserProcessIcon.WriteInstanceShortcut(runtime.ExecutablePath, psi.Arguments,
                    Path.GetDirectoryName(runtime.ExecutablePath), id, expectedAumid, displayName, applicationDirectory);
            }
            catch
            {
                // best-effort
            }

            var instance = new BrowserInstance
            {
                Id = id,
                Name = restoring?.Name ?? $"浏览器实例 {ordinal}",
                RuntimeID = runtime.Id,
                RuntimeName = runtime.DisplayTitle,
                RuntimeVersion = runtime.Version,
                RuntimeKind = runtime.Kind,
                RuntimeSource = runtime.Source,
                Mode = mode,
                ProcessID = process.Id,
                DebugPort = port,
                ProfilePath = profile,
                StartURL = restoring?.StartURL ?? settings.HomeURL,
                StartedAt = DateTime.Now,
                Status = InstanceStatus.Running,
                LastScreenshotPath = restoring?.LastScreenshotPath,
                ThumbnailPath = restoring?.ThumbnailPath,
                ThumbnailUpdatedAt = restoring?.ThumbnailUpdatedAt,
                LastPageTitle = restoring?.LastPageTitle,
                LastPageURL = restoring?.LastPageURL,
                DockBadge = normalizedBadge,
                SettingsSnapshot = settings,
                PluginIDs = plugins.Select(p => p.Id).ToList(),
            };

            return new LaunchResult { Process = process, Instance = instance };
        }

        private static void LogLine(FileStream stream, string line)
        {
            try
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(line + "\n");
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
            catch { }
        }
    }
}