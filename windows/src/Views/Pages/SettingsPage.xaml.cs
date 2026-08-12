#nullable enable
using System;
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
        private bool _loadingControls;

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
            SyncThemeSelection();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) =>
            ThemeManager.ThemeChanged -= OnThemeChanged;

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
            SaveStatus.Text = "✓ 主题已应用";
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
                ? $"当前跟随 Windows，正在使用{(ThemeManager.IsDark ? "深色" : "浅色")}外观。"
                : $"当前固定使用{(_store.Settings.ThemePreference == AppThemePreference.Dark ? "深色" : "浅色")}外观。";
        }

        private void Save_Click(object s, RoutedEventArgs e)
        {
            _store.Settings.HomeURL = HomeUrlBox.Text;
            if (int.TryParse(DebugPortBox.Text, out int port)) _store.Settings.DebugPort = port;
            _store.Settings.RestrictWebRTC = WebRTCCheck.IsChecked == true;
            _store.Settings.DisableNotifications = NotificationsCheck.IsChecked == true;
            _store.Settings.IgnoreCertificateErrors = CertCheck.IsChecked == true;
            _store.Settings.AdditionalFlags = FlagsBox.Text;
            _store.SaveSettings();
            SaveStatus.Text = "✓ 设置已保存";
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
            SaveStatus.Text = "已恢复默认值，点击保存生效";
        }

        private void RestoreFlags_Click(object sender, RoutedEventArgs e)
        {
            FlagsBox.Text = "--disable-features=Translate" + Environment.NewLine
                + "--disable-background-networking";
            SaveStatus.Text = "已填入推荐参数";
        }
    }
}
