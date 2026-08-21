#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Threading;
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

        /// <summary>
        /// Unique owner of every resource created for one browser launch. Keeping the process,
        /// redirected log stream and taskbar controller in one disposable object prevents the
        /// three resources from drifting into different lifetimes.
        /// </summary>
        public sealed class LaunchResult : IDisposable
        {
            private readonly FileStream _logStream;
            private int _disposed;

            public Process Process { get; }
            public BrowserInstance Instance { get; }
            public BrowserWindowTaskbarController? TaskbarController { get; private set; }

            internal LaunchResult(Process process, BrowserInstance instance,
                BrowserWindowTaskbarController? taskbarController, FileStream logStream)
            {
                Process = process ?? throw new ArgumentNullException(nameof(process));
                Instance = instance ?? throw new ArgumentNullException(nameof(instance));
                TaskbarController = taskbarController;
                _logStream = logStream ?? throw new ArgumentNullException(nameof(logStream));
            }

            internal void ReleaseTaskbarController()
            {
                var controller = TaskbarController;
                TaskbarController = null;
                try { controller?.Dispose(); }
                catch (Exception ex) { CrashGuard.Record("taskbar-controller-dispose", ex); }
            }

            internal BrowserWindowTaskbarController? TakeTaskbarController()
            {
                var controller = TaskbarController;
                TaskbarController = null;
                return controller;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                ReleaseTaskbarController();
                try { Process.CancelOutputRead(); } catch { }
                try { Process.CancelErrorRead(); } catch { }
                try { Process.EnableRaisingEvents = false; } catch { }
                try { Process.Dispose(); } catch { }
                lock (_logStream)
                {
                    try { _logStream.Dispose(); } catch { }
                }
            }
        }

        public static List<string> BuildArguments(LaunchMode mode, LaunchSettings settings,
            string profilePath, int debugPort, List<BrowserPlugin> plugins,
            BrowserKind? runtimeKind = null, List<string>? internalExtensionPaths = null,
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
                    .Concat(plugins.Select(p => p.Path)).ToList();
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
            BrowserInstance? restoring = null, Func<int, Task>? onWindowReady = null)
        {
            if (runtime == null) throw new ArgumentNullException(nameof(runtime));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            plugins = plugins ?? new List<BrowserPlugin>();
            if (string.IsNullOrWhiteSpace(applicationDirectory))
                throw new ArgumentException("Application directory is required.", nameof(applicationDirectory));
            if (!File.Exists(runtime.ExecutablePath))
                throw new YTrayException(YTrayError.InvalidExecutable, runtime.ExecutablePath);

            var needsCommandLineExtension = mode != LaunchMode.Isolated && (
                plugins.Count > 0
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
            var launchSettings = settings.Clone();
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
            string? icoPath = null;
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

            // Both the shortcut and the live HWND must use the same stable, application-defined
            // identity. Create the shortcut before launch so Explorer never has to reconcile a
            // shortcut that appeared after the Chrome taskbar button.
            var expectedAumid = AumidResolver.BuildInstanceAumid(runtime.Kind, normalizedBadge, id);
            var displayName = $"{runtime.DisplayTitle} · {normalizedBadge}";
            if (!string.IsNullOrEmpty(icoPath))
            {
                try
                {
                    BrowserProcessIcon.WriteInstanceShortcut(runtime.ExecutablePath, psi.Arguments,
                        Path.GetDirectoryName(runtime.ExecutablePath) ?? applicationDirectory,
                        id, expectedAumid, displayName, applicationDirectory);
                }
                catch { /* shortcut creation remains best-effort */ }
            }

            // Log file
            var logs = Path.Combine(applicationDirectory, "Logs");
            Directory.CreateDirectory(logs);
            var logFile = Path.Combine(logs, id + ".log");
            var logStream = new FileStream(logFile, FileMode.Create, FileAccess.Write, FileShare.Read);

            // The hook must already be installed and pumping messages before Process.Start. This
            // ordering is what prevents Explorer from drawing Chrome's stock icon for one frame.
            BrowserWindowTaskbarController? taskbarController = null;
            if (!string.IsNullOrEmpty(icoPath))
            {
                var taskbarIconPath = icoPath!;
                try
                {
                    taskbarController = new BrowserWindowTaskbarController(
                        runtime.ExecutablePath, expectedAumid, taskbarIconPath);
                }
                catch
                {
                    taskbarController?.Dispose();
                    taskbarController = null;
                }
            }

            Process? process = null;
            try
            {
                process = new Process { StartInfo = psi };
                process.OutputDataReceived += (s, e) => { if (e.Data != null) LogLine(logStream, e.Data); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) LogLine(logStream, e.Data); };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                try { taskbarController?.AttachProcess(process.Id); }
                catch
                {
                    taskbarController?.Dispose();
                    taskbarController = null;
                }
            }
            catch (Exception ex)
            {
                taskbarController?.Dispose();
                if (process != null)
                {
                    try { if (!process.HasExited) process.Kill(); } catch { }
                    try { process.Dispose(); } catch { }
                }
                try { logStream.Dispose(); } catch { }
                BrowserProcessIcon.Remove(id, applicationDirectory);
                ProxyAuthenticationExtension.Remove(id, applicationDirectory);
                throw new YTrayException(YTrayError.LaunchFailed, ex.Message);
            }

            // Process.Start either completed above or control escaped through the catch. Keep the
            // invariant explicit so a future edit cannot accidentally construct a running model
            // around a missing native process.
            var runningProcess = process
                ?? throw new InvalidOperationException("Browser process was not created.");

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
                ProcessID = runningProcess.Id,
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
                SettingsSnapshot = settings.Clone(),
                PluginIDs = plugins.Select(p => p.Id).ToList(),
                AppUserModelId = expectedAumid,
            };

            return new LaunchResult(runningProcess, instance, taskbarController, logStream);
        }

        private static void LogLine(FileStream stream, string line)
        {
            try
            {
                // stdout and stderr callbacks may run concurrently. FileStream is not safe for
                // overlapping writes, so the stream itself is also the ownership lock.
                lock (stream)
                {
                    if (!stream.CanWrite) return;
                    var bytes = System.Text.Encoding.UTF8.GetBytes(line + "\n");
                    stream.Write(bytes, 0, bytes.Length);
                }
            }
            catch { }
        }
    }
}
