#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using Newtonsoft.Json.Linq;
using YTray.Core.SystemControl;

namespace YTray.Views
{
    internal sealed class ControlTraceDetails
    {
        public sealed class Field
        {
            public string Label { get; }
            public string Value { get; }
            internal Field(string label, string value) { Label = label; Value = value; }
        }

        public sealed class Stage
        {
            public string Name { get; }
            public string Time { get; }
            public bool Failed { get; }
            public bool Running { get; }
            public bool Unavailable { get; }
            public GridLength Before { get; }
            public GridLength Span { get; }
            public GridLength After { get; }
            public string Tooltip { get; }

            internal Stage(string name, string? status, bool unavailable, long offset, long duration, long total)
            {
                Name = name;
                Failed = status == "failed";
                Running = status == "running";
                Unavailable = unavailable;
                Time = Running ? "" : FormatDuration(duration);
                Tooltip = Name + "\n开始于 +" + FormatDuration(offset) + (Running ? " · 进行中" : " · 耗时 " + FormatDuration(duration))
                    + (Unavailable ? " · 未取得可用画面" : "");
                Before = new GridLength(offset, GridUnitType.Star);
                Span = new GridLength(duration, GridUnitType.Star);
                After = new GridLength(Math.Max(0, total - offset - duration), GridUnitType.Star);
            }
        }

        internal IReadOnlyList<Field> Request { get; }
        internal IReadOnlyList<Field> Response { get; }
        internal IReadOnlyList<Stage> Stages { get; }
        internal string ReturnedText { get; }
        internal string Headline { get; }
        internal string Error { get; }

