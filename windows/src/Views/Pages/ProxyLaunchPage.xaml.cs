#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using YTray.Core;
using YTray.Models;

namespace YTray.Views.Pages
{
    public partial class ProxyLaunchPage : Page
    {
        private sealed class RuntimeChoice
        {
            public BrowserRuntime Runtime { get; set; } = null!;
            public ImageSource? IconSource { get; set; }
        }

        private sealed class ProxyChoice
        {
            public ProxyPreset Preset { get; set; } = null!;
            public string SchemeTitle { get; set; } = "HTTP";
            public string Address { get; set; } = "";
        }

        private readonly InstanceStore _store;
        private bool _subscribed;
        private bool _refreshScheduled;
        private bool _syncingPassword;
        private int _saveFeedbackGeneration;

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
            _syncingPassword = true;
            PasswordInput.Password = _store.Settings.PresetProxyPassword;
            PasswordRevealBox.Text = _store.Settings.PresetProxyPassword;
            _syncingPassword = false;
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
            var selectedRuntimeId = (RuntimeCombo.SelectedItem as RuntimeChoice)?.Runtime.Id
                ?? _store.DefaultRuntime?.Id;
            var runtimes = _store.Runtimes
                .OrderByDescending(r => r.Id == _store.Settings.DefaultRuntimeID)
                .ThenBy(r => r.DisplayTitle, StringComparer.OrdinalIgnoreCase)
                .Select(runtime => new RuntimeChoice
                {
                    Runtime = runtime,
                    IconSource = BrowserIconSource.FromExecutable(runtime.ExecutablePath)
                })
                .ToList();
            RuntimeCombo.ItemsSource = runtimes;
            RuntimeCombo.SelectedItem = runtimes.FirstOrDefault(item => item.Runtime.Id == selectedRuntimeId)
                ?? runtimes.FirstOrDefault();

            var recent = (_store.Settings.RecentProxyPresets ?? new List<ProxyPreset>())
                .Take(5)
                .Select(BuildProxyChoice)
                .ToList();
            RecentProxyList.ItemsSource = recent;
            RecentProxyEmpty.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            CheckButton.IsEnabled = _store.ProxyCheckPhase != ProxyCheckPhase.Checking;
            ProxyStatus.Text = string.IsNullOrWhiteSpace(_store.ProxyCheckMessage) ? "等待检测" : _store.ProxyCheckMessage;
            var brushKey = _store.ProxyCheckPhase == ProxyCheckPhase.Failure ? "DangerBrush"
                : _store.ProxyCheckPhase == ProxyCheckPhase.Success ? "SuccessBrush" : "TextTertiaryBrush";
            ProxyDot.Fill = (Brush)FindResource(brushKey);
            ProxyStatus.Foreground = (Brush)FindResource(brushKey);
        }

        private bool ApplyControls()
        {
            if (string.IsNullOrWhiteSpace(HostBox.Text))
            {
                ShowSaveStatus("请输入代理主机", true);
                HostBox.Focus();
                return false;
            }
            if (!int.TryParse(PortBox.Text, out var proxyPort) || proxyPort < 1 || proxyPort > 65535)
            {
                ShowSaveStatus("代理端口无效", true);
                PortBox.Focus();
                return false;
            }
            if (!int.TryParse(DebugPortBox.Text, out var debugPort) || debugPort < 1 || debugPort > 65535)
            {
                ShowSaveStatus("调试端口无效", true);
                DebugPortBox.Focus();
                return false;
            }
            _store.Settings.PresetProxyScheme = SchemeCombo.SelectedIndex == 1 ? ProxyScheme.Https : ProxyScheme.Http;
            _store.Settings.PresetProxyHost = HostBox.Text.Trim();
            _store.Settings.PresetProxyPort = proxyPort;
            _store.Settings.PresetProxyUsername = UsernameBox.Text.Trim();
            _store.Settings.PresetProxyPassword = PasswordInput.Password;
            _store.Settings.HomeURL = HomeUrlBox.Text.Trim();
            _store.Settings.DebugPort = debugPort;
            _store.Settings.RestrictWebRTC = WebRtcCheck.IsChecked == true;
            _store.Settings.DisableNotifications = NotificationsCheck.IsChecked == true;
            _store.Settings.IgnoreCertificateErrors = CertCheck.IsChecked == true;
            _store.Settings.AdditionalFlags = FlagsBox.Text;
            if (RuntimeCombo.SelectedItem is RuntimeChoice choice) _store.Settings.DefaultRuntimeID = choice.Runtime.Id;
            return true;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (!ApplyControls()) return;
            _store.RememberPresetProxy();
            _store.SaveSettings();
            ShowSaveStatus("设置已保存");
            Refresh();
        }

