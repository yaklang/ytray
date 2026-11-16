#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Newtonsoft.Json.Linq;
using YTray.Core;
using YTray.Core.SystemControl;

namespace YTray.Views
{
    public partial class SystemMcpWindow : Window
    {
        private readonly SystemControlController _controller;
        private readonly Action? _openSettings;
        private string _catalog = "";
        private static readonly Dictionary<string, (string Title, string Summary)> Descriptions = new Dictionary<string, (string, string)> {
            ["launch_app"] = ("打开应用", "按应用名称或 EXE 路径打开，自动切到前台并返回最新界面。"),
            ["open_folder"] = ("打开文件夹", "将绝对目录交给资源管理器，返回 Windows 受理状态。"),
            ["activate_window"] = ("激活窗口", "恢复最小化窗口、切到前台并返回最新界面。"),
            ["list_apps"] = ("发现应用", "列出当前用户的应用。"),
            ["list_windows"] = ("发现窗口", "发现允许访问的窗口，获取后续观察和操作所需的目标引用。"),
            ["check_permissions"] = ("检查控制环境", "读取 Windows 会话和后端权限状态，不弹出授权请求。"),
            ["get_window_state"] = ("观察窗口", "读取指定窗口的控件树、元素引用与截图。遮挡时省略截图，仍可使用控件；操作后需要重新观察。"),
            ["get_desktop_state"] = ("主屏截图", "读取主显示器画面。"),
            ["click"] = ("点击", "使用控件编号或截图坐标点击，自动返回该应用的新界面。默认保持目标窗口在前台。"),
            ["type_text"] = ("输入文字", "向指定控件输入文本，支持中文和 emoji。"),
            ["press_key"] = ("按键", "只需窗口编号与按键，例如 ctrl+l、ctrl+t、enter；自动返回新界面。"),
            ["scroll"] = ("滚动", "在指定窗口内滚动，操作后重新观察位置与内容。"),
            ["system_status"] = ("控制状态", "查看电脑控制开关、连接状态与输入能力。"),
            ["end_session"] = ("结束本次操作", "清理本次操作的观察和输入状态。电脑控制总开关保持开启。"),
            ["ytray_request_get"] = ("查询操作结果", "使用 request_id 查询本会话已接受请求的结果，避免重复投递。"),
            ["ytray_request_cancel"] = ("取消请求", "中断尚未完成的操作。已生效的输入不会回滚，需要重新观察。"),
            ["ytray_session_get"] = ("读取会话", "读取本机已批准的会话状态。"),
            ["ytray_session_open"] = ("取得控制上下文", "返回已开启的电脑控制上下文，无需重复授权。"),
            ["ytray_session_close"] = ("结束本次操作", "清理本次操作，总开关与 MCP 配置保持有效。"),
            ["ytray_capabilities"] = ("查询能力", "读取当前会话的模式与能力限制。")
        };
        internal SystemMcpWindow(SystemControlController controller, Action? openSettings = null)
        {
            InitializeComponent(); _controller = controller; _openSettings = openSettings;
            SourceInitialized += (_, __) => ApplyTitleBar();
            Loaded += (_, __) => FitWorkingArea();
            SizeChanged += (_, __) => CompactLayout();
            controller.PropertyChanged += Changed; ThemeManager.ThemeChanged += ThemeChanged;
            Closed += (_, __) => { controller.PropertyChanged -= Changed; ThemeManager.ThemeChanged -= ThemeChanged; };
            Refresh();
        }
        private void ThemeChanged(object? sender, EventArgs e) => ApplyTitleBar();
        private void ApplyTitleBar()
        {
            var handle = new WindowInteropHelper(this).Handle; if (handle == IntPtr.Zero) return;
            var dark = ThemeManager.IsDark ? 1 : 0; _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        }
        private void FitWorkingArea()
        {
            var source = PresentationSource.FromVisual(this); var scale = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
            var handle = new WindowInteropHelper(Owner ?? this).Handle;
            var work = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
            var origin = scale.Transform(new Point(work.Left, work.Top)); var limit = scale.Transform(new Point(work.Right, work.Bottom));
            MaxWidth = Math.Max(MinWidth, limit.X - origin.X - 24); MaxHeight = Math.Max(MinHeight, limit.Y - origin.Y - 24);
            Width = Math.Min(Width, MaxWidth); Height = Math.Min(Height, MaxHeight);
            Left = Math.Max(origin.X + 12, Math.Min(Left, limit.X - ActualWidth - 12));
            Top = Math.Max(origin.Y + 12, Math.Min(Top, limit.Y - ActualHeight - 12));
        }
        private void Changed(object? sender, PropertyChangedEventArgs e) => Refresh();
        private void CompactLayout()
        {
            var compact = ActualHeight < 570 || ActualWidth < 760;
            RootLayout.Margin = new Thickness(compact ? 16 : 22);
            HeaderSubtitle.Visibility = FooterHint.Visibility = SchemaHintText.Visibility = ConnectionHint.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            ToolColumns.ColumnDefinitions[0].Width = new GridLength(compact ? 195 : 236);
        }
        private void Refresh()
        {
            StatusText.Text = _controller.StatusText; EndpointText.Text = _controller.Endpoint;
            CopyConfigButton.IsEnabled = _controller.ConfigurationJSON != null;
            ToolListTitle.Text = _controller.IsRunning ? "可用工具" : "能力预览";
            StateDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, _controller.IsRunning ? "SuccessBrush" : "TextTertiaryBrush");
            ConnectionHint.Text = _controller.IsEnabled ? "连接时需保留配置中的 Authorization 请求头。关闭电脑控制后配置失效。" : "开启电脑控制后，复制配置到 MCP 客户端即可。";
            var catalog = _controller.IsRunning ? _controller.Generation : "stopped";
            if (_catalog == catalog) return; _catalog = catalog;
            var selected = (ToolList.SelectedItem as Tool)?.Name;
            var definitions = (_controller.IsRunning ? _controller.Tools() : _controller.ReferenceTools()).OfType<JObject>().ToArray();
            var order = new[] { "list_apps", "list_windows", "launch_app", "open_folder", "get_window_state", "get_desktop_state", "click", "type_text", "press_key", "scroll", "check_permissions", "system_status", "end_session", "ytray_request_get", "ytray_request_cancel" };
            var tools = definitions.OrderBy(t => { var index = Array.IndexOf(order, (string)t["name"]!); return index < 0 ? 100 : index; }).Select(t => Make((string)t["name"]!, t["inputSchema"]?.ToString())).ToArray();
            ToolCountText.Text = tools.Length + " 项"; ToolList.ItemsSource = tools;
            ToolList.SelectedItem = tools.FirstOrDefault(t => t.Name == selected) ?? tools.FirstOrDefault();
        }
        private static Tool Make(string name, string? schema) { var details = Descriptions.TryGetValue(name, out var value) ? value : (Title: name, Summary: "读取当前授权会话的状态。"); return new Tool { Name = name, Title = details.Title, Summary = details.Summary, Schema = schema }; }
        private void Tool_Selected(object sender, SelectionChangedEventArgs e)
        {
            if (!(ToolList.SelectedItem is Tool tool)) return;
            ToolTitleText.Text = tool.Title; ToolNameText.Text = tool.Name; ToolSummaryText.Text = tool.Summary;
            SchemaHintText.Text = "直接指定应用或窗口操作；每次操作自动返回最新界面和窗口列表。";
            SchemaTextBox.Text = tool.Schema ?? "开启电脑控制后显示实际参数定义。";
            SchemaTextBox.ScrollToHome();
        }
        private async void CopyConfig_Click(object sender, RoutedEventArgs e)
        {
            if (await McpClipboard.TryCopyAsync(this, () => _controller.ConfigurationJSON) is bool copied)
                ConnectionHint.Text = copied ? "配置已复制，导入时请保留 Authorization 请求头。" : McpClipboard.FailureMessage;
        }
        private void Settings_Click(object sender, RoutedEventArgs e) { if (_openSettings != null) _openSettings(); else Owner?.Activate(); Close(); }
        private sealed class Tool { public string Name { get; set; } = ""; public string Title { get; set; } = ""; public string Summary { get; set; } = ""; public string? Schema { get; set; } }
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    }
}
