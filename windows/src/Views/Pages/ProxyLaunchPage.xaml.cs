#nullable enable
using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using YTray.Core;
using YTray.Models;

namespace YTray.Views.Pages
{
    public partial class ProxyLaunchPage : Page
    {
        private readonly InstanceStore _store;
        private bool _subscribed;
        private bool _refreshScheduled;

        public ProxyLaunchPage(InstanceStore store)
        {
            InitializeComponent();
            _store = store;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            LoadControls();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!_subscribed) { _store.PropertyChanged += OnStoreChanged; _subscribed = true; }
            Refresh();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (!_subscribed) return;
            _store.PropertyChanged -= OnStoreChanged;
            _subscribed = false;
        }

        private void OnStoreChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_refreshScheduled || !IsLoaded) return;
            _refreshScheduled = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _refreshScheduled = false;
                if (IsLoaded) Refresh();
            }), DispatcherPriority.Background);
        }

        private void LoadControls()
        {
            SchemeCombo.SelectedIndex = (int)_store.Settings.PresetProxyScheme;
            HostBox.Text = _store.Settings.PresetProxyHost;
            PortBox.Text = _store.Settings.PresetProxyPort.ToString();
            UsernameBox.Text = _store.Settings.PresetProxyUsername;
            PasswordBox.Text = _store.Settings.PresetProxyPassword;
            HomeUrlBox.Text = _store.Settings.HomeURL;
            DebugPortBox.Text = _store.Settings.DebugPort.ToString();
            WebRtcCheck.IsChecked = _store.Settings.RestrictWebRTC;
            NotificationsCheck.IsChecked = _store.Settings.DisableNotifications;
            CertCheck.IsChecked = _store.Settings.IgnoreCertificateErrors;
            FlagsBox.Text = _store.Settings.AdditionalFlags;
            Refresh();
        }

        private void Refresh()
        {
            if (RuntimeCombo == null) return;
            var selection = RuntimeCombo.SelectedItem as BrowserRuntime ?? _store.DefaultRuntime;
            RuntimeCombo.ItemsSource = _store.Runtimes.OrderByDescending(r => r.Id == _store.Settings.DefaultRuntimeID).ToList();
            RuntimeCombo.SelectedItem = selection != null
                ? _store.Runtimes.FirstOrDefault(r => r.Id == selection.Id)
                : _store.DefaultRuntime;
            RecentProxyList.ItemsSource = (_store.Settings.RecentProxyPresets ?? new System.Collections.Generic.List<ProxyPreset>()).Take(5).ToList();
            CheckButton.IsEnabled = _store.ProxyCheckPhase != ProxyCheckPhase.Checking;
            ProxyStatus.Text = string.IsNullOrWhiteSpace(_store.ProxyCheckMessage) ? "等待检测" : _store.ProxyCheckMessage;
            var brushKey = _store.ProxyCheckPhase == ProxyCheckPhase.Failure ? "DangerBrush"
                : _store.ProxyCheckPhase == ProxyCheckPhase.Success ? "SuccessBrush" : "TextTertiaryBrush";
            ProxyDot.Fill = (Brush)FindResource(brushKey);
            ProxyStatus.Foreground = (Brush)FindResource(brushKey);
        }

        private bool ApplyControls()
        {
            if (!int.TryParse(PortBox.Text, out var proxyPort) || proxyPort < 1 || proxyPort > 65535)
            {
                SaveStatus.Text = "代理端口无效";
                return false;
            }
            if (!int.TryParse(DebugPortBox.Text, out var debugPort) || debugPort < 1 || debugPort > 65535)
            {
                SaveStatus.Text = "调试端口无效";
                return false;
            }
            _store.Settings.PresetProxyScheme = SchemeCombo.SelectedIndex == 1 ? ProxyScheme.Https : ProxyScheme.Http;
            _store.Settings.PresetProxyHost = HostBox.Text.Trim();
            _store.Settings.PresetProxyPort = proxyPort;
            _store.Settings.PresetProxyUsername = UsernameBox.Text.Trim();
            _store.Settings.PresetProxyPassword = PasswordBox.Text;
            _store.Settings.HomeURL = HomeUrlBox.Text.Trim();
            _store.Settings.DebugPort = debugPort;
            _store.Settings.RestrictWebRTC = WebRtcCheck.IsChecked == true;
            _store.Settings.DisableNotifications = NotificationsCheck.IsChecked == true;
            _store.Settings.IgnoreCertificateErrors = CertCheck.IsChecked == true;
            _store.Settings.AdditionalFlags = FlagsBox.Text;
            if (RuntimeCombo.SelectedItem is BrowserRuntime runtime) _store.Settings.DefaultRuntimeID = runtime.Id;
            return true;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (!ApplyControls()) return;
            _store.RememberPresetProxy();
            _store.SaveSettings();
            SaveStatus.Text = "设置已保存";
            Refresh();
        }

        private void SaveProxy_Click(object sender, RoutedEventArgs e)
        {
            if (!ApplyControls()) return;
            var saved = _store.RememberPresetProxy();
            SaveStatus.Text = saved == null ? (_store.ErrorMessage ?? "代理保存失败") : "代理已保存";
            Refresh();
        }

        private async void Check_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!ApplyControls()) return;
                await _store.CheckPresetProxyAsync();
                Refresh();
            }
            catch (Exception ex)
            {
                CrashGuard.Record("proxy-launch-check", ex);
                ProxyStatus.Text = "检测失败 · " + ex.Message;
            }
        }

        private void SelectProxy_Click(object sender, RoutedEventArgs e)
        {
            if (!(((FrameworkElement)sender).Tag is ProxyPreset preset)) return;
            _store.SelectProxyPreset(preset);
            LoadControls();
        }

        private void Direct_Click(object sender, RoutedEventArgs e) { if (ApplyControls()) _store.LaunchConfigured(false); }
        private void Proxy_Click(object sender, RoutedEventArgs e) { if (ApplyControls()) _store.LaunchConfigured(true); }
        private void Wizard_Click(object sender, RoutedEventArgs e)
        {
            ApplyControls();
            var wizard = new CustomLaunchWizard(_store) { Owner = Window.GetWindow(this) };
            wizard.ShowDialog();
        }
    }
}