        private void SaveProxy_Click(object sender, RoutedEventArgs e)
        {
            if (!ApplyControls()) return;
            var saved = _store.RememberPresetProxy();
            ShowSaveStatus(saved == null ? (_store.ErrorMessage ?? "代理保存失败") : "代理已保存", saved == null);
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
                ProxyDot.Fill = (Brush)FindResource("DangerBrush");
                ProxyStatus.Foreground = (Brush)FindResource("DangerBrush");
            }
        }

        private void SelectProxy_Click(object sender, RoutedEventArgs e)
        {
            if (!(((FrameworkElement)sender).Tag is ProxyPreset preset)) return;
            _store.SelectProxyPreset(preset);
            LoadControls();
            ShowSaveStatus("已载入该代理");
        }

        private void SelectProxyRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!(((FrameworkElement)sender).Tag is ProxyPreset preset)) return;
            _store.SelectProxyPreset(preset);
            LoadControls();
            ShowSaveStatus("已载入该代理");
        }

        private void RemoveProxy_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (!(((FrameworkElement)sender).Tag is ProxyPreset preset)) return;
            _store.RemoveProxyPreset(preset);
            Refresh();
            ShowSaveStatus("已删除最近代理");
        }

        private void Direct_Click(object sender, RoutedEventArgs e) { if (ApplyControls()) _store.LaunchConfigured(false); }
        private void Proxy_Click(object sender, RoutedEventArgs e) { if (ApplyControls()) _store.LaunchConfigured(true); }
        private void Wizard_Click(object sender, RoutedEventArgs e)
        {
            ApplyControls();
            var wizard = new CustomLaunchWizard(_store) { Owner = Window.GetWindow(this) };
            wizard.ShowDialog();
        }

        private static ProxyChoice BuildProxyChoice(ProxyPreset preset)
        {
            var schemeTitle = "HTTP";
            var address = preset.Server ?? "";
            try
            {
                var endpoint = HTTPProxyAddress.Split(address);
                schemeTitle = endpoint.Scheme == ProxyScheme.Https ? "HTTPS" : "HTTP";
                address = endpoint.Host + ":" + endpoint.Port;
            }
            catch
            {
                if (address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) address = address.Substring(8);
                else if (address.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) address = address.Substring(7);
            }
            return new ProxyChoice { Preset = preset, SchemeTitle = schemeTitle, Address = address };
        }

        private async void ShowSaveStatus(string message, bool isError = false)
        {
            var generation = ++_saveFeedbackGeneration;
            SaveStatus.Text = message;
            SaveStatus.Foreground = (Brush)FindResource(isError ? "DangerBrush" : "SuccessBrush");
            SaveStatusBorder.Background = (Brush)FindResource(isError ? "DangerPaleBrush" : "SuccessPaleBrush");
            SaveStatusBorder.Visibility = Visibility.Visible;
            await System.Threading.Tasks.Task.Delay(2400);
            if (generation == _saveFeedbackGeneration && IsLoaded)
                SaveStatusBorder.Visibility = Visibility.Collapsed;
        }

        private void PasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_syncingPassword) return;
            _syncingPassword = true;
            PasswordRevealBox.Text = PasswordInput.Password;
            _syncingPassword = false;
        }

        private void PasswordRevealBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_syncingPassword) return;
            _syncingPassword = true;
            PasswordInput.Password = PasswordRevealBox.Text;
            _syncingPassword = false;
        }

        private void TogglePassword_Click(object sender, RoutedEventArgs e)
        {
            var reveal = PasswordRevealBox.Visibility != Visibility.Visible;
            PasswordRevealBox.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;
            PasswordInput.Visibility = reveal ? Visibility.Collapsed : Visibility.Visible;
            PasswordEyeIcon.Data = Geometry.Parse(reveal
                ? "M 1 8 C 4.2 3.7 11.8 3.7 15 8 C 11.8 12.3 4.2 12.3 1 8 Z M 2 2 L 14 14"
                : "M 1 8 C 4.2 3.7 11.8 3.7 15 8 C 11.8 12.3 4.2 12.3 1 8 Z M 8 5.6 A 2.4 2.4 0 1 0 8 10.4 A 2.4 2.4 0 1 0 8 5.6");
            if (reveal)
            {
                PasswordRevealBox.Focus();
                PasswordRevealBox.CaretIndex = PasswordRevealBox.Text.Length;
            }
            else
            {
                PasswordInput.Focus();
            }
        }

        private void ChoiceCombo_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is ComboBox combo) || !combo.IsEnabled) return;
            var source = e.OriginalSource as DependencyObject;
            if (source != null && ItemsControl.ContainerFromElement(combo, source) is ComboBoxItem) return;
            var shouldOpen = !combo.IsDropDownOpen;
            combo.Focus();
            e.Handled = true;
            Dispatcher.BeginInvoke(new Action(() => combo.IsDropDownOpen = shouldOpen), DispatcherPriority.Input);
        }

    }
}