        internal ControlTraceDetails(JObject record)
        {
            var tool = Text(record["tool"]);
            var arguments = record["request"]?["arguments"] as JObject;
            var operation = record["operation"] as JObject;
            var response = record["response"] as JObject;
            var state = response?["structuredContent"] as JObject;
            var error = state?["error"] as JObject ?? operation?["error"] as JObject ?? response?["error"] as JObject;
            var window = Text(state?["title"]);
            if (window.Length == 0 && (Number(arguments?["window_id"]) > 0 || Number(operation?["window_id"]) > 0)) window = Text(operation?["context"]);
            var target = window.Length == 0 ? "目标窗口" : "「" + Clip(window, 80) + "」";
            var control = Text(operation?["control_label"]);
            var status = Text(record["status"]);
            var input = new List<Field>();
            var output = new List<Field>();

            void Add(List<Field> fields, string label, string value)
            {
                if (!string.IsNullOrWhiteSpace(value)) fields.Add(new Field(label, Clip(value, 360)));
            }

            Add(input, "目标窗口", window);
            Add(input, "应用", Text(arguments?["app"]));
            Add(input, "目录", Text(arguments?["path"]));
            Add(input, "网址", Text(arguments?["url"]));
            Add(input, "查找内容", Text(arguments?["query"]));
            if (Number(arguments?["element_index"]) is long index)
                Add(input, "目标控件", control.Length == 0 ? "编号 " + index : control + "（编号 " + index + "）");
            if (Coordinate(arguments?["x"]) is string x && Coordinate(arguments?["y"]) is string y)
                Add(input, "图片坐标", "(" + x + ", " + y + ")");
            var key = Text(arguments?["key"]).ToUpperInvariant().Replace("+", " + ");
            Add(input, "按键", key);
            Add(input, "输入文字", Text(arguments?["text"]));
            if (tool == "click") {
                var button = Text(arguments?["button"]) switch { "right" => "右键", "middle" => "中键", _ => "左键" };
                Add(input, "点击方式", button + (Number(arguments?["count"]) is long count && count > 1 ? " · " + count + " 次" : ""));
                Add(input, "目标范围", RectangleText(arguments?["target_region"]));
            }
            if (tool == "type_text") {
                var replace = Flag(arguments?["replace"]) == true ? "替换原有文字" : "在焦点处输入";
                Add(input, "输入方式", replace + (Flag(arguments?["submit"]) == true ? "并按回车提交" : ""));
            }
            if (tool == "scroll") {
                var direction = Text(arguments?["direction"]) switch { "up" => "向上", "left" => "向左", "right" => "向右", _ => "向下" };
                Add(input, "滚动方向", direction + " · " + (Number(arguments?["amount"]) ?? 3) + " 步");
            }
            if (tool == "get_window_state") {
                Add(input, "读取方式", Flag(arguments?["include_screenshot"]) switch {
                    false => "仅读原生控件", true => "读取原生控件并请求窗口画面", _ => "优先读取原生控件"
                });
            }
            if (input.Count == 0) Add(input, "操作", tool switch {
                "list_windows" => "查找当前打开的窗口", "list_apps" => "查找本机应用", "system_status" => "检查电脑控制连接",
                _ => ControlOperation.ToolTitle(tool)
            });

            var execution = Text(error?["execution_state"]);
            var result = execution switch {
                "NotStarted" => "动作未执行", "Completed" => "动作已执行", "Unknown" => "动作结果待确认",
                _ => status switch { "running" => "等待响应", "cancelled" => "调用已取消", "failed" => "调用失败", _ => "调用已完成" }
            };
            if (tool == "open_folder" && Text(state?["status"]) == "accepted") result = "Windows 已接受打开目录的请求";
            if (Flag(state?["restored_from_tray"]) == true) result = "已从托盘打开现有应用窗口";
            else if (Flag(state?["reused_window"]) == true) result = "已复用现有应用窗口";
            Add(output, "结果", result);
            if (Text(state?["visual_verification"]) == "matched") Add(output, "点击前复核", Text(state?["visual_verification_caret"]) switch {
                "win32" => "按系统光标状态复核，画面匹配",
                "painted" => "局部闪烁消失后，画面匹配",
                _ => "画面匹配"
            });
            if (error?["verification"] is JObject difference) {
                var region = Text(difference["region"]) switch {
                    "target" => "目标控件",
                    "click_neighborhood" => "落点周围",
                    "context" => "上下文区域 " + Number(difference["region_index"]),
                    "client" => "窗口客户区",
                    _ => ""
                };
                if (region.Length > 0) {
                    Add(output, "复核差异", region + "内有 " + Number(difference["changed_pixels"]) + " 个像素变化 · " + RectangleText(difference["changed_bounds"]));
                    Add(output, "复核范围", RectangleText(difference["checked_bounds"]));
                } else {
                    Add(output, "复核差异", Text(difference["reason"]) switch {
                        "window_identity" => "窗口身份已变化",
                        "window_title" => "窗口标题已变化",
                        "window_bounds" => "窗口位置或尺寸已变化",
                        "client_bounds" => "窗口内容区域已变化",
                        _ => ""
                    });
                    Add(output, "原边界", RectangleText(difference["previous_bounds"]));
                    Add(output, "新边界", RectangleText(difference["current_bounds"]));
                }
                if (Flag(difference["foreground_at_observation"]) is bool observed
                    && Flag(difference["foreground_at_verification"]) is bool current && observed != current)
                    Add(output, "前台状态", (observed ? "观察时在前台" : "观察时在后台") + " · " + (current ? "复核时在前台" : "复核时在后台"));
            }
            if (Flag(error?["text_sent"]) == true) Add(output, "已执行", "文字已输入");
            if (tool == "list_apps" || tool == "list_windows" || tool == "get_desktop_state") {
                var apps = tool == "list_apps";
                if (state?[apps ? "apps" : "windows"] is JArray items) {
                    var names = items.OfType<JObject>().Select(item => Text(item[apps ? "name" : "title"]))
                        .Where(name => name.Length != 0).Distinct().Take(4).ToArray();
                    Add(output, apps ? "应用" : "窗口", items.Count + " 个" + (names.Length == 0 ? "" : " · " + string.Join("、", names))
                        + (items.Count > 4 ? " 等" : ""));
                    if (apps) foreach (var item in items.OfType<JObject>().Where(item => Flag(item["running"]) == true).Take(4)) {
                        var statusText = Text(item["window_status"]) switch {
                            "in_tray" => "正在运行，已找到托盘图标",
                            "running_without_window" => "正在运行，暂无可读窗口",
                            "window_available" => "正在运行，窗口可读",
                            _ => "正在运行"
                        };
                        Add(output, Text(item["name"]), statusText);
                    }
                }
            }
            var layout = Text(state?["layout_status"]);
            if (layout == "window_only") Add(output, "原生布局", "Windows 仅提供窗口框架");
            else if (Number(state?["controls_shown"]) is long shown) {
                var total = Number(state?["controls_total"]) ?? shown;
                Add(output, "原生布局", shown == total ? "返回 " + shown + " 个控件" : "返回 " + shown + " 个控件，共读取 " + total + " 个");
            }
            var source = Text(state?["screenshot_source"]);
            var foregroundCapture = Flag(state?["foreground_for_capture"]) == true;
            if (Number(state?["image_width"]) is long width && width > 0 && Number(state?["image_height"]) is long height && height > 0) {
                var capture = foregroundCapture ? "切到前台后取图" : source == "window_render" ? "从目标窗口取图" : "返回截图";
                Add(output, "画面", capture + " · " + width + " × " + height);
            } else if (Text(state?["screenshot_error"]) is string captureError && captureError.Length != 0) {
                Add(output, "画面", captureError);
            } else if ((response?["content"] as JArray)?.OfType<JObject>().Any(block => Text(block["type"]) == "image") == true) {
                Add(output, "画面", "响应包含截图");
            }
            var popups = (state?["related_windows"] as JArray)?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            foreach (var popup in popups) {
                var title = Text(popup["title"]);
                var popupWidth = Number(popup["image_width"]) ?? 0;
                var popupHeight = Number(popup["image_height"]) ?? 0;
                Add(output, "关联弹层", (title.Length == 0 ? "窗口 " + Number(popup["window_id"]) : title)
                    + (popupWidth > 0 && popupHeight > 0 ? " · 窗口截图 " + popupWidth + " × " + popupHeight : " · 原生观察"));
            }

            var blocks = (response?["content"] as JArray)?.OfType<JObject>().Where(block => Text(block["type"]) == "text")
                .Select(block => Text(block["text"])).Where(text => !string.IsNullOrWhiteSpace(text)
                    && !text.TrimStart().StartsWith("{", StringComparison.Ordinal) && !text.TrimStart().StartsWith("[", StringComparison.Ordinal));
            ReturnedText = string.Join("\n\n", blocks ?? Enumerable.Empty<string>());
            Error = Text(error?["message"]);
            if (status == "failed" && Error.Length == 0) Error = Clip(ReturnedText, 360);
            Headline = Flag(state?["restored_from_tray"]) == true ? "从托盘打开应用"
                : tool == "get_window_state" && window.Length != 0 ? "观察" + target : Text(operation?["description"]);
            Request = input;
            Response = output;
            Stages = ReadStages(operation, target, control, key, tool, response != null && source != "window_render"
                && !popups.Any(popup => Text(popup["screenshot_source"]) == "window_render"));
        }

