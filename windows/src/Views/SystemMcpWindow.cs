#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YTray.Core;
using YTray.Core.SystemControl;
using YTray.Native;

namespace YTray.Views
{
    public partial class SystemMcpWindow : Window
    {
        private readonly SystemControlController _controller;
        private readonly Action? _openSettings;
        private string _catalog = "";

        private static readonly Dictionary<string, (string Title, string Category, string Summary)> Descriptions =
            new Dictionary<string, (string, string, string)>
            {
                ["launch_app"] = ("打开应用", "窗口与应用", "按应用名称或 EXE 路径启动，自动切到前台并返回最新界面快照。"),
                ["open_folder"] = ("打开文件夹", "窗口与应用", "将绝对目录交给资源管理器打开，返回 Windows 受理状态。"),
                ["activate_window"] = ("激活窗口", "窗口与应用", "恢复最小化窗口、切到前台并返回最新界面状态。"),
                ["list_apps"] = ("发现应用", "窗口与应用", "列出当前登录用户桌面会话中的已安装与运行中应用。"),
                ["list_windows"] = ("发现窗口", "窗口与应用", "发现允许访问的桌面窗口，获取后续观察和操作所需的目标引用。"),
                ["get_window_state"] = ("观察窗口", "视觉与控件", "读取指定窗口的原生 UIA 控件树、元素引用与截图。遮挡时省略截图但仍可操作原生控件。"),
                ["get_desktop_state"] = ("主屏截图", "视觉与控件", "截取主显示器当前完整桌面画面。"),
                ["click"] = ("点击元素或坐标", "交互输入", "通过控件编号或截图坐标执行左键/右键/双击，自动复核并返回新界面。"),
                ["type_text"] = ("输入文字", "交互输入", "向指定控件写入文本，完整支持中文、符号与 Emoji，可选清空替换或回车提交。"),
                ["press_key"] = ("发送快捷键", "交互输入", "向目标窗口发送组合键或单键（如 ctrl+l、ctrl+t、enter），自动返回新界面。"),
                ["scroll"] = ("滚轮滚动", "交互输入", "在指定窗口内按方向与步数滚动，便于浏览长列表或文档。"),
                ["check_permissions"] = ("检查控制环境", "会话与诊断", "读取当前 Windows 会话完整性级别与后端权限状态，不触发授权弹窗。"),
                ["system_status"] = ("控制状态", "会话与诊断", "查看电脑控制总开关、网关连接状态与可用输入能力。"),
                ["end_session"] = ("结束本次操作", "会话与诊断", "清理当前任务的观察与输入缓存；电脑控制总开关保持开启。"),
                ["ytray_request_get"] = ("查询操作结果", "会话与诊断", "使用 request_id 查询本会话已受理请求的结果，避免超时重试造成重复点击。"),
                ["ytray_request_cancel"] = ("取消请求", "会话与诊断", "中断尚未执行完成的异步操作；已投递的输入不会回滚，请重新观察窗口。"),
                ["ytray_session_get"] = ("读取会话", "会话与诊断", "读取本机当前已批准的控制会话状态。"),
                ["ytray_session_open"] = ("取得控制上下文", "会话与诊断", "获取已开启的电脑控制上下文，无需重复授权。"),
                ["ytray_session_close"] = ("结束本次操作", "会话与诊断", "清理本次会话操作上下文，总开关与 MCP 凭据保持有效。"),
                ["ytray_capabilities"] = ("查询能力", "会话与诊断", "读取当前会话支持的动作模式与安全限制。"),
            };

        internal SystemMcpWindow(SystemControlController controller, Action? openSettings = null)
        {
            InitializeComponent();
            WindowChromeHelper.Attach(this);
            _controller = controller;
            _openSettings = openSettings;
            Loaded += (_, __) => FitWorkingArea();
            SizeChanged += (_, __) => CompactLayout();
            controller.PropertyChanged += Changed;
            Closed += (_, __) => controller.PropertyChanged -= Changed;
            Refresh();
        }

        private void FitWorkingArea()
        {
            var source = PresentationSource.FromVisual(this);
            var scale = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
            var handle = new WindowInteropHelper(Owner ?? this).Handle;
            var work = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
            var origin = scale.Transform(new Point(work.Left, work.Top));
            var limit = scale.Transform(new Point(work.Right, work.Bottom));
            MaxWidth = Math.Max(MinWidth, limit.X - origin.X - 24);
            MaxHeight = Math.Max(MinHeight, limit.Y - origin.Y - 24);
            Width = Math.Min(Width, MaxWidth);
            Height = Math.Min(Height, MaxHeight);
            Left = Math.Max(origin.X + 12, Math.Min(Left, limit.X - ActualWidth - 12));
            Top = Math.Max(origin.Y + 12, Math.Min(Top, limit.Y - ActualHeight - 12));
        }

        private void Changed(object? sender, PropertyChangedEventArgs e)
        {
            if (Dispatcher.CheckAccess()) Refresh();
            else Dispatcher.BeginInvoke(new Action(Refresh));
        }

