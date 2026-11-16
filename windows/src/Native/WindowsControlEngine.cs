#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using YTray.Core.SystemControl;
using YTray.Models;
using static YTray.Native.WindowsAutomation;

namespace YTray.Native
{
    // Self-contained Windows backend: inbox UI Automation COM, user32 and GDI.
    // Runs only in YTray's private worker, never on the UI/MCP dispatch thread.
    internal sealed class WindowsControlEngine : IDisposable
    {
        private readonly IAutomation _automation = Create();
        private readonly WindowsActionOverlay _feedback = new WindowsActionOverlay();
        private readonly Dictionary<long, Dictionary<string, IElement>> _elements = new Dictionary<long, Dictionary<string, IElement>>();
        private static readonly int[] Properties = { 30001, 30003, 30005, 30008, 30010, 30011, 30019, 30020, 30022, 30024, 30045, 30070, 30079, 30086, 30093 };
        internal JObject ReportFeedback(JObject parameters)
        {
            WindowsControlTarget.RequireDesktop();
            var phase = (string?)parameters["phase"] ?? "";
            if (phase != "prepare" && phase != "running" && phase != "completed" && phase != "failed") throw new ControlException("InvalidArguments", "未知操作反馈阶段");
            var shown = _feedback.Operation(parameters.Value<long>("id"), phase, new IntPtr((long?)parameters["window_id"] ?? 0),
                Clip((string?)parameters["description"] ?? "", 160), Clip((string?)parameters["context"] ?? "", 120));
            return ControlJson.Result(new JObject { ["shown"] = shown });
        }
        internal JObject Call(string method, JObject args)
        {
            if (method == "start_session" || method == "end_session") return ControlJson.Result(new JObject());
            WindowsControlTarget.RequireDesktop();
            if (method == "check_permissions") return ControlJson.Result(new JObject { ["backend"] = "windows-native", ["available"] = true });
            var desktop = method == "get_desktop_state" || (string?)args["target"]?["kind"] == "desktop";
            var id = desktop ? 0 : args.Value<long>("window_id");
            var target = id == 0 ? null : WindowsControlTarget.Resolve(new IntPtr(id));
            if (target != null && args["pid"] != null && args.Value<int>("pid") != target.Pid) throw new ControlException("TargetChanged", "窗口所属进程已变化，请重新观察");
            if (method == "get_window_state" || method == "get_desktop_state") return Read(target, args);
            return Act(method, target, args);
        }
        private JObject Read(WindowsControlTarget? target, JObject args)
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen ?? throw new ControlException("DesktopUnavailable", "当前没有可用显示器");
            var bounds = target == null ? screen.Bounds : Bounds(target);
            if (bounds.Width <= 0 || bounds.Height <= 0 || bounds.Width > 20000 || bounds.Height > 20000) throw new ControlException("WindowNotReady", "窗口尺寸暂不可用，请恢复窗口后继续");
            var maximum = Math.Max(100, Math.Min(4096, (int?)args["max_image_dimension"] ?? 1400));
            var scale = Math.Min(1, maximum / (double)Math.Max(bounds.Width, bounds.Height));
            var state = new JObject { ["window_id"] = target?.Handle.ToInt64() ?? 0, ["window_title"] = target?.Title ?? "Desktop",
                ["window_bounds"] = new JObject { ["x"] = bounds.Left, ["y"] = bounds.Top, ["width"] = bounds.Width, ["height"] = bounds.Height },
                ["screenshot_width"] = (int)Math.Round(bounds.Width * scale), ["screenshot_height"] = (int)Math.Round(bounds.Height * scale) };
            if (target != null && args["include_accessibility_tree"]?.Value<bool>() != false) ReadTree(target, state, args);
            var result = ControlJson.Result(state);
            if (args["include_screenshot"]?.Value<bool>() != false) {
                try {
                    var capture = WindowsNativeCapture.Read(target?.Handle ?? IntPtr.Zero, bounds, maximum, target?.IsObscured(IntPtr.Zero) ?? false);
                    if (capture["image"] is JObject image) { ((JArray)result["content"]!).Add(image); capture.Remove("image"); }
                    foreach (var property in capture.Properties().ToArray()) state[property.Name] = property.Value;
                } catch (Exception error) { state["screenshot_error"] = error.Message; state["screenshot_frame_valid"] = false; }
            }
            return result;
        }
        private void ReadTree(WindowsControlTarget target, JObject state, JObject args)
        {
            var elements = new JArray(); var lookup = new Dictionary<string, IElement>();
            var maximum = Math.Max(1, Math.Min(1000, (int?)args["max_elements"] ?? 500));
            var maxDepth = Math.Max(1, Math.Min(25, (int?)args["max_depth"] ?? 22));
            var timeout = Math.Max(100, Math.Min(5000, (int?)args["timeout_ms"] ?? 1500));
            var elapsed = Stopwatch.StartNew();
            var cache = _automation.CreateCacheRequest(); foreach (var property in Properties) cache.AddProperty(property);
            var walker = _automation.ControlViewWalker();
            var pending = new Stack<(IElement Element, int Depth, int Parent, bool Web)>();
            try {
                pending.Push((_automation.ElementFromHandleBuildCache(target.Handle, cache), 0, -1, false));
                while (pending.Count > 0 && elements.Count < maximum && elapsed.ElapsedMilliseconds < timeout) {
                    var entry = pending.Pop(); var element = entry.Element;
                    try {
                        var role = Role(Convert.ToInt32(element.Cached(30003)));
                        var index = elements.Count; var token = string.Join(".", element.GetRuntimeId());
                        var password = element.Cached(30019) is bool secret && secret;
                        var row = new JObject { ["element_index"] = index, ["element_token"] = token, ["role"] = role,
                            ["label"] = Clip(element.Cached(30005) as string ?? "", 2000), ["enabled"] = element.Cached(30010) as bool? ?? true,
                            ["focused"] = element.Cached(30008) as bool? ?? false, ["offscreen"] = element.Cached(30022) as bool? ?? false,
                            ["frame"] = Frame(Rect(element.Cached(30001))), ["in_web_content"] = entry.Web || role == "Document" };
                        if (entry.Parent >= 0) row["parent_index"] = entry.Parent;
                        var automationId = element.Cached(30011) as string;
                        if (role == "CheckBox" && element.Cached(30086) is int toggle) { row["checked"] = toggle == 1; if (toggle == 2) row["indeterminate"] = true; }
                        if (new[] { "RadioButton", "ListItem", "TabItem", "TreeItem", "DataItem" }.Contains(role) && element.Cached(30079) is bool selected) row["selected"] = selected;
                        if ((role == "TreeItem" || role == "ComboBox") && element.Cached(30070) is int expanded && expanded != 3) row["expanded"] = expanded == 1 || expanded == 2;
                        if (!string.IsNullOrEmpty(automationId)) {
                            row["automation_id"] = automationId;
                            if (string.IsNullOrEmpty((string?)row["label"])) row["label"] = automationId;
                        }
                        var frame = (JObject)row["frame"]!; var bounds = Bounds(target);
                        var sx = state.Value<double>("screenshot_width") / bounds.Width; var sy = state.Value<double>("screenshot_height") / bounds.Height;
                        row["screenshot_frame"] = new JObject { ["x"] = (frame.Value<double>("x") - bounds.X) * sx, ["y"] = (frame.Value<double>("y") - bounds.Y) * sy,
                            ["w"] = frame.Value<double>("w") * sx, ["h"] = frame.Value<double>("h") * sy };
                        if (!password) {
                            var value = element.Cached(30045) as string;
                            if (string.IsNullOrEmpty(value)) value = element.Cached(30093) as string;
                            if (!string.IsNullOrEmpty(value)) row["value"] = Clip(value!, 2000);
                        }
                        elements.Add(row); lookup[token] = element;
                        if (entry.Depth > 0) { var next = walker.NextSiblingBuildCache(element, cache); if (next != null) pending.Push((next, entry.Depth, entry.Parent, entry.Web)); }
                        if (entry.Depth < maxDepth && (!(bool)row["offscreen"]! || maximum > 500)) {
                            var child = walker.FirstChildBuildCache(element, cache);
                            if (child != null) pending.Push((child, entry.Depth + 1, index, entry.Web || role == "Document"));
                        }
                    } catch (COMException) { state["truncated"] = true; }
                }
                if (pending.Count > 0) state["truncated"] = true;
            } catch (COMException error) { state["accessibility_error"] = error.Message; state["truncated"] = true; }
            if (_elements.Count >= 8 && !_elements.ContainsKey(target.Handle.ToInt64())) _elements.Remove(_elements.Keys.First());
            _elements[target.Handle.ToInt64()] = lookup;
            var query = (string?)args["query"];
            state["elements"] = string.IsNullOrWhiteSpace(query) ? elements : new JArray(elements.OfType<JObject>()
                .Where(element => ((string?)element["label"] ?? "").Contains(query!, StringComparison.OrdinalIgnoreCase)
                    || ((string?)element["value"] ?? "").Contains(query!, StringComparison.OrdinalIgnoreCase)).Select(element => element.DeepClone()));
        }
        private JObject Act(string method, WindowsControlTarget? target, JObject args)
        {
            var foreground = (string?)args["delivery_mode"] != "background";
            var window = target?.Handle ?? IntPtr.Zero;
            IElement? element = null;
            if (args["element_token"] is JValue token) {
                if (!_elements.TryGetValue(window.ToInt64(), out var known) || !known.TryGetValue((string)token!, out element)) throw new ControlException("ElementChanged", "控件已更新，请重新观察");
                // A live COM property read verifies that this element still exists.
                if (element.Current(30010) is bool enabled && !enabled) throw new ControlException("ElementDisabled", "控件当前不可用");
            }
            var bounds = target == null ? System.Windows.Forms.Screen.PrimaryScreen!.Bounds : Bounds(target);
            var frame = element == null ? Rectangle.Empty : Rect(element.Current(30001));
            if (method == "click" && foreground && element != null && (frame.IsEmpty || !frame.IntersectsWith(bounds))) {
                if (TryPattern<IScrollItem>(element, 10017, out var item)) { item!.ScrollIntoView(); frame = Rect(element.Current(30001)); }
                if (frame.IsEmpty || !frame.IntersectsWith(bounds)) throw new ControlException("ElementNotVisible", "控件当前不可见，请滚动或展开后继续");
            }
            var point = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            if (element != null && frame.Width > 0 && frame.Height > 0) {
                if (frame.IntersectsWith(bounds) || foreground && (method == "click" || method == "scroll"))
                    point = WindowsInputGeometry.VisibleCenter(frame, bounds);
                else point = new Point(frame.Left + frame.Width / 2, frame.Top + frame.Height / 2);
            }
            if (args["x"] != null && args["y"] != null) {
                var dimensions = args["_image_size"] as JObject;
                var width = (double?)dimensions?["width"] ?? bounds.Width; var height = (double?)dimensions?["height"] ?? bounds.Height;
                var x = args.Value<double>("x"); var y = args.Value<double>("y");
                if (double.IsNaN(x) || double.IsInfinity(x) || double.IsNaN(y) || double.IsInfinity(y) || x < 0 || y < 0 || x >= width || y >= height)
                    throw new ControlException("InvalidCoordinates", "坐标必须位于当前返回的图片范围内");
                point = new Point(bounds.Left + (int)(args.Value<double>("x") * bounds.Width / Math.Max(1, width)), bounds.Top + (int)(args.Value<double>("y") * bounds.Height / Math.Max(1, height)));
                if (!foreground && element == null && target != null && !target.IsObscured(IntPtr.Zero)) {
                    var hit = _automation.ElementFromPoint(new NativePoint { X = point.X, Y = point.Y });
                    if (BelongsTo(hit, window)) element = hit;
                }
            }
            var nativeHandle = element?.Current(30020) is int handle && handle != 0 ? new IntPtr(handle) : window;
            if (foreground) WindowsNativeInput.RequireForeground(window);
            // Keyboard operations without an explicit element still indicate the
            // actual focused field, using one property read rather than a tree walk.
            var indicatorPoint = point;
            if (foreground && element == null && (method == "type_text" || method == "press_key")) {
                try {
                    var focused = _automation.GetFocusedElement();
                    var focusedFrame = Rect(focused.Current(30001));
                    if (!focusedFrame.IsEmpty && focusedFrame.IntersectsWith(bounds) && BelongsTo(focused, window))
                        indicatorPoint = new Point(focusedFrame.Left + focusedFrame.Width / 2, focusedFrame.Top + focusedFrame.Height / 2);
                } catch { /* Focus decoration cannot prevent keyboard input. */ }
            }
            var label = method == "click" ? ((string?)args["button"] == "right" ? "右键" : (int?)args["count"] == 2 ? "双击" : "点击")
                : method == "type_text" ? "输入"
                : method == "scroll" ? "滚动 " + ((string?)args["direction"] == "up" ? "↑" : (string?)args["direction"] == "left" ? "←" : (string?)args["direction"] == "right" ? "→" : "↓")
                : "按键 " + string.Join(" + ", (args["modifiers"] as JArray ?? new JArray()).Values<string>().Concat(new[] { (string?)args["key"] ?? "" }));
            var visible = foreground || target != null && !target.IsObscured(IntPtr.Zero);
            using var feedback = visible ? _feedback.Begin(window, indicatorPoint, Clip(label, 32)) : new WindowsActionOverlay.Feedback(null, 0);
            // A readable glide leaves time for a page to reflow. Follow a moved
            // element once, then refuse an unstable target rather than misclick.
            if (foreground && method == "click" && element != null && args["x"] == null) {
                Point LivePoint() {
                    var current = Rect(element.Current(30001));
                    if (current.IsEmpty || current.Width <= 0 || current.Height <= 0) throw new ControlException("ElementNotVisible", "控件已隐藏，请重新观察");
                    return WindowsInputGeometry.VisibleCenter(current, bounds);
                }
                try {
                    var live = LivePoint();
                    if (Math.Abs(live.X - point.X) > 4 || Math.Abs(live.Y - point.Y) > 4) {
                        feedback.Retarget(window, live, Clip(label, 32)); point = live;
                        var settled = LivePoint();
                        if (Math.Abs(settled.X - point.X) > 4 || Math.Abs(settled.Y - point.Y) > 4)
                            throw new ControlException("ElementChanged", "控件仍在移动，已停止点击，请使用最新界面继续");
                    }
                } catch (COMException) { throw new ControlException("ElementChanged", "控件已更新，请使用最新界面继续"); }
            }
            // The feedback glide takes time. Window moves during it invalidate
            // the original target geometry even if foreground ownership is intact.
            if (foreground && target != null) target.Revalidate(true);
            switch (method) {
                case "click":
                    var button = (string?)args["button"] ?? "left"; var count = (int?)args["count"] ?? 1;
                    if (!foreground && element != null && button == "left" && count == 1) {
                        if (BackgroundClick(element, window)) break;
                    }
                    WindowsNativeInput.Click(window, point.X, point.Y, button, count, foreground); break;
                case "type_text":
                    var text = (string?)args["text"] ?? "";
                    if (foreground && element != null) element.SetFocus();
                    if (!foreground && element != null && nativeHandle == window && TryPattern<IValue>(element, 10002, out var value)) {
                        value!.SetValue((element.Current(30045) as string ?? "") + text); break;
                    }
                    WindowsNativeInput.Text(foreground ? window : nativeHandle, text, foreground); break;
                case "press_key":
                    if ((foreground || args["_focus_before_key"]?.Value<bool>() == true) && element != null) element.SetFocus();
                    WindowsNativeInput.Press(foreground ? window : nativeHandle, (string?)args["key"] ?? "", (args["modifiers"] as JArray ?? new JArray()).Values<string>()!, foreground); break;
                case "scroll":
                    var direction = (string?)args["direction"] ?? "down"; var amount = Math.Max(1, Math.Min(50, (int?)args["amount"] ?? 3));
                    if (!foreground && element != null && TryPattern<IScroll>(element, 10004, out var scroll)) {
                        for (var i = 0; i < amount; i++) scroll!.Scroll(direction == "left" ? 1 : direction == "right" ? 4 : 2, direction == "up" ? 1 : direction == "down" ? 4 : 2); break;
                    }
                    WindowsNativeInput.Scroll(window, point.X, point.Y, direction, amount, foreground); break;
                default: throw new ControlException("UnknownTool", "不支持的原生操作：" + method);
            }
            feedback.Complete();
            return ControlJson.Result(new JObject { ["completed"] = true });
        }
        public void Dispose() => _feedback.Dispose();
        private bool BackgroundClick(IElement element, IntPtr window)
        {
            var walker = _automation.ControlViewWalker();
            for (var depth = 0; depth < 8 && element != null; depth++) {
                if (Convert.ToInt32(element.Current(30003)) == 50032) break;
                if (TryPattern<IInvoke>(element, 10000, out var invoke)) { invoke!.Invoke(); return true; }
                if (TryPattern<IToggle>(element, 10015, out var toggle)) { toggle!.Toggle(); return true; }
                if (TryPattern<ISelectionItem>(element, 10010, out var select)) { select!.Select(); return true; }
                element = walker.Parent(element);
            }
            return false;
        }
        private bool BelongsTo(IElement element, IntPtr window)
        {
            var walker = _automation.ControlViewWalker();
            for (var depth = 0; depth < 32 && element != null; depth++) {
                if (element.Current(30020) is int handle && handle != 0) return GetAncestor(new IntPtr(handle), 2) == window;
                element = walker.Parent(element);
            }
            return false;
        }
        private static bool TryPattern<T>(IElement element, int id, out T? pattern) where T : class { try { pattern = element.Pattern(id) as T; return pattern != null; } catch (COMException) { pattern = null; return false; } }
        private static Rectangle Bounds(WindowsControlTarget target) => Rectangle.FromLTRB(target.Bounds.Left, target.Bounds.Top, target.Bounds.Right, target.Bounds.Bottom);
        private static Rectangle Rect(object value) { if (value is double[] a && a.Length == 4) return new Rectangle((int)Math.Round(a[0]), (int)Math.Round(a[1]), Math.Max(0, (int)Math.Round(a[2])), Math.Max(0, (int)Math.Round(a[3]))); return Rectangle.Empty; }
        private static JObject Frame(Rectangle rectangle) => new JObject { ["x"] = rectangle.Left, ["y"] = rectangle.Top, ["w"] = rectangle.Width, ["h"] = rectangle.Height };
        private static string Clip(string value, int maximum) => value.Length <= maximum ? value : value.Substring(0, maximum - (char.IsHighSurrogate(value[maximum - 1]) ? 1 : 0));
        private static readonly string[] Roles = { "Button", "Calendar", "CheckBox", "ComboBox", "Edit", "Hyperlink", "Image", "ListItem", "List", "Menu", "MenuBar", "MenuItem", "ProgressBar", "RadioButton", "ScrollBar", "Slider", "Spinner", "StatusBar", "Tab", "TabItem", "Text", "ToolBar", "ToolTip", "Tree", "TreeItem", "Custom", "Group", "Thumb", "DataGrid", "DataItem", "Document", "SplitButton", "Window", "Pane", "Header", "HeaderItem", "Table", "TitleBar", "Separator", "SemanticZoom", "AppBar" };
        private static string Role(int id) => id >= 50000 && id < 50000 + Roles.Length ? Roles[id - 50000] : "Custom";
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    }
}
