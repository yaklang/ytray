#nullable enable
using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using YTray.Core;

namespace YTray.Views.Pages
{
    public partial class McpPage : Page
    {
        private readonly InstanceStore _store;
        private bool _refreshing;
        public McpPage(InstanceStore store)
        {
            InitializeComponent(); _store = store;
            EndpointText.Text = McpServer.Endpoint;
            ComputerEndpointText.Text = store.SystemControl.Endpoint;
            ConfigTextBox.Text = McpConfiguration.YTray;
            Loaded += (_, __) => { _store.PropertyChanged += Changed; _store.SystemControl.PropertyChanged += Changed; Refresh(); };
            Unloaded += (_, __) => { _store.PropertyChanged -= Changed; _store.SystemControl.PropertyChanged -= Changed; };
            Refresh();
        }
        private void Changed(object? sender, PropertyChangedEventArgs e)
        {
            if (Dispatcher.CheckAccess()) Refresh(); else Dispatcher.BeginInvoke(new Action(Refresh));
        }
        private void Refresh()
        {
            _refreshing = true;
            ServiceToggle.IsChecked = _store.IsMcpAvailable;
            AutoStartToggle.IsChecked = _store.IsMcpAutoStartEnabled;
            ComputerToggle.IsChecked = _store.SystemControl.IsEnabled;
            _refreshing = false;
            ServiceStatusDetail.Text = _store.IsMcpAvailable ? "已开启 · " + _store.McpStatusMessage : _store.McpStatusMessage;
            ComputerStatusText.Text = _store.SystemControl.StatusText + (_store.SystemControl.IsEnabled ? " · 保持开启即可随时使用" : " · 开启后可复制连接配置");
            CopyComputerButton.IsEnabled = CopyBothButton.IsEnabled = _store.SystemControl.ConfigurationJSON != null;
            ConnectionHint.Text = _store.SystemControl.IsEnabled ? "两个配置分别对应 ytray 和 ytray-system。电脑控制配置在总开关关闭后失效。" : "电脑控制尚未开启。可先复制 YTray 配置；开启电脑控制后，可复制包含两个服务的完整 JSON。";
        }
        private void ServiceToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_refreshing) return;
            if (ServiceToggle.IsChecked == true) _store.StartMcpService(); else _store.StopMcpService();
            Refresh();
        }
        private void AutoStartToggle_Changed(object sender, RoutedEventArgs e) { if (!_refreshing) _store.SetMcpAutoStart(AutoStartToggle.IsChecked == true); }
        private async void ComputerToggle_Click(object sender, RoutedEventArgs e)
        {
            ComputerToggle.IsEnabled = false;
            try { await _store.SetComputerControlEnabledAsync(ComputerToggle.IsChecked == true); }
            catch (Exception error) { CopyFeedback.Text = error.Message; }
            finally { ComputerToggle.IsEnabled = true; Refresh(); }
        }
        private async Task CopyAsync(Func<string?> readConfiguration, string message)
        {
            if (await McpClipboard.TryCopyAsync(this, readConfiguration) is bool copied)
                CopyFeedback.Text = copied ? message : McpClipboard.FailureMessage;
        }
        private async void CopyConfig_Click(object sender, RoutedEventArgs e) => await CopyAsync(() => McpConfiguration.YTray, "YTray MCP 配置已复制。");
        private async void CopyEndpoint_Click(object sender, RoutedEventArgs e) => await CopyAsync(() => McpServer.Endpoint, "YTray MCP 地址已复制。");
        private async void CopyComputer_Click(object sender, RoutedEventArgs e) => await CopyAsync(() => _store.SystemControl.ConfigurationJSON, "电脑控制 MCP 配置已复制。");
        private async void CopyBoth_Click(object sender, RoutedEventArgs e) => await CopyAsync(() => McpConfiguration.Both(_store.SystemControl.ConfigurationJSON), "两个 MCP 的配置已复制，粘贴一次即可添加两个服务。");
        private void ComputerTools_Click(object sender, RoutedEventArgs e) => new SystemMcpWindow(_store.SystemControl) { Owner = Window.GetWindow(this) }.Show();
        private void ComputerSettings_Click(object sender, RoutedEventArgs e)
        {
            var settings = new SystemControlSettingsWindow(_store); settings.ShowFor(Window.GetWindow(this)!);
        }
    }
}
