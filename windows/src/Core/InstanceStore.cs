#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using YTray.Models;

namespace YTray.Core
{
    /// <summary>
    /// Central observable state + process lifecycle manager (mirrors macOS InstanceStore).
    /// </summary>
    public class InstanceStore : INotifyPropertyChanged, IDisposable
    {
        public string ApplicationDirectory { get; }
        private readonly DispatcherTimer _timer;
        private readonly DispatcherTimer _persistenceTimer;
        private readonly Dictionary<Guid, BrowserLauncher.LaunchResult> _launches =
            new Dictionary<Guid, BrowserLauncher.LaunchResult>();
        private readonly Dictionary<Guid, BrowserWindowTaskbarController> _taskbarControllers =
            new Dictionary<Guid, BrowserWindowTaskbarController>();
        private readonly HashSet<Guid> _thumbnailInFlight = new HashSet<Guid>();
        private Dictionary<Guid, DateTime> _lastThumbnailAttempt = new Dictionary<Guid, DateTime>();
        private bool _pageRefreshInFlight;
        private bool _stateDirty;
        private bool _disposed;
        private static readonly TimeSpan ThumbnailRefreshInterval = TimeSpan.FromSeconds(12);
        private static readonly TimeSpan PageRefreshInterval = TimeSpan.FromSeconds(3);
        private const string LegacyDirectoryName = "InstanceDock";

        // Observable collections
        public ObservableCollection<BrowserRuntime> Runtimes { get; } = new ObservableCollection<BrowserRuntime>();
        public ObservableCollection<BrowserPlugin> Plugins { get; } = new ObservableCollection<BrowserPlugin>();
        public ObservableCollection<BrowserInstance> Instances { get; } = new ObservableCollection<BrowserInstance>();
        public LaunchSettings Settings { get; private set; } = new LaunchSettings();
        public List<MirrorVersion> AvailableVersions { get; private set; } = new List<MirrorVersion>();

        // Launch / activity UI state
        public bool IsInstalling { get; private set; }
        public string ActivityMessage { get; private set; } = "";
        public int InstallProgressPercent { get; private set; }
        public long InstallBytesReceived { get; private set; }
        public long? InstallBytesTotal { get; private set; }
        public string? ErrorMessage { get; private set; }
        public BrowserLaunchPhase LaunchPhase { get; private set; } = BrowserLaunchPhase.Idle;
        public string LaunchMessage { get; private set; } = "";
        public LaunchMode? LaunchingMode { get; private set; }
        public bool? LaunchingUsesProxy { get; private set; }
        public Guid? LaunchingInstanceID { get; private set; }
        public Guid? RestoringInstanceID { get; private set; }
        public ProxyCheckPhase ProxyCheckPhase { get; private set; } = ProxyCheckPhase.Idle;
        public string ProxyCheckMessage { get; private set; } = "";
        public ProxyCheckReport? ProxyCheckReport { get; private set; }
        public bool IsProxyAdvancedExpanded { get; set; }
        public Guid? LaunchToken { get; private set; }

        public bool IsLaunching => LaunchPhase == BrowserLaunchPhase.Preparing || LaunchPhase == BrowserLaunchPhase.Waiting;

        public List<BrowserInstance> RunningInstances => Instances.Where(i => i.Status == InstanceStatus.Running).ToList();
        public List<BrowserInstance> HistoryInstances => Instances.Where(i => i.Status != InstanceStatus.Running).ToList();
        public List<BrowserRuntime> SystemRuntimes => Runtimes.Where(r => r.IsSystemEnvironment).OrderBy(r => r.DisplayTitle).ToList();
        public List<BrowserRuntime> ManagedRuntimes => Runtimes.Where(r => r.Source == RuntimeSource.Managed).OrderByDescending(r => r.CreatedAt).ToList();
        public List<BrowserRuntime> LocalBrowserRuntimes => Runtimes.Where(r => r.Source != RuntimeSource.Managed).OrderBy(r => r.DisplayTitle).ToList();

        public BrowserRuntime? DefaultRuntime =>
            Runtimes.FirstOrDefault(r => r.Id == Settings.DefaultRuntimeID)
            ?? SystemRuntimes.FirstOrDefault()
            ?? Runtimes.FirstOrDefault();

        public InstanceStore(string? applicationDirectory = null, bool discoverSystemBrowsers = true)
        {
            ApplicationDirectory = applicationDirectory ?? StatePersistence.DefaultApplicationDirectory;
            // Legacy migration (InstanceDock -> YTray), best-effort.
            MigrateLegacyDirectoryIfNeeded();
            try { Directory.CreateDirectory(ApplicationDirectory); } catch { }

            Load();
            if (discoverSystemBrowsers) RefreshSystemBrowsers();
            RefreshProcessStates();

            _timer = new DispatcherTimer(DispatcherPriority.Background);
            _timer.Interval = PageRefreshInterval;
            _timer.Tick += OnMaintenanceTick;
            _persistenceTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
            {
                Interval = TimeSpan.FromMilliseconds(450),
            };
            _persistenceTimer.Tick += OnPersistenceTick;
            _timer.Start();
        }

        private void OnMaintenanceTick(object sender, EventArgs e)
        {
            if (_disposed) return;
            RefreshProcessStates();
            CrashGuard.Observe(RefreshRunningPageTitlesAsync(), "refresh-page-state");
            ScheduleAutomaticThumbnailRefresh();
        }

        private void OnPersistenceTick(object sender, EventArgs e)
        {
            _persistenceTimer.Stop();
            PersistPendingState();
        }

        private void MigrateLegacyDirectoryIfNeeded()
        {
            try
            {
                var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LegacyDirectoryName);
                if (Directory.Exists(legacy) && !Directory.Exists(ApplicationDirectory))
                    Directory.Move(legacy, ApplicationDirectory);
            }
            catch { }
        }

