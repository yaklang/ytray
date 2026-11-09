#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using YTray.Core;
using YTray.Models;
using YTray.Views.Pages;

namespace YTray.Views
{
    public partial class ManagerView : Window
    {
        private readonly InstanceStore _store;
        private readonly LaunchAtLoginManager _launchAtLogin;
        private readonly AppUpdateService _updater = AppUpdateService.Shared;
        private readonly Dictionary<string, Page> _pageCache = new Dictionary<string, Page>();
        private bool _loaded;
        private bool _sidebarRefreshScheduled;
        private bool _updateRefreshScheduled;
        private string? _currentPageTag;

        public ManagerView(InstanceStore store, LaunchAtLoginManager launchAtLogin)
        {
            InitializeComponent();
            _store = store;
            _launchAtLogin = launchAtLogin;
            Loaded += OnLoaded;
            Closed += OnClosed;
            StateChanged += (s, e) =>
                MaximizeIcon.Data = Geometry.Parse(WindowState == WindowState.Maximized
                    ? "M 2.5 0.5 L 9.5 0.5 L 9.5 7.5 M 0.5 2.5 L 7.5 2.5 L 7.5 9.5 L 0.5 9.5 Z"
                    : "M 0.5 0.5 L 9.5 0.5 L 9.5 9.5 L 0.5 9.5 Z");
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!_loaded)
            {
                _loaded = true;
                NavQuick.IsSelected = true;
                ShowPage("overview");
                _store.PropertyChanged += OnStorePropertyChanged;
                _updater.PropertyChanged += OnUpdaterPropertyChanged;
                ThemeManager.ThemeChanged += OnThemeChanged;
            }
            RefreshSidebarStatus();
            RefreshUpdateControls();
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            _store.PropertyChanged -= OnStorePropertyChanged;
            _updater.PropertyChanged -= OnUpdaterPropertyChanged;
            ThemeManager.ThemeChanged -= OnThemeChanged;
            ContentFrame.Content = null;
            _pageCache.Clear();
        }

        private void OnThemeChanged(object? sender, EventArgs e) => RefreshStatusBar();

