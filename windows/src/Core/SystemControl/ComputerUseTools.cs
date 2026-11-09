#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YTray.Models;
using YTray.Native;

namespace YTray.Core.SystemControl
{
    public sealed partial class SystemControlController
    {
        private readonly SemaphoreSlim _interaction = new SemaphoreSlim(1, 1);
        private static JObject Property(string type, string description = "") => new JObject { ["type"] = type, ["description"] = description };
        private JArray PublicTools()
        {
            JObject Tool(string name, string description, JObject properties, params string[] required) => new JObject {
                ["name"] = name, ["description"] = description,
                ["inputSchema"] = new JObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JArray(required), ["additionalProperties"] = false },
                ["annotations"] = new JObject { ["readOnlyHint"] = name.StartsWith("list_", StringComparison.Ordinal) || name.StartsWith("get_", StringComparison.Ordinal) || name == "system_status" }
            };
            JObject Window() => new JObject { ["window_id"] = Property("integer", "Window ID from list_windows or the previous action. 0 means the primary desktop.") };
            JObject Input() { var p = Window(); p["delivery_mode"] = new JObject { ["type"] = "string", ["enum"] = new JArray("foreground", "background"), ["description"] = "Default foreground: activate and keep the target in front. Background is optional." }; p["include_screenshot"] = Property("boolean", "Default true. False returns fresh numbered controls without an image."); return p; }
            var read = Window(); read["include_screenshot"] = Property("boolean", "Default true."); read["query"] = Property("string", "Optional text filter for the controls.");
            var activate = Window(); activate["include_screenshot"] = Property("boolean", "Default true. False returns fresh controls and focus without an image.");
            read["max_image_dimension"] = new JObject { ["type"] = "integer", ["minimum"] = 100, ["maximum"] = 4096 };
            read["detail"] = new JObject { ["type"] = "string", ["enum"] = new JArray("compact", "full"), ["description"] = "Default compact: visible controls and short text. Use query for a specific control or full for the complete tree." };
            read["region"] = new JObject { ["type"] = "object", ["description"] = "Read only controls/text intersecting this rectangle in window-image pixels (same max_image_dimension, default 1400). Defaults to no screenshot. No prior capture is required.",
                ["properties"] = new JObject { ["x"] = new JObject { ["type"] = "number", ["minimum"] = 0 }, ["y"] = new JObject { ["type"] = "number", ["minimum"] = 0 },
                    ["width"] = new JObject { ["type"] = "number", ["minimum"] = 0.01 }, ["height"] = new JObject { ["type"] = "number", ["minimum"] = 0.01 } },
                ["required"] = new JArray("x", "y", "width", "height"), ["additionalProperties"] = false };
            var click = Input(); click["element_index"] = Property("integer", "Control number [N] in the latest returned state. Or provide x and y from its image.");
            click["x"] = Property("number"); click["y"] = Property("number"); click["button"] = new JObject { ["type"] = "string", ["enum"] = new JArray("left", "right", "middle") };
            click["count"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 3 };
            var type = Input(); type["text"] = Property("string", "Text to insert into the focused field."); type["element_index"] = Property("integer", "Optional control to focus first.");
            type["replace"] = Property("boolean", "Select all in the focused field before typing; default false.");
            type["submit"] = Property("boolean", "Press Enter after successful typing and return only the final state; default false.");
            var key = Input(); key["key"] = Property("string", "Key or chord, e.g. ctrl+l, ctrl+t, enter, ctrl+a.");
            var scroll = Input(); scroll["direction"] = new JObject { ["type"] = "string", ["enum"] = new JArray("up", "down", "left", "right") };
            scroll["amount"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 50, ["description"] = "Scroll steps; default 3." };
            scroll["element_index"] = Property("integer", "Optional scrollable control."); scroll["x"] = Property("number"); scroll["y"] = Property("number");
            var tools = new JArray {
                Tool("list_apps", "Find installed or running desktop apps. Use an app name or EXE with launch_app.", new JObject { ["query"] = Property("string", "Optional app name filter.") }),
                Tool("list_windows", "List open windows. Actions need only window_id; no session setup or request IDs.", new JObject { ["app"] = Property("string", "Optional app name or EXE filter.") }),
                Tool("get_window_state", "Read compact controls with focus/selection summary. Same-destination links appear once. Use region for local text, query for a control, or detail:full for every alias. Actions already return fresh state automatically.", read, "window_id"),
                Tool("get_desktop_state", "Read the primary desktop. Use window_id:0 for desktop coordinates.", new JObject { ["max_image_dimension"] = new JObject { ["type"] = "integer", ["minimum"] = 100, ["maximum"] = 4096 } }),
                Tool("system_status", "Check the computer control connection; stale connections recover automatically.", new JObject())
            };
            if (_grant?.ReadOnly != true) {
                tools.Add(Tool("launch_app", "Open an app, activate its window, and return its latest state. For browsers, pass url to navigate in this same call.", new JObject { ["app"] = Property("string", "Installed app name, e.g. chrome, notepad, or an EXE path."), ["new_window"] = Property("boolean", "Default true for browsers."), ["url"] = Property("string", "Optional http/https URL to open in a browser."), ["include_screenshot"] = Property("boolean", "Default true.") }, "app"));
                tools.Add(Tool("activate_window", "Bring a window to the front, restore it if minimized, and return its latest state.", activate, "window_id"));
                tools.Add(Tool("click", "Click a numbered control or image coordinates. Returns the updated app window and screenshot.", click, "window_id"));
                tools.Add(Tool("type_text", "Focus an optional numbered field and type. replace:true clears existing text; submit:true presses Enter. One call returns only the final updated state.", type, "window_id", "text"));
                tools.Add(Tool("press_key", "Send a key or chord to a window. No control number is needed. Returns updated state.", key, "window_id", "key"));
                tools.Add(Tool("scroll", "Scroll the window or a numbered control. Returns updated state.", scroll, "window_id", "direction"));
            }
            return tools;
        }

