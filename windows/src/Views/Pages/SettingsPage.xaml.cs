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
        private readonly LaunchAtLoginManager _launchAtLogin;
        private bool _loadingControls;
        private int _feedbackGeneration;

        public SettingsPage(InstanceStore store, LaunchAtLoginManager? launchAtLogin = null)
        {
            InitializeComponent();
            _store = store;
            _launchAtLogin = launchAtLogin ?? new LaunchAtLoginManager();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            Load();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ThemeManager.ThemeChanged -= OnThemeChanged;
            ThemeManager.ThemeChanged += OnThemeChanged;
            _launchAtLogin.PropertyChanged -= OnLaunchAtLoginChanged;
            _launchAtLogin.PropertyChanged += OnLaunchAtLoginChanged;
            _launchAtLogin.Refresh();
            Load();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            ThemeManager.ThemeChanged -= OnThemeChanged;
            _launchAtLogin.PropertyChanged -= OnLaunchAtLoginChanged;
        }

        private void OnLaunchAtLoginChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(SyncStartupControls)); return; }
            SyncStartupControls();
        }

        private void OnThemeChanged(object? sender, EventArgs e)
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

        private void SyncStartupControls()
        {
            if (StartupSwitch == null) return;
            StartupSwitch.IsChecked = _launchAtLogin.IsEnabled;
            StartupStatusDetail.Text = string.IsNullOrWhiteSpace(_launchAtLogin.StatusDetail)
                ? "登录 Windows 后静默进入系统托盘，不自动拉起浏览器实例。"
                : _launchAtLogin.StatusDetail;
        }

        private void SyncEdgeDockControls()
        {
            if (EdgeDockSwitch == null) return;
            EdgeDockSwitch.IsChecked = _store.Settings.EdgeDockEnabled;
            var onLeft = _store.Settings.EdgeDockOnLeft;
            SetSegmentState(DockLeftButton, onLeft);
            SetSegmentState(DockRightButton, !onLeft);
        }

        private void SetSegmentState(Button button, bool selected)
        {
            button.Background = selected ? (Brush)FindResource("SurfaceRaisedBrush") : Brushes.Transparent;
            button.BorderBrush = selected ? (Brush)FindResource("HairlineBrush") : Brushes.Transparent;
            button.Foreground = (Brush)FindResource(selected ? "BrandOrangeBrush" : "TextSecondaryBrush");
        }

        private void Load()
        {
            _loadingControls = true;
            HomeUrlBox.Text = _store.Settings.HomeURL;
            DebugPortBox.Text = _store.Settings.DebugPort.ToString();
            AutoConnectYakitCheck.IsChecked = _store.Settings.AutoConnectYakit;
            WebRTCCheck.IsChecked = _store.Settings.RestrictWebRTC;
            NotificationsCheck.IsChecked = _store.Settings.DisableNotifications;
            CertCheck.IsChecked = _store.Settings.IgnoreCertificateErrors;
            TestTypeCheck.IsChecked = _store.Settings.UseTestType;
            ColorizeCheck.IsChecked = _store.Settings.ColorizeBrowserInstances;
            AutoUpdateSwitch.IsChecked = _store.Settings.CheckForAppUpdates;
            FlagsBox.Text = _store.Settings.AdditionalFlags;
            SyncStartupControls();
            SyncEdgeDockControls();
            UpdateThemeChoiceVisuals();
            _loadingControls = false;
            RefreshThemeDescription();
        }

        private void StartupSwitch_Click(object sender, RoutedEventArgs e)
        {
            if (_loadingControls) return;
            if (_launchAtLogin.IsEnabled)
            {
                var choice = MessageBox.Show(
                    "关闭后，登录 Windows 时 YTray 将不会自动进入系统托盘。",
                    "确认关闭开机启动？",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);
                if (choice != MessageBoxResult.Yes)
                {
                    SyncStartupControls();
                    return;
                }
            }
            if (!_launchAtLogin.SetEnabled(!_launchAtLogin.IsEnabled))
                ShowFeedback(_launchAtLogin.ErrorMessage ?? "开机启动设置失败", true);
            else
                ShowFeedback(_launchAtLogin.IsEnabled ? "已开启开机自动启动" : "已关闭开机自动启动");
            SyncStartupControls();
        }

        private void EdgeDockSwitch_Click(object sender, RoutedEventArgs e)
        {
            if (_loadingControls) return;
            var enabled = EdgeDockSwitch.IsChecked == true;
            _store.Settings.EdgeDockEnabled = enabled;
            _store.SaveSettings();
            ApplyLiveEdgeDock();
            ShowFeedback(enabled ? "已开启屏幕边缘悬浮条" : "已隐藏屏幕边缘悬浮条");
        }

        private void DockSide_Click(object sender, RoutedEventArgs e)
        {
            if (_loadingControls || !(sender is Button button)) return;
            _store.Settings.EdgeDockOnLeft = string.Equals(button.Tag?.ToString(), "Left", StringComparison.OrdinalIgnoreCase);
            _store.SaveSettings();
            SyncEdgeDockControls();
            ApplyLiveEdgeDock();
            ShowFeedback(_store.Settings.EdgeDockOnLeft ? "悬浮条已切换至屏幕左缘" : "悬浮条已切换至屏幕右缘");
        }

        private void ApplyLiveEdgeDock()
        {
            var dock = Application.Current?.Windows.OfType<EdgeDock>().FirstOrDefault();
            if (dock == null) return;
            if (_store.Settings.EdgeDockEnabled) dock.ShowDock(remember: false);
            else dock.HideDock(remember: false);
        }

        private void AutoUpdateSwitch_Click(object sender, RoutedEventArgs e)
        {
            if (_loadingControls) return;
            _store.Settings.CheckForAppUpdates = AutoUpdateSwitch.IsChecked == true;
            _store.SaveSettings();
            ShowFeedback(_store.Settings.CheckForAppUpdates ? "已开启自动检测更新" : "已关闭自动检测更新");
        }

        private void BehaviorSwitch_Click(object sender, RoutedEventArgs e)
        {
            if (_loadingControls) return;
            _store.SetAutoConnectYakit(AutoConnectYakitCheck.IsChecked == true);
            _store.Settings.RestrictWebRTC = WebRTCCheck.IsChecked == true;
            _store.Settings.DisableNotifications = NotificationsCheck.IsChecked == true;
            _store.Settings.IgnoreCertificateErrors = CertCheck.IsChecked == true;
            _store.Settings.UseTestType = TestTypeCheck.IsChecked == true;
            _store.Settings.ColorizeBrowserInstances = ColorizeCheck.IsChecked == true;
            _store.SaveSettings();
            ShowFeedback("偏好已自动保存");
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
                ? $"当前跟随 Windows（{(ThemeManager.IsDark ? "深色" : "浅色")}）"
                : $"当前固定为{(_store.Settings.ThemePreference == AppThemePreference.Dark ? "深色" : "浅色")}";
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
            _store.SetAutoConnectYakit(AutoConnectYakitCheck.IsChecked == true);
            _store.Settings.RestrictWebRTC = WebRTCCheck.IsChecked == true;
            _store.Settings.DisableNotifications = NotificationsCheck.IsChecked == true;
            _store.Settings.IgnoreCertificateErrors = CertCheck.IsChecked == true;
            _store.Settings.UseTestType = TestTypeCheck.IsChecked == true;
            _store.Settings.ColorizeBrowserInstances = ColorizeCheck.IsChecked == true;
            _store.Settings.AdditionalFlags = FlagsBox.Text;
            _store.SaveSettings();
            ShowFeedback("设置已保存");
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            var defaults = new LaunchSettings(_store.Settings.DefaultRuntimeID);
            HomeUrlBox.Text = defaults.HomeURL;
            DebugPortBox.Text = defaults.DebugPort.ToString();
            AutoConnectYakitCheck.IsChecked = defaults.AutoConnectYakit;
            WebRTCCheck.IsChecked = defaults.RestrictWebRTC;
            NotificationsCheck.IsChecked = defaults.DisableNotifications;
            CertCheck.IsChecked = defaults.IgnoreCertificateErrors;
            TestTypeCheck.IsChecked = defaults.UseTestType;
            ColorizeCheck.IsChecked = defaults.ColorizeBrowserInstances;
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
