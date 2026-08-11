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
        private readonly Dictionary<Guid, Process> _processes = new Dictionary<Guid, Process>();
        private readonly HashSet<Guid> _thumbnailInFlight = new HashSet<Guid>();
        private Dictionary<Guid, DateTime> _lastThumbnailAttempt = new Dictionary<Guid, DateTime>();
        private bool _disposed;
        private static readonly TimeSpan ThumbnailRefreshInterval = TimeSpan.FromSeconds(12);
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
        public string ErrorMessage { get; private set; }
        public BrowserLaunchPhase LaunchPhase { get; private set; } = BrowserLaunchPhase.Idle;
        public string LaunchMessage { get; private set; } = "";
        public LaunchMode? LaunchingMode { get; private set; }
        public bool? LaunchingUsesProxy { get; private set; }
        public Guid? LaunchingInstanceID { get; private set; }
        public Guid? RestoringInstanceID { get; private set; }
        public ProxyCheckPhase ProxyCheckPhase { get; private set; } = ProxyCheckPhase.Idle;
        public string ProxyCheckMessage { get; private set; } = "";
        public ProxyCheckReport ProxyCheckReport { get; private set; }
        public bool IsProxyAdvancedExpanded { get; set; }
        public Guid? LaunchToken { get; private set; }

        public bool IsLaunching => LaunchPhase == BrowserLaunchPhase.Preparing || LaunchPhase == BrowserLaunchPhase.Waiting;

        public List<BrowserInstance> RunningInstances => Instances.Where(i => i.Status == InstanceStatus.Running).ToList();
        public List<BrowserInstance> HistoryInstances => Instances.Where(i => i.Status != InstanceStatus.Running).ToList();
        public List<BrowserRuntime> SystemRuntimes => Runtimes.Where(r => r.IsSystemEnvironment).OrderBy(r => r.DisplayTitle).ToList();
        public List<BrowserRuntime> ManagedRuntimes => Runtimes.Where(r => r.Source == RuntimeSource.Managed).OrderByDescending(r => r.CreatedAt).ToList();
        public List<BrowserRuntime> LocalBrowserRuntimes => Runtimes.Where(r => r.Source != RuntimeSource.Managed).OrderBy(r => r.DisplayTitle).ToList();

        public BrowserRuntime DefaultRuntime =>
            Runtimes.FirstOrDefault(r => r.Id == Settings.DefaultRuntimeID)
            ?? SystemRuntimes.FirstOrDefault()
            ?? Runtimes.FirstOrDefault();

        public InstanceStore(string applicationDirectory = null, bool discoverSystemBrowsers = true)
        {
            ApplicationDirectory = applicationDirectory ?? StatePersistence.DefaultApplicationDirectory;
            try { Directory.CreateDirectory(ApplicationDirectory); } catch { }

            // Legacy migration (InstanceDock -> YTray), best-effort.
            MigrateLegacyDirectoryIfNeeded();

            Load();
            if (discoverSystemBrowsers) RefreshSystemBrowsers();
            RefreshProcessStates();

            _timer = new DispatcherTimer(DispatcherPriority.Background);
            _timer.Interval = TimeSpan.FromSeconds(2);
            _timer.Tick += (s, e) => { RefreshProcessStates(); _ = RefreshRunningPageTitlesAsync(); ScheduleAutomaticThumbnailRefresh(); };
            _timer.Start();
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

        public BrowserRuntime AddLocalRuntime(string selectedPath)
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
                var manifestPath = Path.Combine(directory, "manifest.json");
                var json = File.ReadAllText(manifestPath);
                var manifest = Newtonsoft.Json.JsonConvert.DeserializeObject<PluginManifest>(json);
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
            var idx = Plugins.ToList().FindIndex(p => p.Id == plugin.Id);
            if (idx >= 0) Plugins[idx] = plugin;
            Save();
        }

        public void RemovePlugin(BrowserPlugin plugin)
        {
            var p = Plugins.FirstOrDefault(x => x.Id == plugin.Id);
            if (p != null) Plugins.Remove(p);
            Settings.DefaultPluginIDs.RemoveAll(g => g == plugin.Id);
            Save();
        }

        public void SelectDefaultRuntime(BrowserRuntime runtime)
        {
            if (!Runtimes.Any(r => r.Id == runtime.Id)) return;
            Settings.DefaultRuntimeID = runtime.Id;
            Save();
        }

        public BrowserRuntime RuntimeFor(BrowserInstance instance) =>
            Runtimes.FirstOrDefault(r => r.Id == instance.RuntimeID);

        public void Stop(BrowserInstance instance) => _ = ArchiveAndStopAsync(instance);

        public void RemoveHistory(BrowserInstance instance)
        {
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

        public void RenameHistory(BrowserInstance instance, string name)
        {
            var i = Instances.FirstOrDefault(x => x.Id == instance.Id);
            if (i == null || i.Status == InstanceStatus.Running) return;
            var v = name?.Trim();
            if (string.IsNullOrEmpty(v)) return;
            i.Name = v;
            Save();
        }

        public async Task CaptureAsync(BrowserInstance instance)
        {
            if (instance.Status != InstanceStatus.Running) return;
            try
            {
                var pictures = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "YTray");
                Directory.CreateDirectory(pictures);
                var output = await ScreenshotService.CaptureAsync(instance.DebugPort, instance.Id, pictures);
                var i = Instances.FirstOrDefault(x => x.Id == instance.Id);
                if (i != null) i.LastScreenshotPath = output;
                Save();
            }
            catch (Exception ex)
            {
                Report(ex);
            }
        }

        public void RevealProfile(BrowserInstance instance)
        {
            try
            {
                System.Diagnostics.Process.Start(new ProcessStartInfo("explorer.exe", instance.ProfilePath) { UseShellExecute = true });
            }
            catch { }
        }

        public void Focus(BrowserInstance instance)
        {
            try
            {
                var p = System.Diagnostics.Process.GetProcessById(instance.ProcessID);
                var hwnd = YTray.Native.WindowEnum.FindFirstVisibleWindow(p.Id);
                if (hwnd != IntPtr.Zero)
                {
                    YTray.Native.Win32.ShowWindow(hwnd, YTray.Native.Win32.SW_RESTORE);
                    YTray.Native.Win32.SetForegroundWindow(hwnd);
                }
            }
            catch { }
        }

        public void SaveSettings() => Save();

        public void Launch(LaunchMode mode, LaunchSettings customSettings = null, List<Guid> customPluginIDs = null,
            BrowserInstance restoring = null, bool? launchUsesProxy = null)
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

            var configuration = customSettings ?? Settings;
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
                _processes[result.Instance.Id] = result.Process;
                result.Process.EnableRaisingEvents = true;
                result.Process.Exited += (s, e) => System.Windows.Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                {
                    var instanceID = result.Instance.Id;
                    MarkStopped(instanceID);
                }));

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

                _ = WaitForBrowserAsync(result.Instance, token);
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

        public LaunchSettings QuickLaunchConfiguration(bool usePresetProxy)
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
            if (instance.Status == InstanceStatus.Running) return;
            var cfg = instance.SettingsSnapshot ?? new LaunchSettings(instance.RuntimeID)
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
                var stillRunning = instance.ProcessID > 0 && ProcessAlive(instance.ProcessID);
                var detail = stillRunning ? "浏览器进程已经创建，但调试端口未在 15 秒内就绪" : "浏览器进程在完成启动前退出";
                FinishLaunchFailure(new YTrayException(YTrayError.LaunchFailed, detail), token);
                return;
            }

            // Resolve the real AUMID from the live Chrome window and persist into instance metadata.
            try
            {
                var aumid = await AumidResolver.ResolveAsync(instance.ProcessID, instance.ProfilePath,
                    instance.RuntimeKind ?? BrowserKind.Chrome);
                var i = Instances.FirstOrDefault(x => x.Id == instance.Id);
                if (i != null)
                {
                    i.AppUserModelId = aumid;
                    Save();
                }
            }
            catch { /* AUMID resolution is best-effort */ }

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
                    await ScreenshotService.NavigateAsync(instance.DebugPort, navigationTarget);
                }
                catch (Exception ex)
                {
                    FinishLaunchFailure(ex, token);
                    return;
                }
            }

            LaunchMessage = $"{instance.RuntimeName} 已启动";
            LaunchPhase = BrowserLaunchPhase.Succeeded;
            LaunchingMode = null;
            LaunchingUsesProxy = null;
            LaunchingInstanceID = null;
            RestoringInstanceID = null;
            ScheduleAutomaticThumbnailRefresh(instance.Id);
            await Task.Delay(1200);
            if (LaunchToken != token || LaunchPhase != BrowserLaunchPhase.Succeeded) return;
            LaunchMessage = "";
            LaunchPhase = BrowserLaunchPhase.Idle;
            LaunchToken = null;
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

        public string RememberPresetProxy()
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
                return;
            }
            ProxyCheckPhase = ProxyCheckPhase.Checking;
            ProxyCheckMessage = "检测中 · 最多 10 秒";
            ProxyCheckReport = null;
            var username = (Settings.PresetProxyUsername ?? "").Trim();
            var password = Settings.PresetProxyPassword;
            var report = await ProxyConnectivityChecker.CheckDefaultTargetsAsync(endpoint, username, password, Settings.PresetProxyCheckTarget, ProxyConnectivityChecker.DefaultTimeout);
            ProxyCheckReport = report;
            ProxyCheckPhase = report.IsSuccess ? ProxyCheckPhase.Success : ProxyCheckPhase.Failure;
            ProxyCheckMessage = report.Message;
        }

        public void SelectProxyPreset(ProxyPreset preset)
        {
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
            var used = new HashSet<string>(RunningInstances.Where(i => !string.IsNullOrEmpty(i.DockBadge)).Select(i => i.DockBadge));
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
        }

        public async Task InstallAsync(MirrorVersion version)
        {
            IsInstalling = true;
            ActivityMessage = $"正在下载并校验 {version.Version}…";
            try
            {
                var rt = await RuntimeInstaller.InstallAsync(version, ApplicationDirectory);
                Upsert(rt);
            }
            catch (Exception ex) { Report(ex); }
            finally { IsInstalling = false; ActivityMessage = ""; }
        }

        private void RefreshProcessStates()
        {
            var changed = false;
            foreach (var i in Instances.ToList())
            {
                if (i.Status != InstanceStatus.Running) continue;
                if (i.ProcessID <= 0 || !ProcessAlive(i.ProcessID))
                {
                    i.Status = InstanceStatus.Stopped;
                    BrowserProcessIcon.Remove(i.Id, ApplicationDirectory);
                    ProxyAuthenticationExtension.Remove(i.Id, ApplicationDirectory);
                    changed = true;
                }
            }
            if (changed) { ConsolidateHistoryBadges(); Save(); }
        }

        private static bool ProcessAlive(int pid)
        {
            try
            {
                var p = System.Diagnostics.Process.GetProcessById(pid);
                return !p.HasExited;
            }
            catch { return false; }
        }

        private void MarkStopped(Guid id)
        {
            _processes.Remove(id);
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

        private void ConsolidateHistoryBadges()
        {
            var history = HistoryInstances;
            var newestByBadge = new Dictionary<string, BrowserInstance>();
            foreach (var i in history)
            {
                if (string.IsNullOrEmpty(i.DockBadge)) continue;
                if (newestByBadge.TryGetValue(i.DockBadge, out var cur) && cur.StartedAt >= i.StartedAt) continue;
                newestByBadge[i.DockBadge] = i;
            }
            var removed = history.Where(i => !string.IsNullOrEmpty(i.DockBadge)
                && newestByBadge.TryGetValue(i.DockBadge, out var n) && n.Id != i.Id).ToList();
            foreach (var i in removed)
            {
                Instances.Remove(i);
                BrowserProcessIcon.Remove(i.Id, ApplicationDirectory);
                InstanceThumbnailStorage.RemoveThumbnail(i, ApplicationDirectory);
            }
        }

        private async Task ArchiveAndStopAsync(BrowserInstance instance)
        {
            await CaptureAndStoreThumbnailAsync(instance);
            await RefreshPageTitleAsync(instance);
            if (_processes.TryGetValue(instance.Id, out var p) && !p.HasExited) { try { p.Kill(); } catch { } }
            else if (instance.ProcessID > 0 && ProcessAlive(instance.ProcessID))
            {
                try { System.Diagnostics.Process.GetProcessById(instance.ProcessID).Kill(); } catch { }
            }
        }

        private async Task RefreshRunningPageTitlesAsync()
        {
            var targets = RunningInstances.Select(i => (i.Id, i.DebugPort)).ToList();
            if (targets.Count == 0) return;
            var updates = new List<(Guid, ScreenshotService.PageState)>();
            foreach (var (id, port) in targets)
            {
                var state = await ScreenshotService.CurrentPageStateAsync(port);
                if (state.HasValue) updates.Add((id, state.Value));
            }
            var changed = false;
            foreach (var (id, state) in updates)
            {
                var i = Instances.FirstOrDefault(x => x.Id == id);
                if (i == null) continue;
                if (!string.IsNullOrEmpty(state.Title) && i.LastPageTitle != state.Title) { i.LastPageTitle = state.Title; changed = true; }
                if (!string.IsNullOrEmpty(state.URL) && i.LastPageURL != state.URL) { i.LastPageURL = state.URL; changed = true; }
            }
            if (changed) Save();
        }

        private async Task RefreshPageTitleAsync(BrowserInstance instance)
        {
            var state = await ScreenshotService.CurrentPageStateAsync(instance.DebugPort, 5);
            if (!state.HasValue) return;
            var s = state.Value;
            var i = Instances.FirstOrDefault(x => x.Id == instance.Id);
            if (i == null) return;
            if (!string.IsNullOrEmpty(s.Title)) i.LastPageTitle = s.Title;
            if (!string.IsNullOrEmpty(s.URL)) i.LastPageURL = s.URL;
            Save();
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
                _ = CaptureAndStoreThumbnailAsync(instance).ContinueWith(_ => _thumbnailInFlight.Remove(instance.Id));
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
                if (i != null)
                {
                    i.ThumbnailPath = captured;
                    i.ThumbnailUpdatedAt = DateTime.Now;
                    Save();
                }
            }
            catch { }
        }

        private void Load()
        {
            var state = StatePersistence.Load(ApplicationDirectory);
            if (state == null) return;
            Runtimes.Clear();
            foreach (var r in state.Runtimes) Runtimes.Add(r);
            Plugins.Clear();
            foreach (var p in state.Plugins) Plugins.Add(p);
            Instances.Clear();
            foreach (var i in state.Instances) Instances.Add(i);
            Settings = state.Settings;
            Save();
        }

        private void Save()
        {
            var state = new PersistedState
            {
                Runtimes = Runtimes.ToList(),
                Plugins = Plugins.ToList(),
                Instances = Instances.ToList(),
                Settings = Settings,
            };
            try { StatePersistence.Save(ApplicationDirectory, state); } catch { }
        }

        private void Report(Exception ex)
        {
            ErrorMessage = ex is YTrayException ye ? ((Exception)ye).Message : ex.Message;
            OnPropertyChanged(nameof(ErrorMessage));
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Stop();
            foreach (var p in _processes.Values)
            {
                try { if (!p.HasExited) p.Dispose(); } catch { }
            }
            _processes.Clear();
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