        private void CompactLayout()
        {
            var compact = ActualHeight < 570 || ActualWidth < 760;
            RootLayout.Margin = new Thickness(compact ? 14 : 22, compact ? 14 : 18, compact ? 14 : 22, compact ? 14 : 18);
            HeaderSubtitle.Visibility = FooterHint.Visibility = SchemaHintBorder.Visibility = ConnectionHint.Visibility =
                compact ? Visibility.Collapsed : Visibility.Visible;
            ToolColumns.ColumnDefinitions[0].Width = new GridLength(compact ? 204 : 264);
        }

        private void Refresh()
        {
            StatusText.Text = _controller.StatusText;
            EndpointText.Text = _controller.Endpoint;
            CopyConfigButton.IsEnabled = _controller.ConfigurationJSON != null;
            ToolListTitle.Text = _controller.IsRunning ? "可用工具" : "能力预览";
            StateDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, _controller.IsRunning ? "SuccessBrush" : "TextTertiaryBrush");
            ConnectionHint.Text = _controller.IsEnabled
                ? "客户端请求需保留配置中的 Authorization 请求头；关闭电脑控制后凭据立即失效。"
                : "在电脑控制页开启总开关后，即可复制完整 JSON 配置导入客户端。";

            var catalog = _controller.IsRunning ? _controller.Generation : "stopped";
            if (_catalog == catalog) return;
            _catalog = catalog;

            var selected = (ToolList.SelectedItem as Tool)?.Name;
            var definitions = (_controller.IsRunning ? _controller.Tools() : _controller.ReferenceTools()).OfType<JObject>().ToArray();
            var order = new[]
            {
                "list_apps", "list_windows", "launch_app", "open_folder", "activate_window",
                "get_window_state", "get_desktop_state",
                "click", "type_text", "press_key", "scroll",
                "check_permissions", "system_status", "end_session", "ytray_request_get", "ytray_request_cancel",
            };
            var tools = definitions
                .OrderBy(t => { var index = Array.IndexOf(order, (string)t["name"]!); return index < 0 ? 100 : index; })
                .Select(t => Make((string)t["name"]!, t["inputSchema"]))
                .ToArray();

            ToolCountText.Text = tools.Length + " 项";
            ToolList.ItemsSource = tools;
            ToolList.SelectedItem = tools.FirstOrDefault(t => t.Name == selected) ?? tools.FirstOrDefault();
        }

        private static Tool Make(string name, JToken? schemaToken)
        {
            var details = Descriptions.TryGetValue(name, out var value)
                ? value
                : (Title: name, Category: "扩展工具", Summary: "读取或管理当前已授权会话的控制状态。");
            var formattedSchema = schemaToken != null ? schemaToken.ToString(Formatting.Indented) : null;
            return new Tool
            {
                Name = name,
                Title = details.Title,
                CategoryTag = details.Category,
                Summary = details.Summary,
                Schema = formattedSchema,
            };
        }

        private void Tool_Selected(object sender, SelectionChangedEventArgs e)
        {
            if (!(ToolList.SelectedItem is Tool tool)) return;
            ToolTitleText.Text = tool.Title;
            ToolNameText.Text = tool.Name;
            ToolSummaryText.Text = tool.Summary;
            SchemaHintText.Text = "调用说明：直接传入目标应用或 window_id 即可执行；交互动作完成后会自动返回更新后的控件树与窗口状态。";
            SchemaTextBox.Text = tool.Schema ?? "开启电脑控制后即可查看实时 JSON Schema 参数定义。";
            SchemaTextBox.ScrollToHome();
        }

        private async void CopyConfig_Click(object sender, RoutedEventArgs e)
        {
            if (await McpClipboard.TryCopyAsync(this, () => _controller.ConfigurationJSON) is bool copied)
                ConnectionHint.Text = copied ? "✓ 配置已复制，导入客户端时请保留 Authorization 请求头。" : McpClipboard.FailureMessage;
        }

        private async void CopyToolName_Click(object sender, RoutedEventArgs e)
        {
            if (!(ToolList.SelectedItem is Tool tool)) return;
            if (await McpClipboard.TryCopyAsync(this, () => tool.Name) == true)
                ConnectionHint.Text = $"✓ 已复制函数名：{tool.Name}";
        }

        private async void CopySchema_Click(object sender, RoutedEventArgs e)
        {
            var text = SchemaTextBox.Text;
            if (string.IsNullOrWhiteSpace(text)) return;
            if (await McpClipboard.TryCopyAsync(this, () => text) == true)
                ConnectionHint.Text = "✓ 已复制当前工具的 JSON Schema 定义。";
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            if (_openSettings != null) _openSettings();
            else Owner?.Activate();
            Close();
        }

        private sealed class Tool
        {
            public string Name { get; set; } = "";
            public string Title { get; set; } = "";
            public string CategoryTag { get; set; } = "";
            public string Summary { get; set; } = "";
            public string? Schema { get; set; }
        }
    }
}
