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
        private int _feedbackGeneration;

        public McpPage(InstanceStore store)
        {
            InitializeComponent();
            _store = store;
            EndpointText.Text = McpServer.Endpoint;
            ComputerEndpointText.Text = store.SystemControl.Endpoint;
            BrowserEndpointText.Text = store.BrowserUseEndpoint;
            ConfigTextBox.Text = McpConfiguration.YTray;
            Loaded += (_, __) => { _store.PropertyChanged += Changed; _store.SystemControl.PropertyChanged += Changed; Refresh(); };
            Unloaded += (_, __) => { _store.PropertyChanged -= Changed; _store.SystemControl.PropertyChanged -= Changed; };
            Refresh();
        }

        private void Changed(object? sender, PropertyChangedEventArgs e)
        {
            if (Dispatcher.CheckAccess()) Refresh();
            else Dispatcher.BeginInvoke(new Action(Refresh));
        }

        private void Refresh()
        {
            _refreshing = true;
            var ytrayOn = _store.IsMcpAvailable;
            var computerOn = _store.SystemControl.IsEnabled;
            var browserOn = _store.BrowserUseConfiguration != null;
            ServiceToggle.IsChecked = ytrayOn;
            AutoStartToggle.IsChecked = _store.IsMcpAutoStartEnabled;
            ComputerToggle.IsChecked = computerOn;
            BrowserToggle.IsChecked = browserOn;
            _refreshing = false;

            var activeCount = (ytrayOn ? 1 : 0) + (computerOn ? 1 : 0) + (browserOn ? 1 : 0);
            ActiveSummaryText.Text = $"{activeCount} / 3 项服务运行中";
            ActiveSummaryDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, activeCount > 0 ? "SuccessBrush" : "TextTertiaryBrush");

            ServiceStateBadgeText.Text = ytrayOn ? "运行中" : "未开启";
            ServiceStateBadge.SetResourceReference(Border.BackgroundProperty, ytrayOn ? "SuccessPaleBrush" : "SurfaceMutedBrush");
            ServiceStateBadgeText.SetResourceReference(TextBlock.ForegroundProperty, ytrayOn ? "SuccessBrush" : "TextSecondaryBrush");

            ServiceStatusDetail.Text = ytrayOn ? "已就绪 · " + _store.McpStatusMessage : _store.McpStatusMessage;
            ComputerStatusText.Text = _store.SystemControl.StatusText + (computerOn ? " · 保持开启即可随时响应" : " · 开启后可复制带凭据的连接配置");
            CopyComputerButton.IsEnabled = CopyBothButton.IsEnabled = _store.SystemControl.ConfigurationJSON != null;
            CopyBrowserButton.IsEnabled = browserOn;
            BrowserStatusText.Text = browserOn
                ? "已开启 · YTray 托管浏览器实例自动接入"
                : "首次开启后复制配置，后续随 YTray 自动启动";
            ConnectionHint.Text = "安全提示：电脑控制与浏览器控制启用本机回环鉴权，导入客户端配置时请完整保留 Authorization 请求头。";
        }

        private async void ShowFeedback(string message)
        {
            var gen = ++_feedbackGeneration;
            CopyFeedback.Text = message;
            CopyFeedbackBorder.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
            await Task.Delay(2800);
            if (gen == _feedbackGeneration && IsLoaded)
                CopyFeedbackBorder.Visibility = Visibility.Collapsed;
        }

        private void ServiceToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_refreshing) return;
            if (ServiceToggle.IsChecked == true) _store.StartMcpService();
            else _store.StopMcpService();
            Refresh();
        }

        private void AutoStartToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (!_refreshing) _store.SetMcpAutoStart(AutoStartToggle.IsChecked == true);
        }

        private async void ComputerToggle_Click(object sender, RoutedEventArgs e)
        {
            ComputerToggle.IsEnabled = false;
            try { await _store.SetComputerControlEnabledAsync(ComputerToggle.IsChecked == true); }
            catch (Exception error) { ShowFeedback(error.Message); }
            finally { ComputerToggle.IsEnabled = true; Refresh(); }
        }

        private async Task CopyAsync(Func<string?> readConfiguration, string message)
        {
            if (await McpClipboard.TryCopyAsync(this, readConfiguration) is bool copied)
                ShowFeedback(copied ? message : McpClipboard.FailureMessage);
        }

        private async void CopyConfig_Click(object sender, RoutedEventArgs e) => await CopyAsync(() => McpConfiguration.YTray, "✓ YTray MCP 配置已复制");
        private async void CopyEndpoint_Click(object sender, RoutedEventArgs e) => await CopyAsync(() => McpServer.Endpoint, "✓ YTray MCP 地址已复制");
        private async void CopyComputer_Click(object sender, RoutedEventArgs e) => await CopyAsync(() => _store.SystemControl.ConfigurationJSON, "✓ 电脑控制 MCP 配置已复制");
        private async void CopyBrowser_Click(object sender, RoutedEventArgs e) => await CopyAsync(() => _store.BrowserUseConfiguration, "✓ 浏览器控制 MCP 配置已复制");

        private void BrowserToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_refreshing) return;
            try { _store.SetBrowserUseEnabled(BrowserToggle.IsChecked == true); }
            catch (Exception error) { ShowFeedback(error.Message); }
            finally { Refresh(); }
        }

        private async void CopyBoth_Click(object sender, RoutedEventArgs e) =>
            await CopyAsync(() => McpConfiguration.Both(_store.SystemControl.ConfigurationJSON), "✓ 已复制组合配置，粘贴一次即可接入两个服务");

        private void ComputerTools_Click(object sender, RoutedEventArgs e) =>
            new SystemMcpWindow(_store.SystemControl) { Owner = Window.GetWindow(this) }.Show();

        private void ComputerSettings_Click(object sender, RoutedEventArgs e)
        {
            if (Window.GetWindow(this) is ManagerView manager)
            {
                manager.NavigateTo("system");
                return;
            }
            var settings = new SystemControlSettingsWindow(_store);
            settings.ShowFor(Window.GetWindow(this)!);
        }
    }
}