        public void RefreshSystemBrowsers()
        {
            var discovered = SystemBrowserDiscovery.Discover();
            var discoveredPaths = discovered.Select(d => d.ExecutablePath).ToHashSet();
            // Remove system runtimes that no longer exist.
            var toRemove = Runtimes.Where(r => r.Source == RuntimeSource.System && !discoveredPaths.Contains(r.ExecutablePath)).ToList();
            foreach (var r in toRemove)
                Runtimes.Remove(r);
            // Upsert discovered.
            foreach (var rt in discovered) Upsert(rt, persist: false);
            if (Settings.DefaultRuntimeID == null || !Runtimes.Any(r => r.Id == Settings.DefaultRuntimeID))
                Settings.DefaultRuntimeID = SystemRuntimes.FirstOrDefault()?.Id ?? Runtimes.FirstOrDefault()?.Id;
            Save();
        }

        public BrowserRuntime? AddLocalRuntime(string selectedPath)
        {
            var runtime = SystemBrowserDiscovery.Inspect(selectedPath);
            if (runtime == null)
            {
                Report(new YTrayException(YTrayError.InvalidExecutable, selectedPath));
                return null;
            }
            return Upsert(runtime);
        }

        public BrowserRuntime Upsert(BrowserRuntime runtime, bool persist = true)
        {
            var existing = Runtimes.FirstOrDefault(r => r.ExecutablePath == runtime.ExecutablePath);
            if (existing != null)
            {
                runtime.Id = existing.Id;
                runtime.CreatedAt = existing.CreatedAt;
                var idx = Runtimes.IndexOf(existing);
                Runtimes[idx] = runtime;
            }
            else
            {
                Runtimes.Add(runtime);
            }
            if (Settings.DefaultRuntimeID == null) Settings.DefaultRuntimeID = runtime.Id;
            if (persist) Save();
            return runtime;
        }

        public void RemoveRuntime(BrowserRuntime runtime)
        {
            if (RunningInstances.Any(i => i.RuntimeID == runtime.Id))
            {
                Report(new YTrayException(YTrayError.LaunchFailed, "该运行时仍有实例正在运行"));
                return;
            }
            var r = Runtimes.FirstOrDefault(x => x.Id == runtime.Id);
            if (r != null) Runtimes.Remove(r);
            if (Settings.DefaultRuntimeID == runtime.Id) Settings.DefaultRuntimeID = Runtimes.FirstOrDefault()?.Id;
            Save();
        }

