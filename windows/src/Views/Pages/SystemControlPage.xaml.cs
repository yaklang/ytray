#nullable enable
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using YTray.Core;
using YTray.Core.SystemControl;

namespace YTray.Views.Pages
{
    public partial class SystemControlPage : Page
    {
        private readonly SystemControlController _controller;
        private readonly InstanceStore _store;
        private bool _repairing;
        private string _error = "";
        public SystemControlPage(InstanceStore store)
        {
            InitializeComponent(); _store = store; _controller = store.SystemControl;
            Loaded += (_, __) => { _controller.PropertyChanged += Changed; Refresh(); };
            Unloaded += (_, __) => _controller.PropertyChanged -= Changed;
            EndpointText.Text = _controller.Endpoint;
            Refresh();
        }
        private void Changed(object? sender, PropertyChangedEventArgs e) => Refresh();
        private void Refresh()
        {
            StatusText.Text = _controller.StatusText;
            StateDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, _controller.IsEnabled ? "SuccessBrush" : "TextTertiaryBrush");
            StatusDetailText.Text = _controller.IsEnabled ? "客户端连接时需保留配置中的 Authorization 请求头。重启和解锁后配置继续有效。" : "开启后默认允许全部应用和操作，直到你主动关闭。";
            ToggleControlButton.Content = _controller.IsEnabled ? "关闭电脑控制" : "授权开启";
            ToggleControlButton.SetResourceReference(Control.BackgroundProperty, _controller.IsEnabled ? "SurfaceMutedBrush" : "BrandOrangeBrush");
            if (_controller.IsEnabled) ToggleControlButton.SetResourceReference(Control.ForegroundProperty, "DangerBrush"); else ToggleControlButton.Foreground = System.Windows.Media.Brushes.White;
            ToggleControlButton.IsEnabled = !_repairing;
            CopyConfigButton.IsEnabled = _controller.ConfigurationJSON != null;
            PreviewPipelineButton.Content = _controller.IsPipelinePreviewVisible ? "关闭顶部卡片" : "预览顶部卡片";
            RepairButton.IsEnabled = !_repairing;
            PermissionText.Text = _controller.IsElevated ? "管理员权限已就绪。遇到异常时可重新检查并修复控制组件。" : "管理员窗口无法操作时使用。Windows 会请求 UAC 确认，并以管理员身份重启 YTray。";
            ErrorText.Text = _error.Length == 0 ? _controller.LastError : _error;
            ErrorText.Visibility = ErrorText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        private async void Toggle_Click(object sender, RoutedEventArgs e)
        {
            _error = ""; FeedbackText.Visibility = Visibility.Collapsed;
            try { await _store.SetComputerControlEnabledAsync(!_controller.IsEnabled); }
            catch (Exception error) { _error = error.Message; }
            Refresh();
        }
        private async void CopyConfig_Click(object sender, RoutedEventArgs e)
        {
            if (await McpClipboard.TryCopyAsync(this, () => _controller.ConfigurationJSON) is not bool copied) return;
            _error = copied ? "" : McpClipboard.FailureMessage;
            FeedbackText.Text = "MCP 配置已复制，导入时请保留 Authorization 请求头。";
            FeedbackText.Visibility = copied ? Visibility.Visible : Visibility.Collapsed;
            Refresh();
        }
        private void Tools_Click(object sender, RoutedEventArgs e) => new SystemMcpWindow(_controller) { Owner = Window.GetWindow(this) }.Show();
        private void InspectApps_Click(object sender, RoutedEventArgs e) => ControlInspectorWindow.ShowFor(_controller, Window.GetWindow(this));
        private void PreviewPipeline_Click(object sender, RoutedEventArgs e) => _controller.ToggleOperationPreview();
        private void Trace_Click(object sender, RoutedEventArgs e) => ControlTraceWindow.ShowFor(_controller, Window.GetWindow(this));
        private async void Repair_Click(object sender, RoutedEventArgs e)
        {
            _repairing = true; _error = ""; Refresh();
            try { await _store.RepairComputerControlAsync(); }
            catch (Exception error) { _error = error.Message; }
            finally { _repairing = false; Refresh(); }
        }
    }
}
