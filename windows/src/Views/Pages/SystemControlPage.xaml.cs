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
            InitializeComponent();
            _store = store;
            _controller = store.SystemControl;
            Loaded += (_, __) => { _controller.PropertyChanged += Changed; Refresh(); };
            Unloaded += (_, __) => _controller.PropertyChanged -= Changed;
            EndpointText.Text = _controller.Endpoint;
            Refresh();
        }

        private void Changed(object? sender, PropertyChangedEventArgs e)
        {
            if (Dispatcher.CheckAccess()) Refresh();
            else Dispatcher.BeginInvoke(new Action(Refresh));
        }

        private void Refresh()
        {
            var enabled = _controller.IsEnabled;
            StatusText.Text = _controller.StatusText;
            StateDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, enabled ? "SuccessBrush" : "TextTertiaryBrush");
            HeroIconBadge.SetResourceReference(Border.BackgroundProperty, enabled ? "SuccessPaleBrush" : "BrandPaleBrush");
            HeroIconPath.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, enabled ? "SuccessBrush" : "BrandOrangeBrush");

            StatusDetailText.Text = enabled
                ? "客户端连接时需保留配置中的 Authorization 请求头；重启应用与解锁桌面后配置继续有效。"
                : "开启一次即可默认允许全部桌面应用与交互操作，直到你主动关闭。";

            ToggleControlButton.Content = enabled ? "关闭电脑控制" : "授权开启";
            ToggleControlButton.Style = (Style)FindResource(enabled ? "ControlDangerButton" : "ControlPrimaryButton");
            ToggleControlButton.IsEnabled = !_repairing;

            CopyConfigButton.IsEnabled = _controller.ConfigurationJSON != null;
            PreviewPipelineButton.Content = _controller.IsPipelinePreviewVisible ? "关闭顶部卡片" : "预览顶部卡片";
            RepairButton.IsEnabled = !_repairing;

            ElevationBadgeText.Text = _controller.IsElevated ? "管理员权限已就绪" : "标准用户会话";
            ElevationDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, _controller.IsElevated ? "SuccessBrush" : "BrandOrangeBrush");
            PermissionText.Text = _controller.IsElevated
                ? "当前已具备高完整性管理员权限，可正常观察与操作提权窗口；遇到连接异常时可重新校验控制组件。"
                : "当目标窗口以管理员身份运行而无法读取或点击时使用。Windows 将请求 UAC 确认并以管理员权限重启 YTray。";

            var err = _error.Length == 0 ? _controller.LastError : _error;
            ErrorText.Text = err;
            ErrorBorder.Visibility = err.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private async void Toggle_Click(object sender, RoutedEventArgs e)
        {
            _error = "";
            FeedbackBorder.Visibility = Visibility.Collapsed;
            try { await _store.SetComputerControlEnabledAsync(!_controller.IsEnabled); }
            catch (Exception error) { _error = error.Message; }
            Refresh();
        }

        private async void CopyConfig_Click(object sender, RoutedEventArgs e)
        {
            if (await McpClipboard.TryCopyAsync(this, () => _controller.ConfigurationJSON) is not bool copied) return;
            _error = copied ? "" : McpClipboard.FailureMessage;
            FeedbackText.Text = "✓ MCP 配置已复制，导入客户端时请保留 Authorization 请求头。";
            FeedbackBorder.Visibility = copied ? Visibility.Visible : Visibility.Collapsed;
            Refresh();
        }

        private void Tools_Click(object sender, RoutedEventArgs e) =>
            new SystemMcpWindow(_controller, () =>
            {
                if (Window.GetWindow(this) is ManagerView manager) manager.Activate();
            }) { Owner = Window.GetWindow(this) }.Show();

        private void InspectApps_Click(object sender, RoutedEventArgs e) =>
            ControlInspectorWindow.ShowFor(_controller, Window.GetWindow(this));

        private void PreviewPipeline_Click(object sender, RoutedEventArgs e) =>
            _controller.ToggleOperationPreview();

        private void Trace_Click(object sender, RoutedEventArgs e) =>
            ControlTraceWindow.ShowFor(_controller, Window.GetWindow(this));

        private async void Repair_Click(object sender, RoutedEventArgs e)
        {
            _repairing = true;
            _error = "";
            Refresh();
            try { await _store.RepairComputerControlAsync(); }
            catch (Exception error) { _error = error.Message; }
            finally { _repairing = false; Refresh(); }
        }
    }
}