        public void AddPlugin(string directory)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(directory))
                    throw new YTrayException(YTrayError.InvalidPlugin, "未选择插件目录");
                var manifestPath = Path.Combine(directory, "manifest.json");
                var json = File.ReadAllText(manifestPath);
                var manifest = Newtonsoft.Json.JsonConvert.DeserializeObject<PluginManifest>(json);
                if (manifest == null || string.IsNullOrWhiteSpace(manifest.Name))
                    throw new YTrayException(YTrayError.InvalidPlugin, directory);
                var existing = Plugins.FirstOrDefault(p => p.Path == directory);
                if (existing != null) Plugins.Remove(existing);
                Plugins.Add(new BrowserPlugin
                {
                    Name = manifest.Name, Version = manifest.Version,
                    Path = directory, ManifestVersion = manifest.ManifestVersion,
                });
                Save();
            }
            catch
            {
                Report(new YTrayException(YTrayError.InvalidPlugin, directory));
            }
        }

        public void UpdatePlugin(BrowserPlugin plugin)
        {
            if (plugin == null) return;
            var idx = Plugins.ToList().FindIndex(p => p.Id == plugin.Id);
            if (idx >= 0) Plugins[idx] = plugin;
            Save();
        }

        public void RemovePlugin(BrowserPlugin plugin)
        {
            if (plugin == null) return;
            var p = Plugins.FirstOrDefault(x => x.Id == plugin.Id);
            if (p != null) Plugins.Remove(p);
            Settings.DefaultPluginIDs.RemoveAll(g => g == plugin.Id);
            Save();
        }

        public void SelectDefaultRuntime(BrowserRuntime runtime)
        {
            if (runtime == null) return;
            if (!Runtimes.Any(r => r.Id == runtime.Id)) return;
            Settings.DefaultRuntimeID = runtime.Id;
            Save();
        }

        public BrowserRuntime? RuntimeFor(BrowserInstance? instance) =>
            instance == null ? null : Runtimes.FirstOrDefault(r => r.Id == instance.RuntimeID);

        public void Stop(BrowserInstance? instance) =>
            CrashGuard.Observe(StopAsync(instance), "stop-browser");

        public async Task<bool> StopAsync(BrowserInstance? instance)
        {
            var current = Instances.FirstOrDefault(x => x.Id == instance?.Id);
            if (current == null || current.Status != InstanceStatus.Running || current.IsStopping) return false;

            current.IsStopping = true;
            ErrorMessage = null;
            OnPropertyChanged(string.Empty);
            try
            {
                // Preserve the last visible state when CDP responds promptly, but never make the
                // stop button wait indefinitely for a screenshot or page-title request.
                var preserveTask = Task.WhenAll(
                    CaptureAndStoreThumbnailAsync(current),
                    RefreshPageTitleAsync(current));
                await Task.WhenAny(preserveTask, Task.Delay(900));
                // The preservation workflow may legitimately outlive the 900ms interaction
                // budget, but its eventual fault must still be observed.
                CrashGuard.Observe(preserveTask, "preserve-before-stop");

                Process? process = null;
                var ownsTemporaryProcess = false;
                if (_launches.TryGetValue(current.Id, out var launch) && IsProcessUsable(launch.Process))
                    process = launch.Process;
                else if (current.ProcessID > 0 && IsExpectedInstanceProcess(current))
                {
                    try
                    {
                        process = System.Diagnostics.Process.GetProcessById(current.ProcessID);
                        ownsTemporaryProcess = true;
                    }
                    catch { process = null; }
                }

                try
                {
                    if (process != null && IsProcessUsable(process)) process.Kill();
                }
                finally
                {
                    if (ownsTemporaryProcess) process?.Dispose();
                }

                // Exited may dispose the tracked Process while this operation is awaiting. Polling
                // by PID avoids using a process wrapper with a competing lifetime.
                for (var attempt = 0; attempt < 14 && current.ProcessID > 0 && IsExpectedInstanceProcess(current); attempt++)
                    await Task.Delay(100);

                if (current.ProcessID > 0 && IsExpectedInstanceProcess(current))
                    throw new YTrayException(YTrayError.LaunchFailed, "浏览器进程没有响应停止请求");

                MarkStopped(current.Id);
                return true;
            }
            catch (Exception ex)
            {
                Report(ex);
                return false;
            }
            finally
            {
                current.IsStopping = false;
                OnPropertyChanged(string.Empty);
            }
        }

        private static bool IsProcessUsable(Process? process)
        {
            if (process == null) return false;
            try { return !process.HasExited; }
            catch (ObjectDisposedException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        public void RemoveHistory(BrowserInstance instance)
        {
            if (instance == null) return;
            if (instance.Status == InstanceStatus.Running) return;
            var i = Instances.FirstOrDefault(x => x.Id == instance.Id);
            if (i != null) Instances.Remove(i);
            BrowserProcessIcon.Remove(instance.Id, ApplicationDirectory);
            InstanceThumbnailStorage.RemoveThumbnail(instance, ApplicationDirectory);
            Save();
        }

        public void RemoveAllHistory()
        {
            var history = HistoryInstances;
            foreach (var i in history)
            {
                BrowserProcessIcon.Remove(i.Id, ApplicationDirectory);
                InstanceThumbnailStorage.RemoveThumbnail(i, ApplicationDirectory);
            }
            foreach (var i in history.ToList()) Instances.Remove(i);
            Save();
        }

        public void RenameHistory(BrowserInstance instance, string? name)
        {
            if (instance == null) return;
            var i = Instances.FirstOrDefault(x => x.Id == instance.Id);
            if (i == null || i.Status == InstanceStatus.Running) return;
            var v = name?.Trim();
            if (v == null || v.Length == 0) return;
            i.Name = v;
            Save();
        }

        public async Task<string?> CaptureAsync(BrowserInstance? instance)
        {
            var current = Instances.FirstOrDefault(x => x.Id == instance?.Id);
            if (current == null || current.Status != InstanceStatus.Running || current.IsCapturing || current.IsStopping)
                return null;

            current.IsCapturing = true;
            current.PreviewError = null;
            ErrorMessage = null;
            OnPropertyChanged(string.Empty);
            try
            {
                var pictures = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "YTray");
                Directory.CreateDirectory(pictures);
                var output = await ScreenshotService.CaptureAsync(current.DebugPort, current.Id, pictures);
                var i = Instances.FirstOrDefault(x => x.Id == current.Id);
                if (i != null)
                {
                    i.LastScreenshotPath = output;
                    // Reuse the successful full-size capture immediately instead of waiting for
                    // the next 12-second thumbnail cycle.
                    i.ThumbnailPath = output;
                    i.ThumbnailUpdatedAt = DateTime.Now;
                    i.PreviewError = null;
                }
                Save();
                return output;
            }
            catch (Exception ex)
            {
                current.PreviewError = "页面预览暂不可用";
                Report(ex);
                return null;
            }
            finally
            {
                current.IsCapturing = false;
                OnPropertyChanged(string.Empty);
            }
        }

        public void RevealProfile(BrowserInstance instance)
        {
            try
            {
                if (instance == null || string.IsNullOrWhiteSpace(instance.ProfilePath)
                    || !Directory.Exists(instance.ProfilePath)) return;
                System.Diagnostics.Process.Start(new ProcessStartInfo("explorer.exe", instance.ProfilePath) { UseShellExecute = true });
            }
            catch { }
        }

        public void Focus(BrowserInstance instance)
        {
            try
            {
                if (instance == null || instance.ProcessID <= 0 || !IsExpectedInstanceProcess(instance)) return;
                var p = System.Diagnostics.Process.GetProcessById(instance.ProcessID);
                using (p)
                {
                    var hwnd = YTray.Native.WindowEnum.FindFirstVisibleWindow(p.Id);
                    if (hwnd != IntPtr.Zero)
                    {
                        YTray.Native.Win32.ShowWindow(hwnd, YTray.Native.Win32.SW_RESTORE);
                        YTray.Native.Win32.SetForegroundWindow(hwnd);
                    }
                }
            }
            catch { }
        }

        public void SaveSettings() => Save();

        public void SetThemePreference(AppThemePreference preference)
        {
            if (!Enum.IsDefined(typeof(AppThemePreference), preference))
                preference = AppThemePreference.System;
            Settings.ThemePreference = preference;
            ThemeManager.SetPreference(preference);
            Save();
        }

        public void Launch(LaunchMode mode, LaunchSettings? customSettings = null, List<Guid>? customPluginIDs = null,
            BrowserInstance? restoring = null, bool? launchUsesProxy = null)
        {
            if (IsLaunching) return;
            var token = Guid.NewGuid();
            LaunchToken = token;
            LaunchingMode = mode;
            LaunchingUsesProxy = launchUsesProxy;
            RestoringInstanceID = restoring?.Id;
            LaunchMessage = "正在准备浏览器…";
            LaunchPhase = BrowserLaunchPhase.Preparing;
            ErrorMessage = null;
            OnPropertyChanged(string.Empty);

            // A launch owns an isolated settings value. Never let BrowserLauncher or restore
            // mutations alias the live application settings or a historical snapshot.
            var configuration = (customSettings ?? Settings).Clone();
            if (mode == LaunchMode.Isolated)
                configuration = new LaunchSettings(configuration.DefaultRuntimeID ?? Settings.DefaultRuntimeID);
            if (restoring != null)
            {
                configuration.DefaultRuntimeID = restoring.RuntimeID;
                configuration.HomeURL = restoring.StartURL;
                configuration.DockBadge = restoring.DockBadge ?? "";
            }

            var runtimeID = configuration.DefaultRuntimeID ?? Runtimes.FirstOrDefault()?.Id;
            if (runtimeID == null || !Runtimes.Any(r => r.Id == runtimeID))
            {
                FinishLaunchFailure(new YTrayException(YTrayError.NoRuntime, ""), token);
                return;
            }
            var runtime = Runtimes.First(r => r.Id == runtimeID);
            var selectedIDs = customPluginIDs ?? configuration.DefaultPluginIDs;
            var selectedPlugins = Plugins.Where(p => selectedIDs.Contains(p.Id) && p.Enabled).ToList();

            try
            {
                var requestedBadge = (restoring?.DockBadge ?? configuration.DockBadge ?? "").Trim();
                var badge = string.IsNullOrEmpty(requestedBadge) ? NextAvailableDockBadge() : DockBadgeLabel.Normalize(requestedBadge);
                if (RunningInstances.Any(i => i.DockBadge == badge))
                    throw new YTrayException(YTrayError.LaunchFailed, $"Dock 角标 {badge} 已被运行中的实例使用");

                var result = BrowserLauncher.Launch(runtime, mode, configuration, selectedPlugins,
                    ApplicationDirectory, Instances.Count + 1, badge, restoring);
                if (_launches.TryGetValue(result.Instance.Id, out var previousLaunch))
                {
                    _launches.Remove(result.Instance.Id);
                    previousLaunch.Dispose();
                }
                _launches[result.Instance.Id] = result;
                ReleaseTaskbarController(result.Instance.Id);
                var taskbarController = result.TakeTaskbarController();
                if (taskbarController != null)
                    _taskbarControllers[result.Instance.Id] = taskbarController;
                result.Process.EnableRaisingEvents = true;
                result.Process.Exited += (s, e) =>
                {
                    var dispatcher = System.Windows.Application.Current?.Dispatcher;
                    if (dispatcher == null || dispatcher.HasShutdownStarted) return;
                    dispatcher.BeginInvoke(new Action(() => MarkStopped(result.Instance.Id)),
                        DispatcherPriority.Background);
                };

                if (restoring != null)
                {
                    var old = Instances.FirstOrDefault(i => i.Id == restoring.Id);
                    if (old != null) Instances.Remove(old);
                }
                Instances.Insert(0, result.Instance);
                Save();
                LaunchingInstanceID = result.Instance.Id;
                LaunchMessage = $"正在启动 {runtime.DisplayTitle}…";
                LaunchPhase = BrowserLaunchPhase.Waiting;

                CrashGuard.Observe(WaitForBrowserAsync(result.Instance, token), "wait-for-browser");
            }
            catch (Exception ex)
            {
                FinishLaunchFailure(ex, token);
            }
        }

        public void LaunchConfigured(bool usePresetProxy)
        {
            var cfg = QuickLaunchConfiguration(usePresetProxy);
            if (cfg == null) return;
            Launch(LaunchMode.Quick, cfg, launchUsesProxy: usePresetProxy);
        }

        public LaunchSettings? QuickLaunchConfiguration(bool usePresetProxy)
        {
            var cfg = new LaunchSettings
            {
                DefaultRuntimeID = Settings.DefaultRuntimeID,
                HomeURL = Settings.HomeURL,
                DebugPort = Settings.DebugPort,
                RestrictWebRTC = Settings.RestrictWebRTC,
                DisableNotifications = Settings.DisableNotifications,
                IgnoreCertificateErrors = Settings.IgnoreCertificateErrors,
                AdditionalFlags = Settings.AdditionalFlags,
                DefaultPluginIDs = Settings.DefaultPluginIDs.ToList(),
                DockBadge = Settings.DockBadge,
            };
            if (usePresetProxy)
            {
                var server = RememberPresetProxy();
                if (server == null) return null;
                cfg.ProxyServer = server;
                cfg.ProxyUsername = Settings.PresetProxyUsername;
                cfg.ProxyPassword = Settings.PresetProxyPassword;
            }
            else
            {
                cfg.ProxyServer = "";
                cfg.ProxyUsername = "";
                cfg.ProxyPassword = "";
            }
            return cfg;
        }

        public void RestoreHistory(BrowserInstance instance)
        {
            if (instance == null) return;
            if (instance.Status == InstanceStatus.Running) return;
            var cfg = instance.SettingsSnapshot?.Clone() ?? new LaunchSettings(instance.RuntimeID)
            {
                HomeURL = instance.StartURL,
                DockBadge = instance.DockBadge ?? "",
            };
            cfg.DefaultRuntimeID = instance.RuntimeID;
            cfg.HomeURL = instance.StartURL;
            cfg.DockBadge = instance.DockBadge ?? "";
            var restoredPluginIDs = instance.PluginIDs ?? cfg.DefaultPluginIDs;
            Launch(instance.Mode, cfg, restoredPluginIDs, instance,
                launchUsesProxy: !string.IsNullOrEmpty(cfg.ProxyServer?.Trim()));
        }

        private async Task WaitForBrowserAsync(BrowserInstance instance, Guid token)
        {
            var ready = await ScreenshotService.WaitUntilReadyAsync(instance.DebugPort);
            if (LaunchToken != token) return;
            if (!ready)
            {
                var stillRunning = instance.ProcessID > 0 && IsExpectedInstanceProcess(instance);
                var detail = stillRunning ? "浏览器进程已经创建，但调试端口未在 15 秒内就绪" : "浏览器进程在完成启动前退出";
                FinishLaunchFailure(new YTrayException(YTrayError.LaunchFailed, detail), token);
                return;
            }

            // The staged controller normally installs the custom AUMID/ICO before the window becomes
            // visible. Keep the former post-launch path only as a fallback when WinEvent/DWM staging
            // is unavailable on this machine.
            var taskbarReady = false;
            if (_taskbarControllers.TryGetValue(instance.Id, out var taskbarController))
            {
                try { taskbarReady = await taskbarController.WaitForInitialWindowAsync(TimeSpan.FromSeconds(3)); }
                catch { taskbarReady = false; }
                if (!taskbarReady) ReleaseTaskbarController(instance.Id);
            }

            if (!taskbarReady)
            {
                try
                {
                    var aumid = await AumidResolver.ResolveAsync(instance.ProcessID, instance.ProfilePath,
                        instance.RuntimeKind ?? BrowserKind.Chrome, TimeSpan.FromSeconds(5));
                    var i = Instances.FirstOrDefault(x => x.Id == instance.Id);
                    if (i != null)
                    {
                        var appliedAumid = string.IsNullOrEmpty(i.AppUserModelId)
                            ? aumid
                            : i.AppUserModelId!;
                        i.AppUserModelId = appliedAumid;
                        BrowserProcessIcon.ApplyToProcessWindow(i.Id, i.ProcessID, ApplicationDirectory,
                            appliedAumid, $"{i.RuntimeName} · {i.DockBadge}");
                        Save();
                    }
                }
                catch { /* live taskbar metadata remains best-effort on the fallback path */ }
            }

            var usesProxyAuth = !string.IsNullOrEmpty(instance.SettingsSnapshot?.ProxyUsername)
                || !string.IsNullOrEmpty(instance.SettingsSnapshot?.ProxyPassword);
            var restoreURL = instance.LastPageURL?.Trim();
            var navigationTarget = RestoringInstanceID == instance.Id
                ? (!string.IsNullOrEmpty(restoreURL) ? restoreURL : instance.StartURL)
                : (usesProxyAuth ? instance.StartURL : null);

            if (!string.IsNullOrEmpty(navigationTarget))
            {
                try
                {
                    if (usesProxyAuth) await Task.Delay(900);
                    await ScreenshotService.NavigateAsync(instance.DebugPort, navigationTarget!);
                }
                catch (Exception ex)
                {
                    FinishLaunchFailure(ex, token);
                    return;
                }
            }

            // Populate the compact widget immediately; the periodic poll keeps it current later.
            await RefreshPageTitleAsync(instance);

            LaunchMessage = $"{instance.RuntimeName} 已启动";
            LaunchPhase = BrowserLaunchPhase.Succeeded;
            LaunchingMode = null;
            LaunchingUsesProxy = null;
            LaunchingInstanceID = null;
            RestoringInstanceID = null;
            OnPropertyChanged(string.Empty);
            ScheduleAutomaticThumbnailRefresh(instance.Id);
            await Task.Delay(1200);
            if (LaunchToken != token || LaunchPhase != BrowserLaunchPhase.Succeeded) return;
            LaunchMessage = "";
            LaunchPhase = BrowserLaunchPhase.Idle;
            LaunchToken = null;
            OnPropertyChanged(string.Empty);
        }

        private void FinishLaunchFailure(Exception ex, Guid token)
        {
            if (LaunchToken != token) return;
            LaunchPhase = BrowserLaunchPhase.Idle;
            LaunchMessage = "";
            LaunchingMode = null;
            LaunchingUsesProxy = null;
            LaunchingInstanceID = null;
            RestoringInstanceID = null;
            LaunchToken = null;
            Report(ex);
        }

        public string? RememberPresetProxy()
        {
            try
            {
                var normalized = HTTPProxyAddress.Build(Settings.PresetProxyScheme, Settings.PresetProxyHost, Settings.PresetProxyPort);
                var remark = (Settings.PresetProxyRemark ?? "").Trim();
                var username = (Settings.PresetProxyUsername ?? "").Trim();
                var password = Settings.PresetProxyPassword;
                Settings.PresetProxyServer = normalized;
                Settings.PresetProxyHost = HTTPProxyAddress.Split(normalized).Host;
                Settings.PresetProxyUsername = username;
                Settings.PresetProxyRemark = remark;
                Settings.RecentProxyPresets.RemoveAll(p =>
                    string.Equals(p.Server, normalized, StringComparison.OrdinalIgnoreCase) && p.Username == username);
                Settings.RecentProxyPresets.Insert(0, new ProxyPreset(normalized, remark, username, password));
                if (Settings.RecentProxyPresets.Count > 5)
                    Settings.RecentProxyPresets = Settings.RecentProxyPresets.Take(5).ToList();
                Save();
                return normalized;
            }
            catch (Exception ex)
            {
                Report(ex);
                return null;
            }
        }

        public async Task CheckPresetProxyAsync()
        {
            if (ProxyCheckPhase == ProxyCheckPhase.Checking) return;
            ProxyEndpoint endpoint;
            try
            {
                var server = HTTPProxyAddress.Build(Settings.PresetProxyScheme, Settings.PresetProxyHost, Settings.PresetProxyPort);
                endpoint = HTTPProxyAddress.Split(server);
            }
            catch (Exception ex)
            {
                ProxyCheckPhase = ProxyCheckPhase.Failure;
                ProxyCheckMessage = ex.Message;
                OnPropertyChanged(string.Empty);
                return;
            }
            ProxyCheckPhase = ProxyCheckPhase.Checking;
            ProxyCheckMessage = "检测中 · 最多 10 秒";
            ProxyCheckReport = null;
            OnPropertyChanged(string.Empty);
            var username = (Settings.PresetProxyUsername ?? "").Trim();
            var password = Settings.PresetProxyPassword;
            try
            {
                var report = await ProxyConnectivityChecker.CheckDefaultTargetsAsync(endpoint, username,
                    password, Settings.PresetProxyCheckTarget, ProxyConnectivityChecker.DefaultTimeout);
                if (_disposed) return;
                ProxyCheckReport = report;
                ProxyCheckPhase = report.IsSuccess ? ProxyCheckPhase.Success : ProxyCheckPhase.Failure;
                ProxyCheckMessage = report.Message;
            }
            catch (Exception ex)
            {
                ProxyCheckPhase = ProxyCheckPhase.Failure;
                ProxyCheckMessage = ex.Message;
                CrashGuard.Record("proxy-check", ex);
            }
            OnPropertyChanged(string.Empty);
        }

        public void SelectProxyPreset(ProxyPreset preset)
        {
            if (preset == null) return;
            try
            {
                var endpoint = HTTPProxyAddress.Split(preset.Server);
                Settings.PresetProxyServer = endpoint.Server;
                Settings.PresetProxyScheme = endpoint.Scheme;
                Settings.PresetProxyHost = endpoint.Host;
                Settings.PresetProxyPort = endpoint.Port;
                Settings.PresetProxyUsername = preset.Username;
                Settings.PresetProxyPassword = preset.Password;
                Settings.PresetProxyRemark = preset.Remark;
                ResetProxyCheck();
                Save();
            }
            catch { }
        }

        public void UpdatePresetProxyServer(string value)
        {
            Settings.PresetProxyServer = value;
            try
            {
                var endpoint = HTTPProxyAddress.Split(value);
                Settings.PresetProxyScheme = endpoint.Scheme;
                Settings.PresetProxyHost = endpoint.Host;
                Settings.PresetProxyPort = endpoint.Port;
            }
            catch { }
            ResetProxyCheck();
            Save();
        }
        public void UpdatePresetProxyScheme(ProxyScheme v) { Settings.PresetProxyScheme = v; SyncPresetProxyServer(); }
        public void UpdatePresetProxyHost(string v) { Settings.PresetProxyHost = v; SyncPresetProxyServer(); }
        public void UpdatePresetProxyPort(int v) { Settings.PresetProxyPort = v; SyncPresetProxyServer(); }
        public void UpdatePresetProxyUsername(string v) { Settings.PresetProxyUsername = v; ResetProxyCheck(); Save(); }
        public void UpdatePresetProxyPassword(string v) { Settings.PresetProxyPassword = v; ResetProxyCheck(); Save(); }
        public void UpdatePresetProxyRemark(string v) { Settings.PresetProxyRemark = v; Save(); }
        public void UpdatePresetProxyCheckTarget(string v) { Settings.PresetProxyCheckTarget = v; ResetProxyCheck(); Save(); }

        private void SyncPresetProxyServer()
        {
            try
            {
                var server = HTTPProxyAddress.Build(Settings.PresetProxyScheme, Settings.PresetProxyHost, Settings.PresetProxyPort);
                Settings.PresetProxyServer = server;
            }
            catch { }
            ResetProxyCheck();
            Save();
        }

        private void ResetProxyCheck()
        {
            ProxyCheckPhase = ProxyCheckPhase.Idle;
            ProxyCheckMessage = "";
            ProxyCheckReport = null;
        }

        private string NextAvailableDockBadge()
        {
            var used = new HashSet<string>(RunningInstances
                .Where(i => !string.IsNullOrEmpty(i.DockBadge))
                .Select(i => i.DockBadge!));
            for (int ordinal = 1; ordinal <= 702; ordinal++)
            {
                var candidate = DockBadgeLabel.DefaultLabel(ordinal);
                if (!used.Contains(candidate)) return candidate;
            }
            return "ZZ";
        }

        public async Task RefreshManifestAsync()
        {
            try { AvailableVersions = await RuntimeInstaller.FetchVersionsAsync(); }
            catch (Exception ex) { Report(ex); }
            OnPropertyChanged(string.Empty);
        }

        public async Task InstallAsync(MirrorVersion version)
        {
            IsInstalling = true;
            ErrorMessage = null;
            InstallProgressPercent = 0;
            InstallBytesReceived = 0;
            InstallBytesTotal = null;
            ActivityMessage = $"正在准备 {version.Version}…";
            OnPropertyChanged(string.Empty);
            try
            {
                var progress = new Progress<RuntimeInstaller.InstallProgress>(value =>
                {
                    InstallProgressPercent = value.Percent;
                    InstallBytesReceived = value.BytesReceived;
                    InstallBytesTotal = value.TotalBytes;
                    ActivityMessage = value.Message ?? "正在安装…";
                    OnPropertyChanged(string.Empty);
                });
                var rt = await RuntimeInstaller.InstallAsync(version, ApplicationDirectory, progress);
                Upsert(rt);
                InstallProgressPercent = 100;
                ActivityMessage = $"已安装 {version.Version}";
            }
            catch (Exception ex)
            {
                Report(ex);
                ActivityMessage = ErrorMessage ?? "安装失败";
            }
            finally
            {
                IsInstalling = false;
                OnPropertyChanged(string.Empty);
            }
        }

        private void RefreshProcessStates()
        {
            var changed = false;
            foreach (var i in Instances.ToList())
            {
                if (i.Status != InstanceStatus.Running) continue;
                if (i.ProcessID <= 0 || !IsExpectedInstanceProcess(i))
                {
                    i.Status = InstanceStatus.Stopped;
                    ReleaseTaskbarController(i.Id);
                    BrowserProcessIcon.Remove(i.Id, ApplicationDirectory);
                    ProxyAuthenticationExtension.Remove(i.Id, ApplicationDirectory);
                    changed = true;
                }
            }
            if (changed) { ConsolidateHistoryBadges(); Save(); }
        }

        private bool IsExpectedInstanceProcess(BrowserInstance instance)
        {
            if (instance == null || instance.ProcessID <= 0) return false;
            if (_launches.TryGetValue(instance.Id, out var owned))
                return owned.Process.Id == instance.ProcessID && IsProcessUsable(owned.Process);

            var expectedExecutable = RuntimeFor(instance)?.ExecutablePath;
            if (string.IsNullOrWhiteSpace(expectedExecutable) || !File.Exists(expectedExecutable)) return false;
            try
            {
                using (var process = System.Diagnostics.Process.GetProcessById(instance.ProcessID))
                {
                    if (process.HasExited) return false;
                    var actualExecutable = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(actualExecutable)
                        || !string.Equals(Path.GetFullPath(actualExecutable), Path.GetFullPath(expectedExecutable),
                            StringComparison.OrdinalIgnoreCase)) return false;

                    // StartedAt is recorded immediately after Process.Start. A reused PID points to
                    // a different process generation and must never be focused or terminated.
                    return Math.Abs((process.StartTime - instance.StartedAt).TotalSeconds) <= 10;
                }
            }
            catch { return false; }
        }

        private void MarkStopped(Guid id)
        {
            if (_launches.TryGetValue(id, out var launch))
            {
                _launches.Remove(id);
                launch.Dispose();
            }
            ReleaseTaskbarController(id);
            BrowserProcessIcon.Remove(id, ApplicationDirectory);
            ProxyAuthenticationExtension.Remove(id, ApplicationDirectory);
            if (LaunchingInstanceID == id && IsLaunching && LaunchToken is Guid token)
                FinishLaunchFailure(new YTrayException(YTrayError.LaunchFailed, "浏览器进程在完成启动前退出"), token);
            var i = Instances.FirstOrDefault(x => x.Id == id);
            if (i != null)
            {
                i.Status = InstanceStatus.Stopped;
                ConsolidateHistoryBadges();
                Save();
            }
        }

        private void ReleaseTaskbarController(Guid id)
        {
            if (!_taskbarControllers.TryGetValue(id, out var controller)) return;
            _taskbarControllers.Remove(id);
            try { controller.Dispose(); } catch { }
        }

        private void ConsolidateHistoryBadges()
        {
            var history = HistoryInstances;
            var newestByBadge = new Dictionary<string, BrowserInstance>();
            foreach (var i in history)
            {
                var badge = i.DockBadge;
                if (string.IsNullOrEmpty(badge)) continue;
                if (newestByBadge.TryGetValue(badge!, out var cur) && cur.StartedAt >= i.StartedAt) continue;
                newestByBadge[badge!] = i;
            }
            var removed = history.Where(i => !string.IsNullOrEmpty(i.DockBadge)
                && newestByBadge.TryGetValue(i.DockBadge!, out var n) && n.Id != i.Id).ToList();
            foreach (var i in removed)
            {
                Instances.Remove(i);
                BrowserProcessIcon.Remove(i.Id, ApplicationDirectory);
                InstanceThumbnailStorage.RemoveThumbnail(i, ApplicationDirectory);
            }
        }

        private async Task RefreshRunningPageTitlesAsync()
        {
            if (_disposed || _pageRefreshInFlight) return;
            var targets = RunningInstances.Select(i => (i.Id, i.DebugPort)).ToList();
            if (targets.Count == 0) return;
            _pageRefreshInFlight = true;
            try
            {
                var requests = targets.Select(async target =>
                {
                    var state = await ScreenshotService.CurrentPageStateAsync(target.DebugPort);
                    return (target.Id, State: state);
                }).ToArray();
                var results = await Task.WhenAll(requests);
                if (_disposed) return;
                var changed = false;
                foreach (var result in results)
                {
                    if (!result.State.HasValue) continue;
                    var state = result.State.Value;
                    var i = Instances.FirstOrDefault(x => x.Id == result.Id);
                    if (i == null) continue;
                    if (!string.IsNullOrEmpty(state.Title) && i.LastPageTitle != state.Title) { i.LastPageTitle = state.Title; changed = true; }
                    if (!string.IsNullOrEmpty(state.URL) && i.LastPageURL != state.URL) { i.LastPageURL = state.URL; changed = true; }
                }
                if (changed) SaveDeferred();
            }
            catch (Exception ex)
            {
                CrashGuard.Record("refresh-page-state", ex);
            }
            finally { _pageRefreshInFlight = false; }
        }

        private async Task RefreshPageTitleAsync(BrowserInstance instance)
        {
            var state = await ScreenshotService.CurrentPageStateAsync(instance.DebugPort, 5);
            if (_disposed || !state.HasValue) return;
            var s = state.Value;
            var i = Instances.FirstOrDefault(x => x.Id == instance.Id);
            if (i == null) return;
            if (!string.IsNullOrEmpty(s.Title)) i.LastPageTitle = s.Title;
            if (!string.IsNullOrEmpty(s.URL)) i.LastPageURL = s.URL;
            SaveDeferred();
        }

        private void ScheduleAutomaticThumbnailRefresh(Guid? forceInstanceID = null)
        {
            var now = DateTime.UtcNow;
            foreach (var instance in RunningInstances)
            {
                var isForced = instance.Id == forceInstanceID;
                var last = _lastThumbnailAttempt.TryGetValue(instance.Id, out var t)
                    ? t : (instance.ThumbnailUpdatedAt ?? DateTime.MinValue);
                if (!isForced && now - last < ThumbnailRefreshInterval) continue;
                if (_thumbnailInFlight.Contains(instance.Id)) continue;
                _thumbnailInFlight.Add(instance.Id);
                _lastThumbnailAttempt[instance.Id] = now;
                CrashGuard.Observe(CaptureAutomaticThumbnailAsync(instance), "automatic-thumbnail");
            }
        }

        private async Task CaptureAutomaticThumbnailAsync(BrowserInstance instance)
        {
            try { await CaptureAndStoreThumbnailAsync(instance); }
            finally
            {
                if (!_disposed) _thumbnailInFlight.Remove(instance.Id);
            }
        }

        private async Task CaptureAndStoreThumbnailAsync(BrowserInstance instance)
        {
            if (!Instances.Any(i => i.Id == instance.Id && i.Status == InstanceStatus.Running)) return;
            var output = InstanceThumbnailStorage.ThumbnailURL(instance.Id, ApplicationDirectory);
            try
            {
                var captured = await ScreenshotService.CaptureThumbnailAsync(instance.DebugPort, instance.Id, output);
                var i = Instances.FirstOrDefault(x => x.Id == instance.Id);
                if (!_disposed && i != null)
                {
                    i.ThumbnailPath = captured;
                    i.ThumbnailUpdatedAt = DateTime.Now;
                    i.PreviewError = null;
                    SaveDeferred();
                }
            }
            catch
            {
                var i = Instances.FirstOrDefault(x => x.Id == instance.Id);
                if (i != null && string.IsNullOrWhiteSpace(i.ThumbnailPath))
                {
                    i.PreviewError = "页面预览暂不可用";
                    OnPropertyChanged(string.Empty);
                }
            }
        }

        private void Load()
        {
            var state = StatePersistence.Load(ApplicationDirectory);
            if (state == null) return;
            Runtimes.Clear();
            foreach (var r in state.Runtimes ?? Enumerable.Empty<BrowserRuntime>())
                if (r != null) Runtimes.Add(r);
            Plugins.Clear();
            foreach (var p in state.Plugins ?? Enumerable.Empty<BrowserPlugin>())
                if (p != null) Plugins.Add(p);
            Instances.Clear();
            foreach (var i in state.Instances ?? Enumerable.Empty<BrowserInstance>())
                if (i != null) Instances.Add(i);
            Settings = state.Settings ?? new LaunchSettings();
            NormalizeLoadedState();
            Save();
        }

        private void Save()
        {
            if (_disposed) return;
            _stateDirty = true;
            PersistPendingState();
            OnPropertyChanged(string.Empty);
        }

        private void SaveDeferred()
        {
            if (_disposed) return;
            _stateDirty = true;
            _persistenceTimer.Stop();
            _persistenceTimer.Start();
            OnPropertyChanged(string.Empty);
        }

        private void PersistPendingState()
        {
            if (!_stateDirty) return;
            var state = new PersistedState
            {
                Runtimes = Runtimes.ToList(),
                Plugins = Plugins.ToList(),
                Instances = Instances.ToList(),
                Settings = Settings,
            };
            try
            {
                StatePersistence.Save(ApplicationDirectory, state);
                _stateDirty = false;
            }
            catch (Exception ex)
            {
                CrashGuard.Record("state-save", ex);
            }
        }

        private void NormalizeLoadedState()
        {
            Settings.HomeURL = Settings.HomeURL ?? "chrome://newtab";
            Settings.ProxyServer = Settings.ProxyServer ?? "";
            Settings.ProxyUsername = Settings.ProxyUsername ?? "";
            Settings.ProxyPassword = Settings.ProxyPassword ?? "";
            Settings.PresetProxyServer = Settings.PresetProxyServer ?? LaunchSettings.DefaultPresetProxyServer;
            Settings.PresetProxyHost = Settings.PresetProxyHost ?? "127.0.0.1";
            Settings.PresetProxyUsername = Settings.PresetProxyUsername ?? "";
            Settings.PresetProxyPassword = Settings.PresetProxyPassword ?? "";
            Settings.PresetProxyRemark = Settings.PresetProxyRemark ?? "";
            Settings.PresetProxyCheckTarget = Settings.PresetProxyCheckTarget ?? "";
            Settings.AdditionalFlags = Settings.AdditionalFlags ?? "";
            Settings.DockBadge = Settings.DockBadge ?? "";
            Settings.DefaultPluginIDs = Settings.DefaultPluginIDs ?? new List<Guid>();
            Settings.RecentProxyPresets = Settings.RecentProxyPresets ?? new List<ProxyPreset>();

            foreach (var runtime in Runtimes)
            {
                runtime.Name = runtime.Name ?? "";
                runtime.Version = runtime.Version ?? "";
                runtime.Architecture = runtime.Architecture ?? "";
                runtime.ExecutablePath = runtime.ExecutablePath ?? "";
            }
            foreach (var plugin in Plugins)
            {
                plugin.Name = plugin.Name ?? "";
                plugin.Version = plugin.Version ?? "";
                plugin.Path = plugin.Path ?? "";
            }
            foreach (var instance in Instances)
            {
                instance.Name = instance.Name ?? "浏览器实例";
                instance.RuntimeName = instance.RuntimeName ?? "浏览器";
                instance.ProfilePath = instance.ProfilePath ?? "";
                instance.StartURL = instance.StartURL ?? "chrome://newtab";
                instance.PluginIDs = instance.PluginIDs ?? new List<Guid>();
                if (instance.Status == InstanceStatus.Running && !IsExpectedInstanceProcess(instance))
                    instance.Status = InstanceStatus.Stopped;
            }
        }

        private void Report(Exception ex)
        {
            ErrorMessage = ex is YTrayException ye ? ((Exception)ye).Message : ex.Message;
            OnPropertyChanged(nameof(ErrorMessage));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public void Dispose()
        {
            if (_disposed) return;
            _timer?.Stop();
            _persistenceTimer?.Stop();
            PersistPendingState();
            _disposed = true;
            foreach (var launch in _launches.Values.ToList())
            {
                try { launch.Dispose(); } catch { }
            }
            _launches.Clear();
            foreach (var controller in _taskbarControllers.Values.ToList())
            {
                try { controller.Dispose(); } catch { }
            }
            _taskbarControllers.Clear();
            _thumbnailInFlight.Clear();
            _lastThumbnailAttempt.Clear();
            PropertyChanged = null;
        }
    }

    public static class InstanceThumbnailStorage
    {
        public static string ThumbnailURL(Guid instanceId, string applicationDirectory) =>
            Path.Combine(applicationDirectory, "Thumbnails", instanceId + ".jpg");

        public static void RemoveThumbnail(BrowserInstance instance, string applicationDirectory)
        {
            try { var p = ThumbnailURL(instance.Id, applicationDirectory); if (File.Exists(p)) File.Delete(p); } catch { }
        }
    }
}
