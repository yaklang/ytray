using System;
using System.Windows;
using System.Windows.Controls;
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
            if (ThemeCombo == null) return;
            _loadingControls = true;
            ThemeCombo.SelectedIndex = (int)_store.Settings.ThemePreference;
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
            ThemeCombo.SelectedIndex = (int)_store.Settings.ThemePreference;
            _loadingControls = false;
            RefreshThemeDescription();
        }

        private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_store == null || _loadingControls || ThemeCombo.SelectedIndex < 0) return;
            var preference = (AppThemePreference)ThemeCombo.SelectedIndex;
            _store.SetThemePreference(preference);
            SaveStatus.Text = "✓ 主题已应用";
            RefreshThemeDescription();
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
    }
}
