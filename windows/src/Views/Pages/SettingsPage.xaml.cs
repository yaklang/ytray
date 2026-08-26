#nullable enable
using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using YTray.Core;
using YTray.Models;

namespace YTray.Views.Pages
{
    public partial class SettingsPage : Page
    {
        private readonly InstanceStore _store;
        private readonly AppUpdateService _updater = AppUpdateService.Shared;
        private bool _loadingControls;
        private int _feedbackGeneration;

        public SettingsPage(InstanceStore store)
        {
            InitializeComponent();
            _store = store;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            Load();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ThemeManager.ThemeChanged -= OnThemeChanged;
            ThemeManager.ThemeChanged += OnThemeChanged;
            _updater.PropertyChanged -= OnUpdaterPropertyChanged;
            _updater.PropertyChanged += OnUpdaterPropertyChanged;
            SyncThemeSelection();
            RefreshUpdateControls();
            if (_updater.Phase == AppUpdatePhase.Idle && !IsCaptureProcess())
                CrashGuard.Observe(_updater.CheckAsync(), "check-app-update-settings");
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            ThemeManager.ThemeChanged -= OnThemeChanged;
            _updater.PropertyChanged -= OnUpdaterPropertyChanged;
        }

        private void OnThemeChanged(object sender, EventArgs e)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(SyncThemeSelection)); return; }
            SyncThemeSelection();
        }

        private void SyncThemeSelection()
        {
            if (SystemThemeChoice == null) return;
            _loadingControls = true;
            UpdateThemeChoiceVisuals();
            _loadingControls = false;
            RefreshThemeDescription();
        }

        private void Load()
        {
            HomeUrlBox.Text = _store.Settings.HomeURL;
            DebugPortBox.Text = _store.Settings.DebugPort.ToString();
            WebRTCCheck.IsChecked = _store.Settings.RestrictWebRTC;
            NotificationsCheck.IsChecked = _store.Settings.DisableNotifications;
            CertCheck.IsChecked = _store.Settings.IgnoreCertificateErrors;
            FlagsBox.Text = _store.Settings.AdditionalFlags;
            _loadingControls = true;
            UpdateThemeChoiceVisuals();
            _loadingControls = false;
            RefreshThemeDescription();
        }

        private void ThemeChoice_Click(object sender, RoutedEventArgs e)
        {
            if (_store == null || _loadingControls || !(sender is Button button)
                || !Enum.TryParse(button.Tag?.ToString(), out AppThemePreference preference)) return;
            _store.SetThemePreference(preference);
            ShowFeedback("主题已应用");
            UpdateThemeChoiceVisuals();
            RefreshThemeDescription();
        }

        private void UpdateThemeChoiceVisuals()
        {
            if (SystemThemeChoice == null) return;
            SetThemeChoiceState(SystemThemeChoice, SystemThemeCheck,
                _store.Settings.ThemePreference == AppThemePreference.System);
            SetThemeChoiceState(LightThemeChoice, LightThemeCheck,
                _store.Settings.ThemePreference == AppThemePreference.Light);
            SetThemeChoiceState(DarkThemeChoice, DarkThemeCheck,
                _store.Settings.ThemePreference == AppThemePreference.Dark);
        }

        private void SetThemeChoiceState(Button button, FrameworkElement check, bool selected)
        {
            button.Background = (Brush)FindResource(selected ? "BrandPaleBrush" : "InputBrush");
            button.BorderBrush = (Brush)FindResource(selected ? "BrandOrangeBrush" : "HairlineBrush");
            button.Foreground = (Brush)FindResource(selected ? "BrandOrangeBrush" : "TextSecondaryBrush");
            check.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RefreshThemeDescription()
        {
            if (ThemePreviewLabel == null) return;
            ThemePreviewLabel.Text = _store.Settings.ThemePreference == AppThemePreference.System
                ? $"更改后立即应用 · 当前跟随 Windows（{(ThemeManager.IsDark ? "深色" : "浅色")}）"
                : $"更改后立即应用 · 当前固定为{(_store.Settings.ThemePreference == AppThemePreference.Dark ? "深色" : "浅色")}";
        }

        private void Save_Click(object s, RoutedEventArgs e)
        {
            var homeUrl = HomeUrlBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(homeUrl))
            {
                ShowFeedback("请输入起始地址", true);
                HomeUrlBox.Focus();
                return;
            }
            if (!int.TryParse(DebugPortBox.Text, out int port) || port < 1024 || port > 65535)
            {
                ShowFeedback("调试端口应为 1024–65535", true);
                DebugPortBox.Focus();
                return;
            }
            _store.Settings.HomeURL = homeUrl;
            _store.Settings.DebugPort = port;
            _store.Settings.RestrictWebRTC = WebRTCCheck.IsChecked == true;
            _store.Settings.DisableNotifications = NotificationsCheck.IsChecked == true;
            _store.Settings.IgnoreCertificateErrors = CertCheck.IsChecked == true;
            _store.Settings.AdditionalFlags = FlagsBox.Text;
            _store.SaveSettings();
            ShowFeedback("设置已保存");
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            var defaults = new LaunchSettings(_store.Settings.DefaultRuntimeID);
            HomeUrlBox.Text = defaults.HomeURL;
            DebugPortBox.Text = defaults.DebugPort.ToString();
            WebRTCCheck.IsChecked = defaults.RestrictWebRTC;
            NotificationsCheck.IsChecked = defaults.DisableNotifications;
            CertCheck.IsChecked = defaults.IgnoreCertificateErrors;
            FlagsBox.Text = defaults.AdditionalFlags;
            _store.SetThemePreference(AppThemePreference.System);
            ShowFeedback("已恢复默认值，点击保存生效");
        }

        private void RestoreFlags_Click(object sender, RoutedEventArgs e)
        {
            FlagsBox.Text = "--disable-features=Translate" + Environment.NewLine
                + "--disable-background-networking";
            ShowFeedback("已填入推荐参数");
        }

        private void OnUpdaterPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!IsLoaded) return;
            if (Dispatcher.CheckAccess()) RefreshUpdateControls();
            else Dispatcher.BeginInvoke(new Action(RefreshUpdateControls));
        }

        private void RefreshUpdateControls()
        {
            if (UpdateButton == null) return;
            CurrentVersionText.Text = $"当前 v{_updater.CurrentVersion}";
            UpdateStatusText.Text = _updater.StatusText;
            UpdateStatusText.Foreground = (Brush)FindResource(
                _updater.Phase == AppUpdatePhase.Failed ? "DangerBrush"
                : _updater.IsUpdateAvailable ? "BrandOrangeBrush"
                : _updater.Phase == AppUpdatePhase.UpToDate ? "SuccessBrush"
                : "TextSecondaryBrush");
            UpdateButton.Content = _updater.ActionLabel;
            UpdateButton.IsEnabled = !_updater.IsBusy;
            UpdateProgress.Visibility = _updater.Phase == AppUpdatePhase.Downloading
                ? Visibility.Visible : Visibility.Collapsed;
            UpdateProgress.Value = _updater.DownloadPercent;
        }

        private async void Update_Click(object sender, RoutedEventArgs e)
        {
            if (_updater.IsBusy) return;
            if (!_updater.IsUpdateAvailable && !_updater.IsDownloaded)
            {
                await _updater.CheckAsync();
                return;
            }

            var version = _updater.AvailableVersion ?? "最新版";
            var choice = MessageBox.Show(
                $"YTray v{version} 将在应用内下载并校验。校验通过后会请求管理员权限完成安装，然后自动重新启动。\n\n运行中的浏览器不会被关闭。",
                "安装 YTray 更新？",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information,
                MessageBoxResult.Yes);
            if (choice != MessageBoxResult.Yes) return;

            if (!_updater.IsDownloaded && !await _updater.DownloadAsync()) return;
            if (_updater.StartInstaller()) Application.Current.Shutdown();
        }

        private static bool IsCaptureProcess() => Environment.GetCommandLineArgs().Any(argument =>
            argument.StartsWith("--capture-", StringComparison.OrdinalIgnoreCase));

        private async void ShowFeedback(string message, bool isError = false)
        {
            var generation = ++_feedbackGeneration;
            SaveStatus.Text = message;
            SaveStatus.Foreground = (Brush)FindResource(isError ? "DangerBrush" : "SuccessBrush");
            SaveStatusBorder.Background = (Brush)FindResource(isError ? "DangerPaleBrush" : "SuccessPaleBrush");
            SaveStatusBorder.Visibility = Visibility.Visible;
            await System.Threading.Tasks.Task.Delay(2400);
            if (generation == _feedbackGeneration && IsLoaded)
                SaveStatusBorder.Visibility = Visibility.Collapsed;
        }
    }
}