        private static IReadOnlyList<Stage> ReadStages(JObject? operation, string target, string control, string key, string tool, bool windowCaptureUnavailable)
        {
            if (operation?["steps"] is not JArray steps) return Array.Empty<Stage>();
            var rows = steps.OfType<JObject>().ToArray();
            var duration = rows.Select(row => Math.Max(0, Number(row["duration_ms"]) ?? 0)).ToArray();
            var offset = new long[rows.Length];
            var total = 0L;
            for (var i = 0; i < rows.Length; i++) {
                offset[i] = Math.Max(0, Number(rows[i]["offset_ms"]) ?? total);
                total = Math.Max(total, offset[i] + duration[i]);
            }
            return rows.Select((row, i) => {
                var step = Text(row["step"]);
                var name = step switch {
                    "prepare" => "准备电脑控制连接",
                    "target" when tool == "list_windows" => "查找当前打开的窗口",
                    "target" when tool == "list_apps" => "查找本机应用",
                    "target" when tool == "system_status" => "检查控制连接",
                    "target" => "定位" + target,
                    "launch" => tool == "open_folder" ? "请 Windows 打开文件夹" : "打开应用",
                    "activate" => "确认" + target + "处于前台",
                    "activate_capture" => "后台画面不可用，切到前台取图",
                    "locate" => control.Length == 0 ? "校验窗口和输入位置" : "核对控件「" + Clip(control, 80) + "」",
                    "readback" => tool == "get_window_state" ? "准备读取窗口状态" : "读取最新界面",
                    "read_controls" => "读取 Windows 原生控件",
                    "find_tray_icon" => "读取 Windows 通知区控件",
                    "open_tray_overflow" => "展开通知区的隐藏图标",
                    "restore_tray" => "从托盘打开应用",
                    "wait_tray_window" => "确认窗口属于原应用进程",
                    "capture_window" => "尝试从目标窗口获取画面",
                    "verify_capture" => "复核截图中的目标和上下文",
                    "capture_screen" => "读取屏幕上的目标区域",
                    "format_state" => "整理控件、窗口和图像",
                    "wait_for_change" => "等待目标窗口变化",
                    "invoke_control" => control.Length == 0 ? "直接操作原生控件" : "操作原生控件「" + Clip(control, 80) + "」",
                    "scroll_control" => "使用原生控件滚动",
                    "focus" => "确认输入焦点",
                    "replace" => "选中原有文字",
                    "type_text" => "写入文字",
                    "submit" => "按回车提交",
                    "click" => "执行点击",
                    "press_key" => key.Length == 0 ? "发送按键" : "按下 " + key,
                    "scroll" => "执行滚动",
                    _ => Text(row["label"])
                };
                return new Stage(name, Text(row["status"]), step == "capture_window" && windowCaptureUnavailable,
                    offset[i], duration[i], Math.Max(1, total));
            }).ToArray();
        }

        internal static string FormatDuration(long milliseconds) => milliseconds < 1000
            ? milliseconds.ToString(CultureInfo.CurrentCulture) + " ms"
            : (milliseconds / 1000.0).ToString("0.##", CultureInfo.CurrentCulture) + " s";

        private static string Text(JToken? token) => token?.Type == JTokenType.String ? (string)token! : "";
        private static long? Number(JToken? token) => token?.Type == JTokenType.Integer && long.TryParse(token.ToString(), out var value) ? value : null;
        private static bool? Flag(JToken? token) => token?.Type == JTokenType.Boolean ? (bool)token : null;
        private static string RectangleText(JToken? token) => token is JObject area
            ? "(" + Number(area["x"]) + ", " + Number(area["y"]) + ") · " + Number(area["width"]) + " × " + Number(area["height"])
            : "";
        private static string? Coordinate(JToken? token) => token?.Type is JTokenType.Float or JTokenType.Integer ? token.ToString() : null;
        private static string Clip(string value, int limit) => value.Length <= limit ? value : value.Substring(0, limit - 1) + "…";
    }
}
