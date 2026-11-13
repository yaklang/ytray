#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
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
        private readonly WindowsActionOverlay _feedback;
        private readonly Action<string, JObject>? _notify;
        private readonly Dictionary<long, Dictionary<string, IElement>> _elements = new Dictionary<long, Dictionary<string, IElement>>();
        private readonly WindowsClickVerification _clickVerification = new WindowsClickVerification();
        private static readonly int[] Properties = { 30001, 30003, 30005, 30008, 30010, 30011, 30019, 30020, 30022, 30024, 30045, 30070, 30079, 30086, 30093 };
        private static readonly (int Property, string Pattern)[] InspectionPatterns = {
            (30031, "Invoke"), (30034, "Scroll"), (30036, "SelectionItem"), (30040, "Text"), (30041, "Toggle"), (30043, "Value")
        };
        internal WindowsControlEngine(Action<string, JObject>? notify = null)
        {
            _notify = notify;
            _feedback = new WindowsActionOverlay(id => _notify?.Invoke("operation/show_trace", new JObject { ["trace_id"] = id }));
        }
        internal JObject ReportFeedback(JObject parameters)
        {
            WindowsControlTarget.RequireDesktop();
            var phase = (string?)parameters["phase"] ?? "";
            if (phase != "prepare" && phase != "running" && phase != "completed" && phase != "failed") throw new ControlException("InvalidArguments", "未知操作反馈阶段");
            var shown = _feedback.Operation(parameters.Value<long>("id"), phase, new IntPtr((long?)parameters["window_id"] ?? 0),
                Clip((string?)parameters["description"] ?? "", 160), Clip((string?)parameters["context"] ?? "", 120),
                parameters["task"] as JObject, SystemControlController.HasOperationFeedback((string?)parameters["tool"] ?? ""),
                (string?)parameters["trace_id"], Clip((string?)parameters["label"] ?? "", 50));
            return ControlJson.Result(new JObject { ["shown"] = shown });
        }
        internal JObject HighlightInspection(JObject parameters, WaitHandle? cancellation)
        {
            WindowsControlTarget.RequireDesktop();
            var target = WindowsControlTarget.Resolve(new IntPtr(parameters.Value<long>("window_id")));
            var bounds = Bounds(target);
            var original = (JObject)parameters["window_bounds"]!;
            if (target.Pid != parameters.Value<int>("pid") || target.Created != parameters.Value<long>("created")
                || bounds != new Rectangle(original.Value<int>("x"), original.Value<int>("y"), original.Value<int>("width"), original.Value<int>("height"))
                || WindowsApplicationControl.IsMinimized(target.Handle))
                throw new ControlException("TargetChanged", "窗口位置已改变，请重新分析。");
            var frame = (JObject)parameters["frame"]!;
            var rectangle = Rectangle.Intersect(bounds, new Rectangle(frame.Value<int>("x"), frame.Value<int>("y"), frame.Value<int>("w"), frame.Value<int>("h")));
            rectangle.Intersect(System.Windows.Forms.SystemInformation.VirtualScreen);
            if (rectangle.Width < 4 || rectangle.Height < 4) throw new ControlException("ElementNotVisible", "控件当前不在窗口的可见区域内。");
            ControlCancellation.Check(cancellation);
            if (!_feedback.Highlight(rectangle)) throw new ControlException("HighlightUnavailable", "无法显示控件高亮。");
            return ControlJson.Result(new JObject { ["shown"] = true });
        }
        internal JObject Call(string method, JObject args, WaitHandle? cancellation = null, long operationId = 0)
        {
            ControlCancellation.Check(cancellation);
            if (method == "start_session" || method == "end_session") return ControlJson.Result(new JObject());
            WindowsControlTarget.RequireDesktop();
            if (method == "check_permissions") return ControlJson.Result(new JObject { ["backend"] = "windows-native", ["available"] = true });
            var desktop = method == "get_desktop_state" || (string?)args["target"]?["kind"] == "desktop";
            var id = desktop ? 0 : args.Value<long>("window_id");
            void Stage(string step) {
                if (operationId == 0) return;
                if (step == "locate" && id != 0) _feedback.OperationTarget(operationId, new IntPtr(id));
                _notify?.Invoke("operation/progress", new JObject { ["operation_id"] = operationId, ["step"] = step });
            }
            var read = method == "get_window_state" || method == "get_desktop_state";
            if (!read) Stage("locate");
            var owner = args.Value<long>("_owner_window_id");
            WindowsControlTarget? target;
            try { target = id == 0 ? null : WindowsControlTarget.Resolve(new IntPtr(id)); }
            catch (ControlException error) when (owner != 0 && error.Code == "TargetChanged") { throw WindowsControlTarget.PopupChanged(); }
            if (target != null && args["pid"] != null && args.Value<int>("pid") != target.Pid) throw new ControlException("TargetChanged", "窗口所属进程已变化，请重新观察");
            if (owner != 0 && target != null) {
                if (target.Owner.ToInt64() != owner) throw WindowsControlTarget.PopupChanged();
                target.RequireVisibleOwned();
            }
            if (read) return Read(target, args, cancellation, Stage);
            return Act(method, target, args, cancellation, Stage);
        }
        private JObject Read(WindowsControlTarget? target, JObject args, WaitHandle? cancellation, Action<string> stage)
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen ?? throw new ControlException("DesktopUnavailable", "当前没有可用显示器");
            var bounds = target == null ? screen.Bounds : Bounds(target);
            if (bounds.Width <= 0 || bounds.Height <= 0 || bounds.Width > 20000 || bounds.Height > 20000) throw new ControlException("WindowNotReady", "窗口尺寸暂不可用，请恢复窗口后继续");
            var maximum = Math.Max(100, Math.Min(4096, (int?)args["max_image_dimension"] ?? 1400));
            var scale = Math.Min(1, maximum / (double)Math.Max(bounds.Width, bounds.Height));
            var state = new JObject { ["window_id"] = target?.Handle.ToInt64() ?? 0, ["window_title"] = target?.Title ?? "Desktop",
                ["window_bounds"] = new JObject { ["x"] = bounds.Left, ["y"] = bounds.Top, ["width"] = bounds.Width, ["height"] = bounds.Height },
                ["screenshot_width"] = (int)Math.Round(bounds.Width * scale), ["screenshot_height"] = (int)Math.Round(bounds.Height * scale) };
            ControlCancellation.Check(cancellation);
            Dictionary<string, IElement>? lookup = null;
            if (target != null && args["include_accessibility_tree"]?.Value<bool>() != false) {
                stage("read_controls");
                lookup = ReadTree(target, state, args, bounds, cancellation);
            }
            var compact = args["_compact_observation"]?.Value<bool>() == true;
            var result = compact ? new JObject { ["content"] = new JArray(), ["structuredContent"] = state, ["isError"] = false } : ControlJson.Result(state);
            if (args["include_screenshot"]?.Value<bool>() != false
                || args.Value<bool>("_capture_if_no_layout") && (string?)state["layout_status"] == "window_only") {
                ControlCancellation.Check(cancellation);
                var captureClock = Stopwatch.StartNew();
                try {
                    var capture = WindowsNativeCapture.Read(target, bounds, maximum, args.Value<bool>("_prefer_screen_capture"), cancellation, stage);
                    if (capture["image"] is JObject image) {
                        if (target != null && compact && args["_inspection"]?.Value<bool>() != true)
                            result["_visual_capture_id"] = _clickVerification.Remember(target, bounds, capture, image);
                        ((JArray)result["content"]!).Add(image);
                        capture.Remove("image");
                    }
                    foreach (var property in capture.Properties().ToArray()) state[property.Name] = property.Value;
                } catch (Exception error) { state["screenshot_error"] = error.Message; state["screenshot_frame_valid"] = false; }
                finally { if (compact) result["_capture_ms"] = captureClock.ElapsedMilliseconds; }
            }
            ControlCancellation.Check(cancellation);
            if (lookup != null && args["_inspection"]?.Value<bool>() != true) {
                var windowId = target!.Handle.ToInt64();
                if (_elements.Count >= 8 && !_elements.ContainsKey(windowId)) _elements.Remove(_elements.Keys.First());
                _elements[windowId] = lookup;
            }
            return result;
        }
        private Dictionary<string, IElement> ReadTree(WindowsControlTarget target, JObject state, JObject args, Rectangle bounds, WaitHandle? cancellation)
        {
            var maximum = Math.Max(1, Math.Min(1000, (int?)args["max_elements"] ?? 500));
            var maxDepth = Math.Max(1, Math.Min(25, (int?)args["max_depth"] ?? 22));
            var timeout = Math.Max(100, Math.Min(5000, (int?)args["timeout_ms"] ?? 1500));
            var elapsed = Stopwatch.StartNew();
            var sx = state.Value<double>("screenshot_width") / bounds.Width;
            var sy = state.Value<double>("screenshot_height") / bounds.Height;
            var cache = _automation.CreateCacheRequest(); foreach (var property in Properties) cache.AddProperty(property);
            var inspection = args["_inspection"]?.Value<bool>() == true;
            if (inspection) {
                cache.AddProperty(30009); // IsKeyboardFocusable
                foreach (var pattern in InspectionPatterns) cache.AddProperty(pattern.Property);
            }
            (JArray Elements, Dictionary<string, IElement> Lookup, JObject Details) ReadView(IWalker walker, bool rawView)
            {
                var elements = new JArray();
                var lookup = new Dictionary<string, IElement>();
                var details = new JObject();
                var pending = new Stack<(IElement Element, int Depth, int Parent, bool Web)>();
                try {
                    pending.Push((_automation.ElementFromHandleBuildCache(target.Handle, cache), 0, -1, false));
                    while (pending.Count > 0 && elements.Count < maximum && elapsed.ElapsedMilliseconds < timeout) {
                        ControlCancellation.Check(cancellation);
                        var entry = pending.Pop(); var element = entry.Element;
                        try {
                            if (entry.Depth > 0) {
                                var next = walker.NextSiblingBuildCache(element, cache);
                                if (next != null) pending.Push((next, entry.Depth, entry.Parent, entry.Web));
                            }
                            var role = Role(Convert.ToInt32(element.Cached(30003)));
                            if (rawView && role == "TitleBar") continue;
                            var index = elements.Count; var token = string.Join(".", element.GetRuntimeId());
                            var password = element.Cached(30019) is bool secret && secret;
                            var row = new JObject { ["element_index"] = index, ["element_token"] = token, ["role"] = role,
                                ["label"] = Clip(element.Cached(30005) as string ?? "", 2000), ["enabled"] = element.Cached(30010) as bool? ?? true,
                                ["focused"] = element.Cached(30008) as bool? ?? false, ["offscreen"] = element.Cached(30022) as bool? ?? false,
                                ["frame"] = Frame(Rect(element.Cached(30001))), ["in_web_content"] = entry.Web || role == "Document" };
                            if (entry.Parent >= 0) row["parent_index"] = entry.Parent;
                        if (inspection) {
                            row["focusable"] = element.Cached(30009) is true;
                            row["patterns"] = new JArray(InspectionPatterns.Where(pattern => element.Cached(pattern.Property) is true)
                                .Select(pattern => pattern.Pattern));
                        }
                            var automationId = element.Cached(30011) as string;
                            if (role == "CheckBox" && element.Cached(30086) is int toggle) { row["checked"] = toggle == 1; if (toggle == 2) row["indeterminate"] = true; }
                            if (new[] { "RadioButton", "ListItem", "TabItem", "TreeItem", "DataItem" }.Contains(role) && element.Cached(30079) is bool selected) row["selected"] = selected;
                            if ((role == "TreeItem" || role == "ComboBox") && element.Cached(30070) is int expanded && expanded != 3) row["expanded"] = expanded == 1 || expanded == 2;
                            if (!string.IsNullOrEmpty(automationId)) {
                                row["automation_id"] = automationId;
                                if (string.IsNullOrEmpty((string?)row["label"])) row["label"] = automationId;
                            }
                            var frame = (JObject)row["frame"]!;
                            row["screenshot_frame"] = new JObject { ["x"] = (frame.Value<double>("x") - bounds.X) * sx, ["y"] = (frame.Value<double>("y") - bounds.Y) * sy,
                                ["w"] = frame.Value<double>("w") * sx, ["h"] = frame.Value<double>("h") * sy };
                            if (!password) {
                                var value = element.Cached(30045) as string;
                                if (string.IsNullOrEmpty(value)) value = element.Cached(30093) as string;
                                if (!string.IsNullOrEmpty(value)) row["value"] = Clip(value!, 2000);
                            }
                            elements.Add(row); lookup[token] = element;
                            if (entry.Depth < maxDepth && (!(bool)row["offscreen"]! || maximum > 500)) {
                                var child = walker.FirstChildBuildCache(element, cache);
                                if (child != null) pending.Push((child, entry.Depth + 1, index, entry.Web || role == "Document"));
                            }
                        } catch (COMException) { details["truncated"] = true; }
                    }
                    if (pending.Count > 0) details["truncated"] = true;
                } catch (COMException error) { details["accessibility_error"] = error.Message; details["truncated"] = true; }
                return (elements, lookup, details);
            }

            bool HasLayout(JArray rows)
            {
                var caption = new HashSet<int>();
                foreach (var row in rows.OfType<JObject>()) {
                    var role = (string?)row["role"];
                    // Native minimize, maximize and close buttons are reachable,
                    // but do not describe the application's client area.
                    if (role == "TitleBar" || row.Value<int?>("parent_index") is int parent && caption.Contains(parent)) {
                        caption.Add(row.Value<int>("element_index"));
                        continue;
                    }
                    if (row["offscreen"]?.Value<bool>() != true
                        && row["frame"] is JObject frame && frame.Value<int>("w") > 0 && frame.Value<int>("h") > 0
                        && role is not ("Window" or "Pane" or "Separator" or "Image" or "Group")
                        && (role != "Custom" || !string.IsNullOrWhiteSpace((string?)row["label"]))) return true;
                }
                return false;
            }

            var view = ReadView(_automation.ControlViewWalker(), false);
            var hasLayout = HasLayout(view.Elements);
            var usedRawView = false;
            // Some providers put controls only in UIA's raw view. Share the original
            // read budget and keep the control view if raw traversal adds no layout.
            if (!hasLayout && elapsed.ElapsedMilliseconds < timeout) {
                var raw = ReadView(_automation.RawViewWalker(), true);
                if (HasLayout(raw.Elements)) {
                    view = raw;
                    hasLayout = true;
                    usedRawView = true;
                }
            }
            foreach (var property in view.Details.Properties()) state[property.Name] = property.Value;
            state["layout_status"] = hasLayout ? "controls" : "window_only";
            if (inspection) state["layout_source"] = usedRawView ? "raw" : "control";
            var elements = view.Elements;
            var query = (string?)args["query"];
            state["elements"] = string.IsNullOrWhiteSpace(query) ? elements : new JArray(elements.OfType<JObject>()
                .Where(element => ((string?)element["label"] ?? "").Contains(query!, StringComparison.OrdinalIgnoreCase)
                    || ((string?)element["value"] ?? "").Contains(query!, StringComparison.OrdinalIgnoreCase)).Select(element => element.DeepClone()));
            return view.Lookup;
        }
        private JObject Act(string method, WindowsControlTarget? target, JObject args, WaitHandle? cancellation, Action<string> stage)
        {
            ControlCancellation.Check(cancellation);
            var foreground = (string?)args["delivery_mode"] != "background";
            var window = target?.Handle ?? IntPtr.Zero;
            var compact = args["_compact_action"] as JObject;
            var visualCaptureId = (string?)args["_verify_capture_id"];
            var popup = visualCaptureId != null && target?.IsOwned == true ? target : null;
            var expected = compact?["expected_control"] as JObject;
            IElement? element = null;
            Rectangle frame = Rectangle.Empty;
            if (args["element_token"] is JValue token) {
                if (!_elements.TryGetValue(window.ToInt64(), out var known) || !known.TryGetValue((string)token!, out element)) throw new ControlException("ElementChanged", "控件已更新，请重新观察");
                try {
                    if (expected != null) frame = ValidateObservedElement(element, expected);
                    else {
                        if (element.Current(30010) is bool enabled && !enabled) throw new ControlException("ElementDisabled", "控件当前不可用");
                        frame = Rect(element.Current(30001));
                    }
                } catch (COMException) { throw new ControlException("ElementChanged", "控件已更新，请重新观察"); }
            }
            var bounds = target == null ? System.Windows.Forms.Screen.PrimaryScreen!.Bounds : Bounds(target);
            var observedBounds = args["_observed_bounds"] as JObject;
            if (observedBounds != null && !JToken.DeepEquals(Frame(bounds), observedBounds))
                throw new ControlException("CaptureExpired", "窗口位置已改变，请按新截图选择位置");
            if (method == "click" && foreground && element != null && (frame.IsEmpty || !frame.IntersectsWith(bounds))) {
                if (TryPattern<IScrollItem>(element, 10017, out var item)) {
                    ControlCancellation.Check(cancellation);
                    item!.ScrollIntoView(); frame = Rect(element.Current(30001));
                }
                if (frame.IsEmpty || !frame.IntersectsWith(bounds)) throw new ControlException("ElementNotVisible", "控件当前不可见，请滚动或展开后继续");
            }
            var point = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            if (element != null && frame.Width > 0 && frame.Height > 0) point = new Point(frame.Left + frame.Width / 2, frame.Top + frame.Height / 2);
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
            Action? nativeClick = null;
            IScroll? nativeScroll = null;
            if (!foreground && compact?.Value<bool>("prefer_native_control") == true) {
                if (method == "click" && element != null) nativeClick = FindClickAction(element, cancellation, includeAncestors: false);
                if (method == "scroll" && element != null) nativeScroll = FindScrollPattern(element, window, point, (string?)args["direction"] ?? "down");
                if (nativeClick == null && nativeScroll == null)
                    return new ControlException("ForegroundRequired", "该控件需要前台指针输入", "NotStarted").Result();
            }
            var nativeHandle = element?.Current(30020) is int handle && handle != 0 ? new IntPtr(handle) : window;
            if (foreground) WindowsNativeInput.RequireForeground(window, popup);
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
            var keyboard = method == "type_text" || method == "press_key";
            ControlCancellation.Check(cancellation);
            using var feedback = visible ? _feedback.Begin(window, indicatorPoint, Clip(label, 32), animate: !keyboard) : new WindowsActionOverlay.Feedback(null, 0);
            // Recheck geometry before delivering a click. Follow a moved element
            // once, then refuse a target that is still moving.
            if (foreground && method == "click" && element != null && args["x"] == null) {
                Point LivePoint() {
                    var current = Rect(element.Current(30001));
                    if (current.IsEmpty || current.Width <= 0 || current.Height <= 0) throw new ControlException("ElementNotVisible", "控件已隐藏，请重新观察");
                    return new Point(current.Left + current.Width / 2, current.Top + current.Height / 2);
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
            var watch = compact != null && target != null;
            string? verifiedCaret = null;
            try {
                if (observedBounds != null) {
                    var liveBounds = target == null ? System.Windows.Forms.Screen.PrimaryScreen!.Bounds : Bounds(WindowsControlTarget.ReadBounds(window));
                    if (!JToken.DeepEquals(Frame(liveBounds), observedBounds))
                        throw new ControlException("CaptureExpired", "窗口位置已改变，请按新截图选择位置");
                }
                ControlCancellation.Check(cancellation);
                if (target != null && visualCaptureId != null) {
                    stage("verify_capture");
                    try {
                        verifiedCaret = _clickVerification.RequireMatch(target, bounds, visualCaptureId,
                            new Point((int)args.Value<double>("x"), (int)args.Value<double>("y")),
                            compact?["target_region"] as JObject, compact?["verify_regions"] as JArray, cancellation);
                    } catch (Exception error) when (error is ExternalException or ArgumentException or InvalidOperationException) {
                        throw new ControlException("CaptureVerificationUnavailable", "点击前无法读取可比较的窗口像素，请在新画面中重新选点。");
                    }
                    target.Revalidate(requireGeometry: true);
                    if (foreground) WindowsNativeInput.RequirePointerTarget(window, new NativePoint { X = point.X, Y = point.Y }, includeOwnedWindows: false);
                }
                if (watch) _feedback.WatchWindowChange(target!);
                switch (method) {
                    case "click":
                        var button = (string?)args["button"] ?? "left"; var count = (int?)args["count"] ?? 1;
                        if (nativeClick != null) {
                            stage("invoke_control");
                            ControlCancellation.Check(cancellation);
                            nativeClick(); break;
                        }
                        stage("click");
                        if (element != null && button == "left" && count == 1) {
                            if (!foreground && FindClickAction(element, cancellation, includeAncestors: true) is Action invoke) {
                                ControlCancellation.Check(cancellation);
                                invoke(); break;
                            }
                            if (foreground && args["x"] == null && expected != null && ClickControl(window, element, (string?)expected["role"], cancellation)) break;
                        }
                        WindowsNativeInput.Click(window, point.X, point.Y, button, count, foreground, cancellation,
                            includeOwnedWindows: visualCaptureId == null, popup: popup); break;
                    case "type_text":
                        var text = (string?)args["text"] ?? "";
                        var postCharacters = (string?)compact?["input_method"] == "window_message";
                        if (foreground && compact != null) {
                            var failed = TypeIntoFocusedControl(window, element, text, compact, cancellation, postCharacters, stage);
                            if (failed != null) return failed;
                            break;
                        }
                        if (foreground && element != null) {
                            stage("focus");
                            ControlCancellation.Check(cancellation);
                            element.SetFocus();
                        }
                        stage("type_text");
                        if (!foreground && !postCharacters && element != null && nativeHandle == window && TryPattern<IValue>(element, 10002, out var value)) {
                            var updated = (element.Current(30045) as string ?? "") + text;
                            ControlCancellation.Check(cancellation);
                            value!.SetValue(updated); break;
                        }
                        WindowsNativeInput.Text(foreground ? window : nativeHandle, text, foreground, cancellation, postCharacters); break;
                    case "press_key":
                        if (foreground && element != null) {
                            stage("focus");
                            ControlCancellation.Check(cancellation);
                            element.SetFocus();
                        }
                        stage("press_key");
                        WindowsNativeInput.Press(foreground ? window : nativeHandle, (string?)args["key"] ?? "", (args["modifiers"] as JArray ?? new JArray()).Values<string>()!, foreground, cancellation); break;
                    case "scroll":
                        stage(nativeScroll != null ? "scroll_control" : "scroll");
                        var direction = (string?)args["direction"] ?? "down"; var amount = Math.Max(1, Math.Min(50, (int?)args["amount"] ?? 3));
                        var scroll = nativeScroll;
                        if (scroll == null && !foreground && element != null) TryPattern(element, 10004, out scroll);
                        if (foreground && compact != null && target != null)
                            scroll = FindScrollPattern(args["x"] == null ? element : null, window, point, direction);
                        if (scroll != null) {
                            for (var i = 0; i < amount; i++) {
                                if (foreground) {
                                    try { WindowsNativeInput.RequireForeground(window); }
                                    catch (ControlException error) when (i > 0) { throw new ControlException(error.Code, error.Message, "Unknown"); }
                                }
                                ControlCancellation.Check(cancellation, i > 0);
                                scroll.Scroll(direction == "left" ? 1 : direction == "right" ? 4 : 2, direction == "up" ? 1 : direction == "down" ? 4 : 2);
                            }
                            break;
                        }
                        WindowsNativeInput.Scroll(window, point.X, point.Y, direction, amount, foreground, cancellation); break;
                    default: throw new ControlException("UnknownTool", "不支持的原生操作：" + method);
                }
                feedback.Complete();
                var outcome = new JObject { ["completed"] = true };
                if (visualCaptureId != null) outcome["visual_verification"] = "matched";
                if (verifiedCaret != null) outcome["visual_verification_caret"] = verifiedCaret;
                if (watch) {
                    stage("wait_for_change");
                    var wait = Stopwatch.StartNew();
                    outcome["_ui_change_observed"] = _feedback.WaitForWindowChange(140, cancellation);
                    outcome["_change_wait_ms"] = wait.ElapsedMilliseconds;
                    outcome["_state_waited"] = true;
                }
                return ControlJson.Result(outcome);
            } finally {
                if (watch) _feedback.EndWindowWatch();
            }
        }
        private Rectangle ValidateObservedElement(IElement element, JObject expected)
        {
            var cache = _automation.CreateCacheRequest();
            foreach (var property in new[] { 30001, 30003, 30005, 30010, 30011 }) cache.AddProperty(property);
            var live = element.BuildUpdatedCache(cache);
            if (live.Cached(30010) is bool enabled && !enabled)
                throw new ControlException("ElementDisabled", "控件当前不可用");
            var role = Role(Convert.ToInt32(live.Cached(30003)));
            var automationId = live.Cached(30011) as string ?? "";
            var label = Clip(live.Cached(30005) as string ?? "", 2000);
            if (label.Length == 0) label = automationId;
            var frame = Rect(live.Cached(30001));
            if (role != (string?)expected["role"] || automationId != ((string?)expected["automation_id"] ?? "")
                || label != (string?)expected["label"] || !JToken.DeepEquals(Frame(frame), expected["frame"]))
                throw new ControlException("ElementChanged", "控件身份或位置已改变，请使用最新界面选择");
            return frame;
        }

        private static bool ClickControl(IntPtr window, IElement element, string? role, WaitHandle? cancellation)
        {
            if ((role == "Button" || role == "Hyperlink" || role == "MenuItem") && TryPattern<IInvoke>(element, 10000, out var invoke)) {
                WindowsNativeInput.RequireForeground(window);
                ControlCancellation.Check(cancellation);
                invoke!.Invoke(); return true;
            }
            if (role == "CheckBox" && TryPattern<IToggle>(element, 10015, out var toggle)) {
                WindowsNativeInput.RequireForeground(window);
                ControlCancellation.Check(cancellation);
                toggle!.Toggle(); return true;
            }
            if ((role == "RadioButton" || role == "TabItem") && TryPattern<ISelectionItem>(element, 10010, out var selection)) {
                WindowsNativeInput.RequireForeground(window);
                ControlCancellation.Check(cancellation);
                selection!.Select(); return true;
            }
            return false;
        }

        private IScroll? FindScrollPattern(IElement? element, IntPtr window, Point point, string direction)
        {
            try {
                if (element == null) {
                    var hit = new NativePoint { X = point.X, Y = point.Y };
                    WindowsNativeInput.RequirePointerTarget(window, hit);
                    element = _automation.ElementFromPoint(hit);
                    if (!BelongsTo(element, window)) return null;
                }
                var axis = direction == "left" || direction == "right" ? 30057 : 30058;
                var walker = _automation.RawViewWalker();
                for (var depth = 0; element != null && depth < 12; depth++) {
                    if (TryPattern<IScroll>(element, 10004, out var pattern) && element.Current(axis) is true) return pattern;
                    if (Convert.ToInt32(element.Current(30003)) == 50032) break;
                    element = walker.Parent(element);
                }
            } catch (COMException) { }
            return null;
        }

        private JObject? TypeIntoFocusedControl(IntPtr window, IElement? field, string text, JObject options, WaitHandle? cancellation,
            bool postCharacters, Action<string> stage)
        {
            if (text.Length > 65536) throw new ControlException("InvalidArguments", "一次输入最多 65536 个字符");
            var step = "focus";
            var dispatched = false;
            var selectionSent = false;
            var textSent = false;
            try {
                stage(step);
                ControlCancellation.Check(cancellation);
                var replace = options["replace"]?.Value<bool>() == true;
                var submit = options["submit"]?.Value<bool>() == true;
                // Windows can service posted WM_CHAR messages ahead of keyboard
                // input. Keep select/insert/submit observable as separate calls
                // for editors which require the window-message text path.
                if (postCharacters && text.Length > 0 && (replace || submit))
                    throw new ControlException("TextSequenceUnavailable", "窗口字符消息需要分步执行。用 press_key 全选，再单独调用 type_text；核对文字后再提交。");
                WindowsNativeInput.RequireForeground(window);
                var focus = ReadFocusedElement();
                if (field != null && !IsWithinControl(focus, field)) {
                    ControlCancellation.Check(cancellation);
                    field.SetFocus();
                    focus = ReadFocusedElement();
                    if (!IsWithinControl(focus, field))
                        throw new ControlException("FocusChanged", "输入框未取得键盘焦点，请重新观察");
                }
                if (focus != null && window != IntPtr.Zero && !BelongsTo(focus, window)) focus = null;
                var boundFocus = focus ?? field;
                void CheckFocus() {
                    ControlCancellation.Check(cancellation, dispatched);
                    WindowsNativeInput.RequireForeground(window);
                    if (boundFocus != null && !IsWithinControl(ReadFocusedElement(), boundFocus))
                        throw new ControlException("FocusChanged", "键盘焦点已变化，请重新观察");
                }
                if (replace) {
                    step = "replace";
                    stage(step);
                    CheckFocus();
                    WindowsNativeInput.Press(window, "a", new[] { "ctrl" }, true, cancellation);
                    selectionSent = dispatched = true;
                }
                step = "type_text";
                stage(step);
                CheckFocus();
                if (replace && text.Length == 0) WindowsNativeInput.Press(window, "backspace", Array.Empty<string>(), true, cancellation);
                else WindowsNativeInput.Text(window, text, true, cancellation, postCharacters);
                textSent = dispatched = true;
                if (submit) {
                    step = "submit";
                    stage(step);
                    CheckFocus();
                    WindowsNativeInput.Press(window, "return", Array.Empty<string>(), true, cancellation);
                }
                return null;
            } catch (Exception error) {
                var failure = error as ControlException ?? new ControlException("NativeOperationFailed", error.Message);
                if (dispatched && failure.ExecutionState == "NotStarted")
                    failure = new ControlException(failure.Code, failure.Message, "Unknown");
                var result = failure.Result();
                result["structuredContent"]!["error"]!["step"] = step;
                if (selectionSent) result["structuredContent"]!["error"]!["selection_sent"] = true;
                if (textSent) result["structuredContent"]!["error"]!["text_sent"] = true;
                return result;
            }
        }
        private IElement? ReadFocusedElement()
        {
            try { return _automation.GetFocusedElement(); }
            catch (COMException) { return null; }
        }
        private bool IsWithinControl(IElement? focus, IElement expected)
        {
            var walker = _automation.RawViewWalker();
            for (var depth = 0; focus != null && depth < 8; depth++) {
                if (_automation.CompareElements(focus, expected)) return true;
                focus = walker.Parent(focus);
            }
            return false;
        }
        public void Dispose() => _feedback.Dispose();
        private Action? FindClickAction(IElement element, WaitHandle? cancellation, bool includeAncestors)
        {
            var walker = _automation.ControlViewWalker();
            for (var depth = 0; depth < 8 && element != null; depth++) {
                ControlCancellation.Check(cancellation);
                if (Convert.ToInt32(element.Current(30003)) == 50032) break;
                if (TryPattern<IInvoke>(element, 10000, out var invoke)) return invoke!.Invoke;
                if (TryPattern<IToggle>(element, 10015, out var toggle)) return toggle!.Toggle;
                if (TryPattern<ISelectionItem>(element, 10010, out var select)) return select!.Select;
                if (!includeAncestors) break;
                element = walker.Parent(element);
            }
            return null;
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
        private static Rectangle Bounds(WindowsControlTarget target) => Bounds(target.Bounds);
        private static Rectangle Bounds(WindowsControlTarget.Rect bounds) => Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        private static Rectangle Rect(object value) { if (value is double[] a && a.Length == 4) return new Rectangle((int)Math.Round(a[0]), (int)Math.Round(a[1]), Math.Max(0, (int)Math.Round(a[2])), Math.Max(0, (int)Math.Round(a[3]))); return Rectangle.Empty; }
        private static JObject Frame(Rectangle rectangle) => new JObject { ["x"] = rectangle.Left, ["y"] = rectangle.Top, ["w"] = rectangle.Width, ["h"] = rectangle.Height };
        private static string Clip(string value, int maximum) => value.Length <= maximum ? value : value.Substring(0, maximum - (char.IsHighSurrogate(value[maximum - 1]) ? 1 : 0));
        private static readonly string[] Roles = { "Button", "Calendar", "CheckBox", "ComboBox", "Edit", "Hyperlink", "Image", "ListItem", "List", "Menu", "MenuBar", "MenuItem", "ProgressBar", "RadioButton", "ScrollBar", "Slider", "Spinner", "StatusBar", "Tab", "TabItem", "Text", "ToolBar", "ToolTip", "Tree", "TreeItem", "Custom", "Group", "Thumb", "DataGrid", "DataItem", "Document", "SplitButton", "Window", "Pane", "Header", "HeaderItem", "Table", "TitleBar", "Separator", "SemanticZoom", "AppBar" };
        private static string Role(int id) => id >= 50000 && id < 50000 + Roles.Length ? Roles[id - 50000] : "Custom";
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    }
}
