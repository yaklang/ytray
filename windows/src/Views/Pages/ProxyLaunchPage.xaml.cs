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
            public string DisplayTitle { get; set; } = "";
            public string Address { get; set; } = "";
            public bool IsActive { get; set; }
            public Visibility AuthVisibility { get; set; }
        }

        private readonly InstanceStore _store;
        private ProxyScheme _selectedScheme = ProxyScheme.Http;
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

        private void OnStoreChanged(object? sender, PropertyChangedEventArgs e)
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
            _selectedScheme = _store.Settings.PresetProxyScheme;
            UpdateSchemeSegments();
            HostBox.Text = _store.Settings.PresetProxyHost;
            PortBox.Text = _store.Settings.PresetProxyPort.ToString();
            RemarkBox.Text = _store.Settings.PresetProxyRemark;
            CheckTargetBox.Text = _store.Settings.PresetProxyCheckTarget;
            UsernameBox.Text = _store.Settings.PresetProxyUsername;
            _syncingPassword = true;
            PasswordInput.Password = _store.Settings.PresetProxyPassword;
            PasswordRevealBox.Text = _store.Settings.PresetProxyPassword;
            _syncingPassword = false;
            Refresh();
        }

        private void SchemeSegment_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button)) return;
            _selectedScheme = string.Equals(button.Tag?.ToString(), "Https", StringComparison.OrdinalIgnoreCase)
                ? ProxyScheme.Https
                : ProxyScheme.Http;
            UpdateSchemeSegments();
            UpdateActiveBadge();
        }

        private void UpdateSchemeSegments()
        {
            if (HttpSchemeBtn == null) return;
            SetSegmentState(HttpSchemeBtn, _selectedScheme == ProxyScheme.Http);
            SetSegmentState(HttpsSchemeBtn, _selectedScheme == ProxyScheme.Https);
        }

        private void SetSegmentState(Button button, bool selected)
        {
            button.Background = selected ? (Brush)FindResource("SurfaceRaisedBrush") : Brushes.Transparent;
            button.BorderBrush = selected ? (Brush)FindResource("HairlineBrush") : Brushes.Transparent;
            button.Foreground = (Brush)FindResource(selected ? "BrandOrangeBrush" : "TextSecondaryBrush");
        }

        private void UpdateActiveBadge()
        {
            if (ActiveProxySummaryBadge == null) return;
            var scheme = _selectedScheme == ProxyScheme.Https ? "HTTPS" : "HTTP";
            var host = string.IsNullOrWhiteSpace(HostBox.Text) ? _store.Settings.PresetProxyHost : HostBox.Text.Trim();
            var port = string.IsNullOrWhiteSpace(PortBox.Text) ? _store.Settings.PresetProxyPort.ToString() : PortBox.Text.Trim();
            ActiveProxySummaryBadge.Text = $"{scheme} · {host}:{port}";
        }

        private void Refresh()
        {
            if (RuntimeCombo == null) return;
            UpdateSchemeSegments();
            UpdateActiveBadge();
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

            var currentServer = _store.Settings.PresetProxyServer ?? "";
            var recent = (_store.Settings.RecentProxyPresets ?? new List<ProxyPreset>())
                .Take(5)
                .Select(p => BuildProxyChoice(p, currentServer))
                .ToList();
            RecentProxyList.ItemsSource = recent;
            RecentProxyEmpty.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            PresetCountLabel.Text = $"{recent.Count} / 5";
            CheckButton.IsEnabled = _store.ProxyCheckPhase != ProxyCheckPhase.Checking;
            ProxyStatus.Text = string.IsNullOrWhiteSpace(_store.ProxyCheckMessage)
                ? "等待检测，点击右侧「检测连接」验证代理可用性"
                : _store.ProxyCheckMessage;
            var brushKey = _store.ProxyCheckPhase == ProxyCheckPhase.Failure ? "DangerBrush"
                : _store.ProxyCheckPhase == ProxyCheckPhase.Success ? "SuccessBrush" : "TextTertiaryBrush";
            ProxyDot.Fill = (Brush)FindResource(brushKey);
            ProxyStatus.Foreground = (Brush)FindResource(_store.ProxyCheckPhase == ProxyCheckPhase.Idle ? "TextSecondaryBrush" : brushKey);
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
                ShowSaveStatus("代理端口应为 1–65535", true);
                PortBox.Focus();
                return false;
            }
            _store.Settings.PresetProxyScheme = _selectedScheme;
            _store.Settings.PresetProxyHost = HostBox.Text.Trim();
            _store.Settings.PresetProxyPort = proxyPort;
            _store.Settings.PresetProxyRemark = RemarkBox.Text.Trim();
            _store.Settings.PresetProxyCheckTarget = CheckTargetBox.Text.Trim();
            _store.Settings.PresetProxyUsername = UsernameBox.Text.Trim();
            _store.Settings.PresetProxyPassword = PasswordInput.Password;
            if (RuntimeCombo.SelectedItem is RuntimeChoice choice) _store.Settings.DefaultRuntimeID = choice.Runtime.Id;
            UpdateActiveBadge();
            return true;
        }

        private void SaveProxy_Click(object sender, RoutedEventArgs e)
        {
            if (!ApplyControls()) return;
            var saved = _store.RememberPresetProxy();
            _store.SaveSettings();
            ShowSaveStatus(saved == null ? (_store.ErrorMessage ?? "代理保存失败") : "代理预设已保存", saved == null);
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

        private void SelectProxyRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!(((FrameworkElement)sender).Tag is ProxyPreset preset)) return;
            _store.SelectProxyPreset(preset);
            LoadControls();
            ShowSaveStatus("已切换至该代理预设");
        }

        private void LaunchPreset_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (!(((FrameworkElement)sender).Tag is ProxyPreset preset)) return;
            _store.SelectProxyPreset(preset);
            LoadControls();
            if (!_store.LaunchConfigured(true) && !_store.LaunchWasCancelled)
                ShowSaveStatus(_store.ErrorMessage ?? "无法启动浏览器，请检查当前配置。", true);
        }

        private void RemoveProxy_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (!(((FrameworkElement)sender).Tag is ProxyPreset preset)) return;
            _store.RemoveProxyPreset(preset);
            Refresh();
            ShowSaveStatus("已删除代理预设");
        }

        private void Direct_Click(object sender, RoutedEventArgs e)
        {
            if (RuntimeCombo.SelectedItem is RuntimeChoice choice) _store.SelectDefaultRuntime(choice.Runtime);
            if (!_store.LaunchConfigured(false) && !_store.LaunchWasCancelled)
                ShowSaveStatus(_store.ErrorMessage ?? "无法启动浏览器，请检查当前配置。", true);
        }

        private void Proxy_Click(object sender, RoutedEventArgs e)
        {
            if (ApplyControls() && !_store.LaunchConfigured(true) && !_store.LaunchWasCancelled)
                ShowSaveStatus(_store.ErrorMessage ?? "无法启动浏览器，请检查当前配置。", true);
        }

        private void Wizard_Click(object sender, RoutedEventArgs e)
        {
            ApplyControls();
            var wizard = new CustomLaunchWizard(_store) { Owner = Window.GetWindow(this) };
            wizard.ShowDialog();
        }

        private static ProxyChoice BuildProxyChoice(ProxyPreset preset, string currentServer)
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
            var hasAuth = !string.IsNullOrWhiteSpace(preset.Username) || !string.IsNullOrWhiteSpace(preset.Password);
            return new ProxyChoice
            {
                Preset = preset,
                SchemeTitle = schemeTitle,
                DisplayTitle = string.IsNullOrWhiteSpace(preset.Remark) ? address : preset.Remark,
                Address = address,
                IsActive = string.Equals(preset.Server, currentServer, StringComparison.OrdinalIgnoreCase),
                AuthVisibility = hasAuth ? Visibility.Visible : Visibility.Collapsed,
            };
        }

        private void ChoiceCombo_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is ComboBox combo) || !combo.IsEnabled) return;
            var source = e.OriginalSource as DependencyObject;
            if (source != null && ItemsControl.ContainerFromElement(combo, source) is ComboBoxItem)
                return;
            combo.Focus();
            combo.IsDropDownOpen = !combo.IsDropDownOpen;
            e.Handled = true;
        }

        private void TogglePassword_Click(object sender, RoutedEventArgs e)
        {
            var reveal = PasswordRevealBox.Visibility != Visibility.Visible;
            PasswordRevealBox.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;
            PasswordInput.Visibility = reveal ? Visibility.Collapsed : Visibility.Visible;
            PasswordEyeIcon.Data = Geometry.Parse(reveal
                ? "M 2 2 L 14 14 M 1 8 C 4.2 3.7 11.8 3.7 15 8 C 13.7 9.7 11.9 10.9 10 11.2 M 6 4.9 C 3.9 5.3 2.2 6.5 1 8"
                : "M 1 8 C 4.2 3.7 11.8 3.7 15 8 C 11.8 12.3 4.2 12.3 1 8 Z M 8 5.6 A 2.4 2.4 0 1 0 8 10.4 A 2.4 2.4 0 1 0 8 5.6");
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
    }
}