        private async Task MaintainConnectionAsync()
        {
            if (!IsEnabled || _disposed) return;
            if (!IsRunning) { await EnsureConnectedAsync().ConfigureAwait(false); return; }
            if (!await _operation.WaitAsync(0).ConfigureAwait(false)) return;
            var generation = _generation;
            // Metadata checks exercise the transport without a UIA/desktop permission
            // scan competing with foreground input. Expired native contexts are repaired
            // on the next read or action by CallNativeAsync.
            try { await _connection!.RequestAsync("tools/list", new JObject(), _lifetime!.Token, 3000).ConfigureAwait(false); }
            catch { if (generation == _generation && IsEnabled) StopRuntime("正在恢复控制连接。", "Reconnecting"); }
            finally { _operation.Release(); }
        }
        private async Task ReadyAsync()
        {
            if (!IsEnabled || _grant == null) throw new ControlException("ControlDisabled", "电脑控制已关闭，请在 YTray 开启");
            if (!IsRunning) await EnsureConnectedAsync().ConfigureAwait(false);
            var timer = Stopwatch.StartNew();
            while (IsEnabled && !IsRunning && Phase != "Paused" && timer.Elapsed < TimeSpan.FromSeconds(12)) await Task.Delay(100).ConfigureAwait(false);
            if (!IsRunning) throw new ControlException("DesktopUnavailable", Phase == "Paused" ? "请解锁桌面后继续" : "控制连接尚未就绪，请稍后重试");
        }
        private async Task<JObject> CallSimpleAsync(string name, JObject arguments)
        {
            var definition = PublicTools().OfType<JObject>().FirstOrDefault(t => (string?)t["name"] == name)
                ?? throw new ControlException("UnknownTool", "未找到工具，请重新读取 tools/list");
            ControlJson.Validate(arguments, (JObject)definition["inputSchema"]!);
            await _interaction.WaitAsync().ConfigureAwait(false);
            var feedbackId = Interlocked.Increment(ref _feedbackSequence);
            var shown = false; var ended = false;
            try {
                if (HasOperationFeedback(name)) {
                    try {
                        await ReadyAsync().ConfigureAwait(false);
                        var feedback = DescribeOperation(name, arguments);
                        shown = await SendOperationFeedbackAsync(feedbackId, "prepare", feedback.Window, feedback.Description, feedback.Context).ConfigureAwait(false);
                        if (shown) {
                            await Task.Delay(300).ConfigureAwait(false);
                            await SendOperationFeedbackAsync(feedbackId, "running").ConfigureAwait(false);
                        }
                    } catch { /* Feedback cannot deny or replay an input. */ }
                }
                var result = await CallSimpleCoreAsync(name, arguments).ConfigureAwait(false);
                if (shown) {
                    var failed = result["isError"]?.Value<bool>() == true;
                    await SendOperationFeedbackAsync(feedbackId, failed ? "failed" : "completed", (long?)result["structuredContent"]?["window_id"] ?? 0,
                        context: failed ? (string?)result["structuredContent"]?["error"]?["message"] ?? "请查看操作结果" : (string?)result["structuredContent"]?["title"] ?? "").ConfigureAwait(false);
                    ended = true;
                    // One readable completion beat per public call, never per
                    // internal Ctrl+A/type/Enter step. The remaining dwell is async.
                    await Task.Delay(900).ConfigureAwait(false);
                }
                return result;
            } finally {
                if (shown && !ended) await SendOperationFeedbackAsync(feedbackId, "failed", context: "操作已中断，请查看最新界面").ConfigureAwait(false);
                _interaction.Release();
            }
        }
        private async Task<JObject> CallSimpleCoreAsync(string name, JObject arguments)
        {
            WindowsControlTarget? target = null;
            try {
                await ReadyAsync().ConfigureAwait(false);
                if (name == "system_status") {
                    return ControlJson.Result(new JObject { ["enabled"] = IsEnabled, ["state"] = Phase, ["backend"] = "windows-native", ["message"] = StatusText,
                        ["version"] = YTrayBuildInfo.Version, ["build_id"] = YTrayBuildInfo.BuildId, ["worker_build_id"] = _connection?.ServerInfo["build_id"], ["worker_pid"] = WorkerProcessId });
                }
                if (name == "list_apps") {
                    var windows = CurrentWindows(); var query = (string?)arguments["query"] ?? "";
                    var installed = WindowsApplicationControl.Installed();
                    foreach (var window in windows) if (!installed.Any(a => WindowsApplicationControl.SamePath(a.Path, window.Executable)))
                        installed.Add(new WindowsApplicationControl.App { Name = WindowsApplicationControl.DisplayName(window.Executable), Path = window.Executable });
                    var matches = installed.Where(a => query.Length == 0 || a.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || a.Id.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
                    return ControlJson.Result(new JObject { ["apps"] = new JArray(matches.Take(100).Select(a => a.Json(windows))), ["has_more"] = matches.Length > 100 });
                }
                if (name == "list_windows") {
                    var filter = (string?)arguments["app"] ?? "";
                    var windows = CurrentWindows().Where(w => filter.Length == 0 || w.Executable.Contains(filter, StringComparison.OrdinalIgnoreCase) || w.Title.Contains(filter, StringComparison.OrdinalIgnoreCase));
                    // Keep the v1 alias in structured lists for installed clients. New schemas
                    // and every new action use the ordinary, stable HWND window_id instead.
                    return ControlJson.Result(new JObject { ["windows"] = new JArray(windows.Select(w => { var item = WindowsApplicationControl.WindowJson(w); item["target_ref"] = w.Reference; return item; })), ["desktop_window_id"] = 0 });
                }
                var windowId = (long?)arguments["window_id"] ?? 0;
                if (name == "get_window_state" && windowId == 0 && arguments["region"] != null)
                    throw new ControlException("InvalidRegion", "region 用于应用窗口，请提供 list_windows 返回的 window_id");
                if (name == "launch_app") {
                    if (_grant!.AllowForeground != true) throw new ControlException("BackgroundUnavailable", "启动应用需要前台操作模式");
                    var app = await Task.Run(() => WindowsApplicationControl.Find((string)arguments["app"]!)).ConfigureAwait(false);
                    target = await WindowsApplicationControl.LaunchAsync(app, arguments["new_window"]?.Value<bool>() ?? true, (string?)arguments["url"]).ConfigureAwait(false);
                    target = RegisterTarget(target.Handle.ToInt64());
                    var browser = new[] { "chrome", "msedge", "chromium", "firefox" }.Contains(app.Id);
                    return await UpdatedAsync(target, "应用已打开并切到前台", browser, "", arguments).ConfigureAwait(false);
                }
                if (name != "get_desktop_state" && windowId != 0) target = RegisterTarget(windowId);
                if (name == "get_window_state" || name == "get_desktop_state")
                    return await PublicStateAsync(await ObserveSimpleAsync(target, arguments).ConfigureAwait(false), target, options: arguments).ConfigureAwait(false);
                if (name == "activate_window") {
                    if (target == null) throw new ControlException("InvalidArguments", "请选择应用窗口");
                    if (_grant!.AllowForeground != true) throw new ControlException("BackgroundUnavailable", "当前模式不切换前台窗口");
                    await WindowsApplicationControl.ActivateAsync(target).ConfigureAwait(false);
                    return await UpdatedAsync(target, "窗口已切到前台", options: arguments).ConfigureAwait(false);
                }
                return await ActSimpleAsync(name, arguments, target).ConfigureAwait(false);
            } catch (ControlException error) {
                var result = error.Result();
                if (target != null && IsRunning) try {
                    var state = await UpdatedAsync(target, error.Message).ConfigureAwait(false);
                    state["isError"] = true; state["structuredContent"]!["error"] = result["structuredContent"]!["error"]!.DeepClone(); return state;
                } catch { }
                return result;
            }
        }
        private WindowsControlTarget[] CurrentWindows()
        {
            var windows = WindowsControlTarget.Enumerate().Where(Allowed).ToArray();
            lock (_sync) foreach (var target in windows) {
                var previous = _targets.Values.FirstOrDefault(t => t.Handle == target.Handle && t.Pid == target.Pid && t.Created == target.Created);
                if (previous != null) target.Reference = previous.Reference;
                _targets[target.Reference] = target;
            }
            return windows;
        }
        private WindowsControlTarget RegisterTarget(long id)
        {
            var target = WindowsControlTarget.Resolve(new IntPtr(id));
            if (!Allowed(target)) throw new ControlException("TargetUnavailable", "这个窗口当前不可操作，请 list_windows 选择目标");
            lock (_sync) {
                var oldHandle = _targets.Values.FirstOrDefault(t => t.Handle == target.Handle);
                if (oldHandle != null && (oldHandle.Pid != target.Pid || oldHandle.Created != target.Created)) throw new ControlException("TargetChanged", "原窗口已关闭，请从最新窗口列表选择目标");
                var previous = _targets.Values.FirstOrDefault(t => t.Handle == target.Handle && t.Pid == target.Pid && t.Created == target.Created);
                if (previous != null) target.Reference = previous.Reference;
                _targets[target.Reference] = target;
            }
            return target;
        }
        private async Task<JObject> ObserveSimpleAsync(WindowsControlTarget? target, JObject? options = null)
        {
            for (var attempt = 0; ; attempt++) {
                await ReadyAsync().ConfigureAwait(false);
                if (target != null) target = RegisterTarget(target.Handle.ToInt64());
                var args = new JObject { ["max_image_dimension"] = (int?)options?["max_image_dimension"] ?? 1400 };
                if (target != null) {
                    args["target_ref"] = target.Reference; args["include_screenshot"] = options?["include_screenshot"]?.Value<bool>() ?? options?["region"] == null;
                    args["max_elements"] = (string?)options?["detail"] == "full" ? 1000 : 500;
                    args["max_depth"] = 22; args["timeout_ms"] = 1500;
                    args["tree_format"] = options?["_controls_only"]?.Value<bool>() == true ? "elements" : "both";
                    if (options?["query"] != null) args["query"] = options["query"]!.DeepClone();
                }
                try {
                    var result = await CallLegacyAsync(target == null ? "get_desktop_state" : "get_window_state", args, waitForOperation: true).ConfigureAwait(false);
                    if (result["isError"]?.Value<bool>() == true && attempt == 0 && (!IsRunning || IsConnectionFailure(result))) {
                        if (IsRunning) StopRuntime("正在恢复控制连接。", "Reconnecting");
                        continue;
                    }
                    return result;
                } catch (ControlException error) when (attempt == 0 && (!IsRunning || error.Code == "RuntimeGenerationMismatch")) { }
            }
        }
        private static bool IsConnectionFailure(JObject result)
        {
            var code = (string?)result["structuredContent"]?["error"]?["code"];
            return code == "DriverUnavailable" || code == "DriverFailure" || code == "RuntimeGenerationMismatch" || code == "DriverTimeout";
        }
        private async Task<JObject> UpdatedAsync(WindowsControlTarget? target, string message, bool waitForNavigation = false, string? oldTitle = null, JObject? options = null)
        {
            if (target == null) try { target = RegisterTarget(WindowsApplicationControl.ForegroundWindow); } catch { }
            if (target != null) {
                // Navigation often starts after key delivery returns. Wait briefly for its
                // first UI update instead of returning the page from before Enter.
                await Task.Delay(180).ConfigureAwait(false);
                try { target = RegisterTarget(target.Handle.ToInt64()); }
                catch (ControlException) {
                    var remaining = CurrentWindows().Where(w => WindowsApplicationControl.SamePath(w.Executable, target.Executable)).ToArray();
                    if (remaining.Length == 0) return ControlJson.Result(new JObject { ["message"] = message, ["windows"] = new JArray(), ["window_closed"] = true });
                    target = remaining[0];
                }
            }
            var navigationOptions = options == null ? new JObject() : (JObject)options.DeepClone();
            if (waitForNavigation) { navigationOptions["include_screenshot"] = false; navigationOptions["_controls_only"] = true; }
            var raw = await ObserveSimpleAsync(target, waitForNavigation ? navigationOptions : options).ConfigureAwait(false);
            if (waitForNavigation && target != null) {
                var waiting = Stopwatch.StartNew();
                string? lastReady = null;
                while (waiting.Elapsed < TimeSpan.FromSeconds(3)) {
                    if (raw["isError"]?.Value<bool>() == true) break;
                    var view = raw["structuredContent"] as JObject;
                    var elements = view?["elements"] as JArray ?? new JArray();
                    var loading = elements.Any(e => (string?)e["label"] is string label && (label == "停止" || label.Contains("停止加载", StringComparison.Ordinal)
                        || label.StartsWith("Stop loading", StringComparison.OrdinalIgnoreCase)));
                    var title = (string?)view?["window_title"] ?? "";
                    var selectedTab = elements.OfType<JObject>().FirstOrDefault(e => string.Equals((string?)e["role"], "TabItem", StringComparison.OrdinalIgnoreCase)
                        && e["in_web_content"]?.Value<bool>() != true && e["selected"]?.Value<bool>() == true);
                    var tabTitle = (string?)selectedTab?["label"];
                    // Chrome can update its title to the destination URL before the new
                    // document appears. That intermediate frame is not useful for the next action.
                    var documentReady = elements.Any(e => string.Equals((string?)e["role"], "document", StringComparison.OrdinalIgnoreCase) && (string?)e["label"] is string label
                        && label.Length > 0 && title.StartsWith(label, StringComparison.Ordinal)
                        && (string.IsNullOrEmpty(tabTitle) || tabTitle!.StartsWith(label, StringComparison.Ordinal) || label.StartsWith(tabTitle!, StringComparison.Ordinal))
                        && MatchesDestination((string?)e["value"], (string?)options?["url"]));
                    var stamp = title + "\n" + elements.Count + "\n" + string.Join("\n", elements.Take(80).Select(e => (string?)e["role"] + ":" + (string?)e["label"]));
                    if (!loading && documentReady) {
                        if (lastReady == stamp && waiting.ElapsedMilliseconds >= (title == oldTitle ? 700 : 350)) break;
                        lastReady = stamp;
                    } else lastReady = null;
                    await Task.Delay(120).ConfigureAwait(false); raw = await ObserveSimpleAsync(target, navigationOptions).ConfigureAwait(false);
                }
                // Poll only text while a page settles; capture one image for the final
                // response rather than PNG-encoding every intermediate loading frame.
                raw = await ObserveSimpleAsync(target, options).ConfigureAwait(false);
            }
            // The native input backend restores its previous foreground after
            // delivery. Public foreground actions intentionally leave the chosen
            // app active, including after a delayed native restoration.
            if (target != null && ((string?)options?["delivery_mode"] ?? (_grant?.AllowForeground == true ? "foreground" : "background")) == "foreground")
                await WindowsApplicationControl.ActivateAsync(target).ConfigureAwait(false);
            return await PublicStateAsync(raw, target, message, options).ConfigureAwait(false);
        }
        private static bool MatchesDestination(string? actual, string? expected)
        {
            if (expected == null) return true;
            if (!Uri.TryCreate(actual, UriKind.Absolute, out var current) || !Uri.TryCreate(expected, UriKind.Absolute, out var requested)) return false;
            static string Host(Uri uri) => uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host.Substring(4) : uri.Host;
            return Host(current).Equals(Host(requested), StringComparison.OrdinalIgnoreCase)
                && (requested.AbsolutePath == "/" || current.AbsolutePath.StartsWith(requested.AbsolutePath, StringComparison.Ordinal));
        }
        private async Task<JObject> PublicStateAsync(JObject raw, WindowsControlTarget? target, string? message = null, JObject? options = null)
        {
            var source = raw["structuredContent"] as JObject ?? new JObject();
            var state = new JObject { ["window_id"] = target?.Handle.ToInt64() ?? 0, ["title"] = (string?)source["window_title"] ?? target?.Title ?? "Desktop" };
            if (message != null) state["message"] = message;
            foreach (var field in new[] { "window_bounds", "screenshot_width", "screenshot_height", "image_width", "image_height", "error" })
                if (source[field] != null) state[field] = source[field]!.DeepClone();
            var full = (string?)options?["detail"] == "full";
            var region = raw["isError"]?.Value<bool>() == true ? null : ControlRegion.Create(source, options);
            // Read focus once at the final public boundary, after foreground
            // restoration; internal input preflights and loading polls skip it.
            if (target != null && raw["isError"]?.Value<bool>() != true)
                source["focus"] = await ReadNativeContextAsync(target, source, region).ConfigureAwait(false);
            var context = ControlContext.Format(source, full, options?["query"] != null, region, (string?)options?["query"]);
            state["controls_shown"] = context.Shown; state["controls_total"] = context.Total; state["truncated"] = context.Truncated;
            if (context.Focus != null) state["focus"] = context.Focus;
            if (context.DuplicateLinksOmitted > 0) state["duplicate_links_omitted"] = context.DuplicateLinksOmitted;
            if (region != null) { state["region"] = region.ImageBounds; state["coordinate_width"] = region.ImageWidth; state["coordinate_height"] = region.ImageHeight; }
            // Full is an explicit compatibility/inspection escape hatch. The default
            // response carries the control text once, readable by every MCP client.
            if (full) state["tree_markdown"] = context.Text;
            state["windows"] = new JArray(CurrentWindows().Where(w => target == null || WindowsApplicationControl.SamePath(w.Executable, target.Executable)).Select(WindowsApplicationControl.WindowJson));
            var text = (message == null ? "" : message + "\n") + "window_id: " + state["window_id"] + " · " + state["title"] + "\n"
                + (context.FocusText.Length == 0 ? "" : context.FocusText + "\n") + (region == null ? "" : "Region: " + region.ImageBounds.ToString(Formatting.None) + "\n") + context.Text;
            if (context.Truncated) text += region == null ? "\nMore: get_window_state(query:\"control text\") or detail:\"full\"." : "\nMore: narrow the region, add query, or use detail:\"full\".";
            var content = new JArray(new JObject { ["type"] = "text", ["text"] = text.Trim() });
            foreach (var image in (raw["content"] as JArray ?? new JArray()).Where(c => (string?)c["type"] == "image")) content.Add(image.DeepClone());
            return new JObject { ["isError"] = raw["isError"]?.Value<bool>() ?? false, ["content"] = content, ["structuredContent"] = state };
        }
        private async Task<JObject> ReadNativeContextAsync(WindowsControlTarget target, JObject source, ControlRegion? region)
        {
            try {
                var args = new JObject { ["window_id"] = target.Handle.ToInt64() };
                if (region != null) {
                    var frame = region.ScreenBounds;
                    args["region"] = new JObject { ["x"] = frame.X, ["y"] = frame.Y, ["w"] = frame.Width, ["h"] = frame.Height };
                    args["controls"] = source["elements"]?.DeepClone();
                }
                var connection = _connection;
                if (connection != null) return await connection.RequestAsync("read_context", args, _lifetime?.Token ?? CancellationToken.None, 1500).ConfigureAwait(false);
            } catch { }
            return new JObject { ["status"] = "unavailable", ["region_text_unavailable"] = region != null };
        }
        private async Task<JObject> ActSimpleAsync(string name, JObject input, WindowsControlTarget? target)
        {
            var mode = (string?)input["delivery_mode"] ?? (_grant!.AllowForeground ? "foreground" : "background");
            var replace = name == "type_text" && input["replace"]?.Value<bool>() == true;
            var submit = name == "type_text" && input["submit"]?.Value<bool>() == true;
            if ((replace || submit) && mode != "foreground") throw new ControlException("InvalidArguments", "replace / submit 组合输入需要 foreground 模式");
            var reference = target?.Reference ?? "desktop_primary";
            Observation? previous; lock (_sync) _observations.TryGetValue(reference, out previous);
            JObject? selected = null;
            if (input["element_index"] != null) {
                selected = (previous?.View["elements"] as JArray)?.OfType<JObject>().FirstOrDefault(e => (int?)e["element_index"] == (int)input["element_index"]!);
                if (selected == null) throw new ControlException("ElementChanged", "请使用最新返回界面的控件编号");
            }
            if (mode == "foreground" && target != null) { await WindowsApplicationControl.ActivateAsync(target).ConfigureAwait(false); target = RegisterTarget(target.Handle.ToInt64()); }
            var coordinates = input["x"] != null || input["y"] != null;
            if (coordinates && (previous == null || previous.Width <= 0)) throw new ControlException("ScreenshotRequired", "请先获取带截图的窗口状态，再选择图片坐标");
            var options = new JObject { ["include_screenshot"] = coordinates || name == "scroll" && selected == null, ["_controls_only"] = true };
            if (previous?.Width > 0) options["max_image_dimension"] = Math.Max(previous.Width, previous.Height);
            JObject fresh;
            if (target != null && selected == null && input["x"] == null && previous != null && previous.Generation == _generation
                && previous.Target != null && previous.Target.Bounds.Equals(target.Bounds) && Stopwatch.GetTimestamp() - previous.Ticks < Stopwatch.Frequency * 25L
                && (name == "press_key" || name == "type_text")) {
                var cached = (JObject)previous.View.DeepClone(); cached["observation_id"] = previous.Id;
                cached["target_ref"] = reference; cached["image_width"] = previous.Width; cached["image_height"] = previous.Height;
                fresh = ControlJson.Result(cached);
            } else fresh = await ObserveSimpleAsync(target, options).ConfigureAwait(false);
            if (fresh["isError"]?.Value<bool>() == true) return await PublicStateAsync(fresh, target).ConfigureAwait(false);
            var view = (JObject)fresh["structuredContent"]!;
            var args = new JObject { ["target_ref"] = view["target_ref"] ?? reference, ["observation_id"] = view["observation_id"],
                ["request_id"] = Guid.NewGuid().ToString(), ["deadline"] = DateTimeOffset.UtcNow.AddSeconds(25).ToString("O"), ["delivery_mode"] = mode };
            foreach (var field in new[] { "x", "y", "button", "count", "text", "direction", "amount" }) if (input[field] != null) args[field] = input[field]!.DeepClone();
            if (input["x"] != null && previous?.Width > 0) {
                if (previous.Target != null && target != null && !previous.Target.Bounds.Equals(target.Bounds)) throw new ControlException("ElementChanged", "窗口大小或位置已改变，已刷新界面，请按新截图选择位置");
                args["x"] = input["x"]!.Value<double>() * (double?)view["image_width"] / previous.Width;
                if (input["y"] != null) args["y"] = input["y"]!.Value<double>() * (double?)view["image_height"] / previous.Height;
            }
            if (selected != null) {
                args["element_token"] = MatchControl(view, selected)["element_token"];
            }
            if (name == "press_key") {
                var parts = ((string)input["key"]!).Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries).Select(part => part.Trim()).Where(part => part.Length > 0).ToArray();
                if (parts.Length == 0) throw new ControlException("InvalidArguments", "请提供按键，例如 ctrl+l 或 enter");
                args["key"] = parts[parts.Length - 1].Equals("enter", StringComparison.OrdinalIgnoreCase) ? "return" : parts[parts.Length - 1];
                args["modifiers"] = new JArray(parts.Take(parts.Length - 1).Select(p => p.ToLowerInvariant()));
            }
            if (name == "scroll") {
                args["amount"] ??= 3; args["by"] = "line";
                if (args["element_token"] == null && args["x"] == null) { args["x"] = view["image_width"]!.Value<double>() / 2; args["y"] = view["image_height"]!.Value<double>() / 2; }
            }
            var inputField = selected;
            if (name == "type_text" && mode == "foreground" && selected != null && target != null && WindowsApplicationControl.UsesWpfKeyboardMessages(target.Handle)) {
                // WPF's posted Enter route must keep the live editor focus rather
                // than re-entering UIA SetFocus (which can commit an IME preedit).
                // Establish it once with a real click, then retain that focus.
                var focus = (JObject)args.DeepClone(); focus.Remove("text");
                var focused = await CallLegacyAsync("click", focus, waitForOperation: true).ConfigureAwait(false);
                if (focused["isError"]?.Value<bool>() == true) return await FinishSimpleAsync(focused, target, "focus", input).ConfigureAwait(false);
                fresh = await ObserveSimpleAsync(target, options).ConfigureAwait(false); view = (JObject)fresh["structuredContent"]!;
                if (fresh["isError"]?.Value<bool>() == true) return await PublicStateAsync(fresh, target).ConfigureAwait(false);
                args.Remove("element_token"); RefreshAction(args, view, reference); inputField = null;
            }
            if (replace) {
                var select = new JObject { ["key"] = "a", ["modifiers"] = new JArray("ctrl"), ["delivery_mode"] = mode };
                RefreshAction(select, view, reference, inputField);
                var selectedAll = await SendSimpleAsync("press_key", select, target, options, reference).ConfigureAwait(false);
                if (selectedAll["isError"]?.Value<bool>() == true) return await FinishSimpleAsync(selectedAll, target, "replace", input).ConfigureAwait(false);
                fresh = await ObserveSimpleAsync(target, options).ConfigureAwait(false); view = (JObject)fresh["structuredContent"]!;
                if (fresh["isError"]?.Value<bool>() == true) return await PublicStateAsync(fresh, target).ConfigureAwait(false);
                RefreshAction(args, view, reference, inputField);
            }
            var oldTitle = (string?)view["window_title"];
            var result = await SendSimpleAsync(name, args, target, options, reference).ConfigureAwait(false);
            if (result["isError"]?.Value<bool>() == true) return await FinishSimpleAsync(result, target, name, input).ConfigureAwait(false);
            if (submit) {
                fresh = await ObserveSimpleAsync(target, options).ConfigureAwait(false); view = (JObject)fresh["structuredContent"]!;
                if (fresh["isError"]?.Value<bool>() == true) return await FinishSimpleAsync(fresh, target, "submit", input).ConfigureAwait(false);
                var enter = new JObject { ["key"] = "return", ["delivery_mode"] = mode };
                RefreshAction(enter, view, reference, inputField);
                result = await SendSimpleAsync("press_key", enter, target, options, reference).ConfigureAwait(false);
                if (result["isError"]?.Value<bool>() == true) return await FinishSimpleAsync(result, target, "submit", input).ConfigureAwait(false);
            }
            var browserEnter = (submit || name == "press_key" && (string?)args["key"] == "return") && target != null
                && new[] { "chrome", "msedge", "chromium", "firefox" }.Contains(Path.GetFileNameWithoutExtension(target.Executable).ToLowerInvariant());
            return await FinishSimpleAsync(result, target, name, input, browserEnter, oldTitle).ConfigureAwait(false);
        }
        private static JObject MatchControl(JObject view, JObject selected)
        {
            var matches = (view["elements"] as JArray ?? new JArray()).OfType<JObject>().Where(e => (string?)e["role"] == (string?)selected["role"] && (string?)e["label"] == (string?)selected["label"]).ToArray();
            var sameFrame = matches.Where(e => JToken.DeepEquals(e["frame"], selected["frame"])).ToArray();
            return (matches.Length == 1 ? matches[0] : sameFrame.Length == 1 ? sameFrame[0] : null)
                ?? throw new ControlException("ElementChanged", "控件已变化，已刷新界面，请重新选择");
        }
        private static void RefreshAction(JObject args, JObject view, string reference, JObject? selected = null)
        {
            args["target_ref"] = view["target_ref"] ?? reference; args["observation_id"] = view["observation_id"];
            args["request_id"] = Guid.NewGuid().ToString(); args["deadline"] = DateTimeOffset.UtcNow.AddSeconds(25).ToString("O");
            // Keep the explicit field through the whole composition. The native
            // input transaction verifies its keyboard focus after activation;
            // a separate click followed by untargeted keys can lose that focus.
            if (selected != null) args["element_token"] = MatchControl(view, selected)["element_token"];
        }
        private async Task<JObject> SendSimpleAsync(string name, JObject args, WindowsControlTarget? target, JObject options, string reference)
        {
            var result = await CallLegacyAsync(name, args, waitForOperation: true).ConfigureAwait(false);
            if ((string?)result["structuredContent"]?["error"]?["code"] == "ViewChanged" && args["element_token"] == null && args["x"] == null) {
                // Rebuilding a native input context also invalidates capture/focus
                // readiness. Re-establish a complete frame once on this recovery path;
                // normal input preflights continue to use accessibility only.
                var recovery = (JObject)options.DeepClone(); recovery["include_screenshot"] = true;
                var fresh = await ObserveSimpleAsync(target, recovery).ConfigureAwait(false);
                if (fresh["isError"]?.Value<bool>() == true) return fresh;
                RefreshAction(args, (JObject)fresh["structuredContent"]!, reference);
                result = await CallLegacyAsync(name, args, waitForOperation: true).ConfigureAwait(false);
            }
            return result;
        }
        private async Task<JObject> FinishSimpleAsync(JObject result, WindowsControlTarget? target, string step, JObject options, bool navigate = false, string? oldTitle = null)
        {
            var failed = result["isError"]?.Value<bool>() == true;
            var updated = await UpdatedAsync(target, failed ? "操作未完成，已返回最新界面" : "已执行 " + step, navigate, oldTitle, options).ConfigureAwait(false);
            if (result["isError"]?.Value<bool>() == true) {
                updated["isError"] = true;
                var failure = result["structuredContent"]?["error"] ?? result["structuredContent"]?["refusal"] ?? result["structuredContent"];
                updated["structuredContent"]!["error"] = new JObject { ["code"] = failure?["code"] ?? "ActionIncomplete", ["message"] = ActionErrorMessage(result), ["step"] = step,
                    ["execution_state"] = failure?["execution_state"] ?? "Unknown" };
                if (step == "submit") updated["structuredContent"]!["error"]!["text_sent"] = true;
                if (updated["content"]?[0] is JObject text) text["text"] = (string?)text["text"] + "\nError at " + step + ": " + updated["structuredContent"]!["error"]!["message"];
            }
            return updated;
        }
        internal static string ActionErrorMessage(JObject result)
        {
            var state = result["structuredContent"];
            var message = (string?)state?["error"]?["message"] ?? (string?)state?["refusal"]?["message"] ?? (string?)state?["message"];
            if (string.IsNullOrWhiteSpace(message)) message = (result["content"] as JArray ?? new JArray())
                .Where(c => (string?)c["type"] == "text").Select(c => (string?)c["text"]).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
            if (message == null || string.IsNullOrWhiteSpace(message)) return "操作结果未确认，请查看已返回的最新界面";
            return message.Length > 800 ? message.Substring(0, 800) + "…" : message;
        }
    }
}
