#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using YTray.Native;

namespace YTray.Core.SystemControl
{
    internal sealed class ControlOperation
    {
        private readonly object _sync = new object();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<(string Name, long Started, long? Ended)> _steps = new List<(string, long, long?)>();
        private WindowsControlTarget? _target;
        private string? _elementToken;
        private string? _controlLabel;
        private JObject? _error;
        private string _phase = "running";
        private readonly long _requestedWindow;
        private readonly string _description, _context, _tool;

        internal long Id { get; }
        internal string Tool => _tool;
        internal string Label { get; }
        internal ControlTask? Task { get; set; }
        internal string? TraceId { get; set; }
        internal long TaskPosition { get; set; }
        internal long? ResumedFrom { get; set; }
        internal bool Desktop { get; }
        internal WindowsControlTarget? Target { get { lock (_sync) return _target; } }
        internal string? ElementToken { get { lock (_sync) return _elementToken; } }
        internal string? CurrentStep { get { lock (_sync) return _steps.Count == 0 ? null : _steps[_steps.Count - 1].Name; } }
        internal bool Failed { get { lock (_sync) return _phase == "failed"; } }
        internal JObject? Failure { get { lock (_sync) return (JObject?)_error?.DeepClone(); } }

        internal ControlOperation(long id, string tool, long window, string label, string description, string context, WindowsControlTarget? target, string? elementToken)
        {
            Id = id; _tool = tool; _requestedWindow = window; Label = label; _description = description; _context = context;
            _target = target; _elementToken = elementToken;
            Desktop = window == 0 && (tool is "click" or "type_text" or "press_key" or "scroll" or "get_desktop_state" or "get_window_state");
        }

        internal void Bind(WindowsControlTarget target) { lock (_sync) _target = target; }
        internal void SetElement(string? token, string? label)
        {
            lock (_sync) { _elementToken = token; _controlLabel = label; }
        }

        internal bool Enter(string step)
        {
            lock (_sync) {
                if (_phase != "running" || _steps.Count != 0 && _steps[_steps.Count - 1].Name == step) return false;
                var now = _clock.ElapsedMilliseconds;
                CloseStep(now);
                _steps.Add((step, now, null));
                return true;
            }
        }

        internal bool Finish(JObject? error = null)
        {
            lock (_sync) {
                if (_phase != "running") return false;
                CloseStep(_clock.ElapsedMilliseconds);
                _phase = error == null ? "completed" : "failed";
                if (error != null) {
                    _error = (JObject)error.DeepClone();
                    if (_steps.Count != 0) _error["step"] = _steps[_steps.Count - 1].Name;
                }
                return true;
            }
        }

        private void CloseStep(long now)
        {
            if (_steps.Count == 0) return;
            var last = _steps[_steps.Count - 1];
            _steps[_steps.Count - 1] = (last.Name, last.Started, now);
        }

        internal JObject Snapshot()
        {
            lock (_sync) {
                var steps = new JArray();
                for (var index = 0; index < _steps.Count; index++) {
                    var step = _steps[index];
                    steps.Add(new JObject {
                        ["step"] = step.Name, ["label"] = StepTitle(step.Name),
                        ["status"] = index == _steps.Count - 1 ? _phase : "completed",
                        ["offset_ms"] = step.Started,
                        ["duration_ms"] = (step.Ended ?? _clock.ElapsedMilliseconds) - step.Started
                    });
                }
                var result = new JObject {
                    ["id"] = Id, ["tool"] = _tool, ["phase"] = _phase, ["window_id"] = _target?.Handle.ToInt64() ?? _requestedWindow,
                    ["label"] = Label, ["description"] = _description, ["context"] = _target?.Title ?? _context, ["steps"] = steps
                };
                if (_error != null) result["error"] = _error.DeepClone();
                if (!string.IsNullOrWhiteSpace(_controlLabel)) result["control_label"] = _controlLabel;
                if (TraceId != null) result["trace_id"] = TraceId;
                if (TaskPosition != 0) result["position"] = TaskPosition;
                if (ResumedFrom.HasValue) result["resumed_from"] = ResumedFrom.Value;
                return result;
            }
        }

        internal JObject Summary()
        {
            lock (_sync) {
                var summary = new JObject {
                    ["id"] = Id, ["position"] = TaskPosition, ["tool"] = _tool, ["label"] = Label,
                    ["description"] = _description.Length <= 110 ? _description : _description.Substring(0, 109) + "…", ["phase"] = _phase
                };
                if (_error != null) summary["error"] = _error.DeepClone();
                if (TraceId != null) summary["trace_id"] = TraceId;
                if (ResumedFrom.HasValue) summary["resumed_from"] = ResumedFrom.Value;
                return summary;
            }
        }

        internal static string ToolTitle(string tool, JObject? arguments = null)
        {
            if (tool == "launch_app" && arguments?["url"]?.Type == JTokenType.String
                && Uri.TryCreate((string?)arguments["url"], UriKind.Absolute, out var uri)
                && (uri.Scheme == "http" || uri.Scheme == "https")) {
                var host = uri.Host;
                if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host.Substring(4);
                return "打开 " + host;
            }
            return tool switch {
                "list_apps" => "查找应用", "list_windows" => "查找窗口", "system_status" => "检查状态",
                "get_window_state" => "观察窗口", "get_desktop_state" => "观察桌面",
                "launch_app" => "打开应用", "activate_window" => "切换窗口", "open_folder" => "打开目录",
                "click" or "type_text" or "press_key" or "scroll" => StepTitle(tool),
                _ => tool
            };
        }

        internal static string StepTitle(string? step) => step switch {
            "target" => "定位", "launch" => "打开", "activate" => "前台", "locate" => "校验",
            "focus" => "聚焦", "replace" => "全选", "type_text" => "输入", "submit" => "提交",
            "click" => "点击", "press_key" => "按键", "scroll" => "滚动", "readback" => "读回",
            "read_controls" => "读取原生控件", "capture_window" => "窗口截图", "capture_screen" => "屏幕截图",
            "verify_capture" => "点击前复核", "wait_for_change" => "等待界面变化", "format_state" => "整理回包",
            "find_tray_icon" => "查找托盘图标", "open_tray_overflow" => "展开托盘", "restore_tray" => "从托盘打开应用", "wait_tray_window" => "确认原窗口",
            "activate_capture" => "切到前台取图", "invoke_control" => "操作原生控件", "scroll_control" => "滚动原生控件",
            _ => "准备"
        };
    }
}