        private void OnStorePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!IsLoaded) return;
            if (_sidebarRefreshScheduled) return;
            _sidebarRefreshScheduled = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _sidebarRefreshScheduled = false;
                if (IsLoaded)
                {
                    RefreshStatusBar();
                    RefreshUpdateControls();
                }
            }), DispatcherPriority.Background);
        }

        private void RefreshSidebarStatus() => RefreshStatusBar();

        private void OnUpdaterPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_updateRefreshScheduled || Dispatcher.HasShutdownStarted) return;
            _updateRefreshScheduled = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _updateRefreshScheduled = false;
                if (IsLoaded) RefreshUpdateControls();
            }), DispatcherPriority.Background);
        }

        private void RefreshUpdateControls()
        {
            if (VersionButton == null) return;
            var appAvailable = _updater.IsUpdateAvailable;
            var extensionAvailable = _store.IsExtensionUpdateAvailable;
            var available = appAvailable || extensionAvailable;
            var version = appAvailable ? _updater.AvailableVersion! : _updater.CurrentVersion;
            VersionButtonText.Text = "v" + version;
            VersionButton.Tag = available ? "Available" : "Current";
            VersionUpdateDot.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
            VersionButton.ToolTip = appAvailable
                ? $"发现 YTray 新版本 v{version}，当前版本 v{_updater.CurrentVersion}"
                : extensionAvailable ? "Yakit Browser Agent 有可用更新"
                : $"YTray v{_updater.CurrentVersion} · 查看版本与更新";
            AutomationProperties.SetName(VersionButton, available
                ? "YTray 有可用更新"
                : $"YTray 当前版本 v{_updater.CurrentVersion}");

            UpdatePopupTitle.Text = "YTray v" + version;
            UpdatePopupBadge.Visibility = appAvailable ? Visibility.Visible : Visibility.Collapsed;
            UpdatePopupStatus.Text = _updater.StatusText;
            UpdatePopupStatus.SetResourceReference(ForegroundProperty,
                _updater.Phase == AppUpdatePhase.Failed ? "DangerBrush" : "TextSecondaryBrush");
            UpdatePopupNotes.Text = !string.IsNullOrWhiteSpace(_updater.ReleaseNotesText)
                ? _updater.ReleaseNotesText
                : _updater.IsBusy ? "正在获取版本说明…" : "检查更新后可查看当前版本说明。";
            UpdatePopupLastCheck.Text = _updater.LastCheck.HasValue
                ? "上次检查：" + _updater.LastCheck.Value.ToString("g") : "尚未检查更新";
            UpdateReleaseNotesButton.Visibility = string.IsNullOrWhiteSpace(_updater.ReleaseNotesUrl)
                ? Visibility.Collapsed : Visibility.Visible;
            UpdateDownloadButton.IsEnabled = _updater.Enabled;
            UpdateActionButton.Content = appAvailable ? "立即更新" : _updater.ActionLabel;
            UpdateActionButton.IsEnabled = _updater.Enabled && !_updater.IsBusy;
            UpdateAutoCheck.IsChecked = _store.Settings.CheckForAppUpdates;

            var installed = _store.ManagedExtension;
            var latest = _store.ExtensionManifest?.Versions.FirstOrDefault(candidate =>
                ExtensionInstaller.EnterpriseArtifact(candidate) != null);
            var hasBundled = ExtensionInstaller.TryGetBundledVersion(out var bundledVersion);
            var targetVersion = latest?.Version ?? (hasBundled ? bundledVersion : installed?.Version ?? "");
            ExtensionPopupTitle.Text = string.IsNullOrWhiteSpace(targetVersion)
                ? "Yakit Browser Agent"
                : "Yakit Browser Agent v" + targetVersion;
            ExtensionPopupBadge.Visibility = extensionAvailable ? Visibility.Visible : Visibility.Collapsed;
            ExtensionPopupVersion.Text = installed == null
                ? "当前未安装"
                : "当前版本: v" + installed.Version;

            if (latest != null)
            {
                var notes = (latest.Notes ?? new List<string>())
                    .Where(note => !string.IsNullOrWhiteSpace(note)).ToList();
                if (notes.Count > 0)
                    ExtensionPopupNotes.Text = string.Join("\n", notes.Select((note, index) => $"{index + 1}. {note.Trim()}"));
                else
                {
                    var published = DateTime.TryParse(latest.PublishedAt, out var date)
                        ? date.ToLocalTime().ToString("yyyy/M/d HH:mm") : latest.PublishedAt;
                    var commit = string.IsNullOrWhiteSpace(latest.Commit) ? "-"
                        : latest.Commit.Substring(0, Math.Min(10, latest.Commit.Length));
                    ExtensionPopupNotes.Text = $"发布时间：{published}\n构建：{commit}";
                }
            }
            else if (hasBundled)
                ExtensionPopupNotes.Text = $"当前安装包内置 Yakit Browser Agent v{bundledVersion}。";
            else
                ExtensionPopupNotes.Text = _store.ExtensionManifest == null
                    ? "打开更新通知后会同时检查浏览器插件版本。"
                    : "当前更新清单中没有可用的企业版扩展包。";

            var extensionBusy = _store.IsInstallingExtension;
            if (extensionBusy) ExtensionActionButton.Content = $"更新中 {_store.ExtensionInstallPercent}%";
            else if (_store.ExtensionManifest == null && installed != null) ExtensionActionButton.Content = "检查更新";
            else if (installed == null) ExtensionActionButton.Content = latest != null ? "立即下载" : "安装内置版";
            else if (extensionAvailable) ExtensionActionButton.Content = "立即更新";
            else ExtensionActionButton.Content = "已是最新";
            ExtensionActionButton.IsEnabled = !extensionBusy && (
                _store.ExtensionManifest == null || installed == null && hasBundled || extensionAvailable);
            ExtensionPopupProgress.Visibility = extensionBusy ? Visibility.Visible : Visibility.Collapsed;
            ExtensionPopupProgress.Value = _store.ExtensionInstallPercent;
            ExtensionPopupStatus.Text = !string.IsNullOrWhiteSpace(_store.ExtensionStatusMessage)
                ? _store.ExtensionStatusMessage
                : _store.ExtensionManifest == null ? "尚未检查更新"
                : "更新清单：" + _store.ExtensionManifest.UpdatedAt;
        }

        private void RefreshStatusBar()
        {
            var count = _store.RunningInstances.Count;
            StatusEnvironment.Text = count == 0 ? "运行环境正常" : $"{count} 个实例运行中";
            RunningDot.Opacity = count == 0 ? 0.35 : 1;
            var runtime = _store.DefaultRuntime;
            StatusRuntime.Text = runtime == null
                ? "暂无默认浏览器"
                : $"{runtime.DisplayTitle} {runtime.VersionLabel}";
            StatusBrowserCount.Text = $"可用浏览器: {_store.Runtimes.Count}";
            StatusPluginCount.Text = $"插件: {_store.Plugins.Count} 个";
            var proxy = _store.Settings.PresetProxyServer;
            if (string.IsNullOrWhiteSpace(proxy)) proxy = LaunchSettings.DefaultPresetProxyServer;
            StatusProxy.Text = "HTTP 代理 " + proxy.Replace("http://", "").Replace("https://", "");
            StatusDebug.Text = "调试基址 127.0.0.1";
            StatusLogsButton.ToolTip = "打开诊断日志\n" + DiagnosticLog.MainLogPath;
            RefreshThemeControls();
        }

        private void RefreshThemeControls()
        {
            if (ThemeButtonIcon == null) return;
            var preference = _store.Settings.ThemePreference;
            ThemeButtonIcon.Data = (Geometry)FindResource(preference == AppThemePreference.System
                ? "ThemeSystemIcon"
                : preference == AppThemePreference.Dark ? "ThemeDarkIcon" : "ThemeLightIcon");
            ThemeButton.ToolTip = preference == AppThemePreference.System
                ? $"外观主题 · 跟随系统（{(ThemeManager.IsDark ? "深色" : "浅色")}）"
                : $"外观主题 · {(preference == AppThemePreference.Dark ? "深色" : "浅色")}";

            SystemThemeCheck.Visibility = preference == AppThemePreference.System ? Visibility.Visible : Visibility.Hidden;
            LightThemeCheck.Visibility = preference == AppThemePreference.Light ? Visibility.Visible : Visibility.Hidden;
            DarkThemeCheck.Visibility = preference == AppThemePreference.Dark ? Visibility.Visible : Visibility.Hidden;
            ApplyThemeChoiceBackground(SystemThemeButton, preference == AppThemePreference.System);
            ApplyThemeChoiceBackground(LightThemeButton, preference == AppThemePreference.Light);
            ApplyThemeChoiceBackground(DarkThemeButton, preference == AppThemePreference.Dark);
        }

        private static void ApplyThemeChoiceBackground(Button button, bool selected)
        {
            button.SetResourceReference(BackgroundProperty, selected ? "BrandPaleBrush" : "SurfaceRaisedBrush");
        }

        private void ThemeButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshThemeControls();
            ThemePopup.IsOpen = !ThemePopup.IsOpen;
        }

        private void ThemeChoice_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button) || !(button.Tag is string raw)
                || !Enum.TryParse(raw, out AppThemePreference preference)) return;
            _store.SetThemePreference(preference);
            ThemePopup.IsOpen = false;
            RefreshSidebarStatus();
        }

        private async void VersionButton_Click(object sender, RoutedEventArgs e)
        {
            UpdatePopup.IsOpen = !UpdatePopup.IsOpen;
            if (!UpdatePopup.IsOpen) return;
            RefreshUpdateControls();
            await System.Threading.Tasks.Task.WhenAll(
                _updater.CheckAsync(), _store.RefreshExtensionManifestAsync());
            RefreshUpdateControls();
        }

        private async void UpdateAction_Click(object sender, RoutedEventArgs e)
        {
            if (_updater.IsBusy) return;
            if (_updater.IsUpdateAvailable)
            {
                UpdatePopup.IsOpen = false;
                _updater.InstallUpdate();
            }
            else await _updater.CheckAsync();
        }

        private void DownloadUpdate_Click(object sender, RoutedEventArgs e) => _updater.OpenDownloads();

        private void OpenReleaseNotes_Click(object sender, RoutedEventArgs e) => _updater.OpenReleaseNotes();

        private void UpdateAutoCheck_Click(object sender, RoutedEventArgs e)
        {
            _store.Settings.CheckForAppUpdates = UpdateAutoCheck.IsChecked == true;
            _store.SaveSettings();
        }

        private async void ExtensionAction_Click(object sender, RoutedEventArgs e)
        {
            if (_store.IsInstallingExtension) return;
            if (_store.ExtensionManifest == null)
            {
                await _store.RefreshExtensionManifestAsync();
                if (_store.ManagedExtension != null && !_store.IsExtensionUpdateAvailable) return;
            }
            await _store.InstallExtensionAsync();
        }

        private void Nav_Selected(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            if (sender is ListBoxItem item && item.Tag is string tag) ShowPage(tag);
        }

        private void ShowPage(string tag)
        {
            tag = string.IsNullOrWhiteSpace(tag) ? "overview" : tag;
            if (_currentPageTag == tag && ContentFrame.Content != null) return;
            if (!_pageCache.TryGetValue(tag, out var page))
            {
                switch (tag)
                {
                    case "runtimes": page = new RuntimePage(_store); break;
                    case "settings": page = new SettingsPage(_store); break;
                    case "instances": page = new InstancesPage(_store); break;
                    case "launch": page = new ProxyLaunchPage(_store); break;
                    case "plugins": page = new PluginsPage(_store); break;
                    case "mcp": page = new McpPage(_store); break;
                    case "system": page = new SystemControlPage(_store); break;
                    case "startup": page = new StartupPage(_launchAtLogin); break;
                    default: page = new QuickLaunchPage(_store); tag = "overview"; break;
                }
                _pageCache[tag] = page;
            }
            _currentPageTag = tag;
            ContentFrame.Content = page;
        }

        public void NavigateTo(string tag)
        {
            if (string.Equals(tag, "settings", StringComparison.OrdinalIgnoreCase))
                NavSettings.IsSelected = true;
            else if (string.Equals(tag, "plugins", StringComparison.OrdinalIgnoreCase))
                NavPlugins.IsSelected = true;
            else if (string.Equals(tag, "mcp", StringComparison.OrdinalIgnoreCase))
                NavMcp.IsSelected = true;
            else if (string.Equals(tag, "system", StringComparison.OrdinalIgnoreCase))
                NavSystem.IsSelected = true;
            else if (string.Equals(tag, "runtimes", StringComparison.OrdinalIgnoreCase))
                NavRuntimes.IsSelected = true;
            ShowPage(tag);
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void Maximize_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void StatusSettings_Click(object sender, RoutedEventArgs e)
        {
            NavSettings.IsSelected = true;
            ShowPage("settings");
        }

        private void StatusLogs_Click(object sender, RoutedEventArgs e)
        {
            if (DiagnosticLog.TryOpen(out var openedPath, out var error))
            {
                StatusLogsButton.ToolTip = "已打开\n" + openedPath;
                return;
            }
            MessageBox.Show(
                error ?? "无法打开诊断日志。",
                "YTray 诊断日志",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
