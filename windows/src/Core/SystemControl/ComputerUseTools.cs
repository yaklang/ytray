#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
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
        private sealed class ControlTiming
        {
            internal readonly Stopwatch Clock = Stopwatch.StartNew();
            internal long Queue, Ready, Preflight, Activate, Input, State, PreflightState, Focus, Capture, ChangeWait;
            internal int Reads, PreflightReads, Captures;
            internal bool ChangeObserved;
            internal string Error = "";
            internal ControlOperation? Operation;
            internal void Write(string tool, int? worker)
            {
                DiagnosticLog.Info("control.timing", $"tool={tool} worker_pid={worker} total_ms={Clock.ElapsedMilliseconds} queue_ms={Queue} ready_ms={Ready} "
                    + $"preflight_ms={Preflight} preflight_reads={PreflightReads} activate_ms={Activate} input_ms={Input} change_wait_ms={ChangeWait} "
                    + $"change_observed={ChangeObserved} state_ms={State - PreflightState} state_reads={Reads - PreflightReads} focus_ms={Focus} "
                    + $"capture_ms={Capture} captures={Captures} error={Error}");
            }
        }

        private readonly SemaphoreSlim _interaction = new SemaphoreSlim(1, 1);
        private enum WindowCaptureMode { Requested, NativeFirst, ForegroundRetry }
        private static JObject Property(string type, string description = "") => new JObject { ["type"] = type, ["description"] = description };
        private JArray PublicTools()
        {
            JObject Tool(string name, string description, JObject properties, params string[] required) {
                properties["task_id"] = new JObject { ["type"] = "string", ["maxLength"] = 96, ["description"] = "Use one globally unique ID for all calls in the same agent task, including across MCP reconnects." };
                properties["resume_from"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 9007199254740991L,
                    ["description"] = "After checking which effects occurred, use the failed operation ID from task.checkpoint.id with the next necessary call." };
                return new JObject {
                    ["name"] = name, ["description"] = description,
                    ["inputSchema"] = new JObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JArray(required), ["additionalProperties"] = false },
                    ["annotations"] = new JObject { ["readOnlyHint"] = !HasOperationFeedback(name) }
                };
            }
            JObject Window() => new JObject { ["window_id"] = Property("integer", "Window ID from list_windows or the previous action. 0 means the primary desktop.") };
            JObject Screenshot() => Property("boolean", "Defaults to false. Request an image when the native layout does not expose the target or you need visual details.");
            JObject NativeFirstScreenshot() => Property("boolean", "Omit to try a window screenshot only when the native layout is unavailable. True also requests a screenshot when controls are available. False reads controls only.");
            JObject ImageRectangle() => new JObject { ["type"] = "object", ["required"] = new JArray("x", "y", "width", "height"),
                ["additionalProperties"] = false,
                ["properties"] = new JObject(new[] { "x", "y", "width", "height" }.Select(field => new JProperty(field,
                    new JObject { ["type"] = "integer", ["minimum"] = field is "x" or "y" ? 0 : 1, ["maximum"] = 4096 }))) };
            JObject Input() {
                var p = Window();
                p["state_id"] = Property("string", "Pass the state_id returned with the control number or image. Superseded states are rejected. Window-image clicks can recheck the latest image after its freshness period.");
                p["delivery_mode"] = new JObject { ["type"] = "string", ["enum"] = new JArray("foreground", "background"), ["description"] = "By default, use a native control pattern in the background when available for a numbered click or scroll. Activate the window for pointer or keyboard input when authorized. An explicit mode overrides this choice." };
                p["include_screenshot"] = Screenshot();
                return p;
            }
            var read = Window(); read["include_screenshot"] = Property("boolean", "Omit to capture only if no usable native layout is available. True also requests an image when controls are available. False reads controls only and never activates the window. Desktop images require true.");
            read["query"] = Property("string", "Optional text filter for the controls.");
            var activate = Window(); activate["include_screenshot"] = NativeFirstScreenshot();
            read["max_image_dimension"] = new JObject { ["type"] = "integer", ["minimum"] = 100, ["maximum"] = 4096 };
            read["detail"] = new JObject { ["type"] = "string", ["enum"] = new JArray("compact", "full"), ["description"] = "Default compact: visible controls and short text. Use query for a specific control or full for the complete tree." };
            read["region"] = new JObject { ["type"] = "object", ["description"] = "Read only controls/text intersecting this rectangle in window-image pixels (same max_image_dimension, default 1400). Defaults to no screenshot. No prior capture is required.",
                ["properties"] = new JObject { ["x"] = new JObject { ["type"] = "number", ["minimum"] = 0 }, ["y"] = new JObject { ["type"] = "number", ["minimum"] = 0 },
                    ["width"] = new JObject { ["type"] = "number", ["minimum"] = 0.01 }, ["height"] = new JObject { ["type"] = "number", ["minimum"] = 0.01 } },
                ["required"] = new JArray("x", "y", "width", "height"), ["additionalProperties"] = false };
            var click = Input(); click["element_index"] = Property("integer", "Control number [N] in the latest returned state. For a target only visible in a screenshot, pass x and y in image pixels with that screenshot's state_id.");
            click["x"] = Property("number"); click["y"] = Property("number"); click["button"] = new JObject { ["type"] = "string", ["enum"] = new JArray("left", "right", "middle") };
            click["count"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 3 };
            click["target_region"] = ImageRectangle();
            click["target_region"]!["description"] = "Bounds of the actual button or field in the image. Must contain x and y. Verify these pixels instead of the default 65-by-65 neighborhood. Use verify_regions to bind additional context, such as the chat title and draft.";
            click["verify_regions"] = new JObject { ["type"] = "array", ["maxItems"] = 4,
                ["description"] = "Optional context rectangles in the same image, such as a conversation title and draft text. When both target_region and verify_regions are omitted, the full client area is also checked.",
                ["items"] = ImageRectangle() };
            var type = Input(); type["text"] = Property("string", "Text to insert into the focused field."); type["element_index"] = Property("integer", "Optional control to focus first.");
            type["input_method"] = new JObject { ["type"] = "string", ["enum"] = new JArray("keyboard", "window_message"),
                ["description"] = "Use standard Unicode keyboard input by default. Choose window_message for a window that accepts Unicode character messages. With that method, select, type, and submit as separate calls." };
            type["replace"] = Property("boolean", "Select all in the focused field before typing; default false.");
            type["submit"] = Property("boolean", "Press Enter after successful typing and return only the final state; default false.");
            var key = Input(); key["key"] = Property("string", "Key or chord, e.g. ctrl+l, ctrl+t, enter, ctrl+a.");
            var scroll = Input(); scroll["direction"] = new JObject { ["type"] = "string", ["enum"] = new JArray("up", "down", "left", "right") };
            scroll["amount"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 50, ["description"] = "Scroll steps; default 3." };
            scroll["element_index"] = Property("integer", "Optional scrollable control."); scroll["x"] = Property("number"); scroll["y"] = Property("number");
            var tools = new JArray {
                Tool("list_apps", "Find desktop apps and their running/window status, including a matching notification-area icon when available. Use an app name or EXE with launch_app.", new JObject { ["query"] = Property("string", "Optional app name filter.") }),
                Tool("list_windows", "List open windows and their window_id values.", new JObject { ["app"] = Property("string", "Optional app name or EXE filter.") }),
                Tool("get_window_state", "Inspect a window in the background. Read native controls first, then try a window screenshot if the layout is unavailable or an image is requested. Visible owned popups appear in related_windows with their own window_id, state_id and image coordinates. Switch to the foreground for a main-window screenshot only if needed and authorized. Use region or query to narrow the controls, or detail:full for every alias.", read, "window_id"),
                Tool("get_desktop_state", "List windows and primary-display bounds. Request a screenshot before choosing desktop coordinates.", new JObject {
                    ["include_screenshot"] = Screenshot(), ["max_image_dimension"] = new JObject { ["type"] = "integer", ["minimum"] = 100, ["maximum"] = 4096 } }),
                Tool("system_status", "Check the computer control connection; stale connections recover automatically.", new JObject())
            };
            var grant = _grant;
            if (grant?.ReadOnly != true) {
                tools.Add(Tool("launch_app", "Activate an existing app window or launch the app if needed. Reuse mode can restore a running app from a uniquely matching notification-area icon. For browsers, pass url to navigate in this same call.", new JObject {
                    ["app"] = Property("string", "Installed app name, e.g. chrome, notepad, or an EXE path."),
                    ["new_window"] = Property("boolean", "Defaults to reusing an existing window. A browser URL opens a new window by default."),
                    ["url"] = Property("string", "Optional http/https URL to open in a browser."),
                    ["include_screenshot"] = NativeFirstScreenshot()
                }, "app"));
                if (grant == null || CanOpenFolder(grant))
                    tools.Add(Tool("open_folder", "Ask Windows to open an existing folder with its registered handler. Returns the path and whether Windows accepted the request. Use list_windows to identify the resulting window.", new JObject { ["path"] = Property("string", "Absolute Windows filesystem path to the folder.") }, "path"));
                tools.Add(Tool("activate_window", "Bring a window to the front, restore it if minimized, and return its latest state.", activate, "window_id"));
                tools.Add(Tool("click", "Click a numbered control or image coordinates. A window-image click with state_id verifies the current target before input, even if the latest image is older than ten seconds. For popup coordinates use the popup's own window_id and state_id while its window group is foreground. If verification fails, use the returned observation to choose again.", click, "window_id"));
                tools.Add(Tool("type_text", "Focus an optional numbered field and type. Use replace and submit where supported. If TextSequenceUnavailable is returned, select, type, and submit as separate calls, checking the text before submission.", type, "window_id", "text"));
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
            // on the next read or action.
            try { await _connection!.RequestAsync("tools/list", new JObject(), _lifetime!.Token, 3000).ConfigureAwait(false); }
            catch { if (generation == _generation && IsEnabled) StopRuntime("正在恢复控制连接。", "Reconnecting"); }
            finally { _operation.Release(); }
        }
        private async Task ReadyAsync(CancellationToken cancellation)
        {
            ControlCancellation.Check(cancellation);
            if (!IsEnabled || _grant == null) throw new ControlException("ControlDisabled", "电脑控制已关闭，请在 YTray 开启");
            if (!IsRunning) _ = EnsureConnectedAsync();
            var timer = Stopwatch.StartNew();
            while (IsEnabled && !IsRunning && Phase != "Paused" && timer.Elapsed < TimeSpan.FromSeconds(12))
                await Task.Delay(100, cancellation).ConfigureAwait(false);
            ControlCancellation.Check(cancellation);
            if (!IsRunning) throw new ControlException("DesktopUnavailable", Phase == "Paused" ? "请解锁桌面后继续" : "控制连接尚未就绪，请稍后重试");
        }
        private async Task<JObject> CallSimpleAsync(string name, JObject arguments, string generation, CancellationToken cancellation, ControlTraceCall? trace)
        {
            var definition = PublicTools().OfType<JObject>().FirstOrDefault(t => (string?)t["name"] == name)
                ?? throw new ControlException("UnknownTool", "未找到工具，请重新读取 tools/list");
            ControlJson.Validate(arguments, (JObject)definition["inputSchema"]!);
            var timing = new ControlTiming();
            var grant = _grant;
            try { await _interaction.WaitAsync(cancellation).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
                timing.Queue = timing.Clock.ElapsedMilliseconds;
                timing.Error = "Cancelled";
                timing.Write(name, null);
                return ControlCancellation.Error().Result();
            }
            timing.Queue = timing.Clock.ElapsedMilliseconds;
            try {
                ControlCancellation.Check(cancellation);
                if (!ReferenceEquals(grant, _grant)) throw new ControlException("GrantRevoked", "电脑控制授权已更新，请重新连接");
                var task = FindTask(arguments);
                var resumedFrom = (long?)arguments["resume_from"];
                var action = HasOperationFeedback(name);
                var blocked = CheckTaskResume(task, action, resumedFrom);
                if (blocked != null) return blocked;
                if (action || task != null || trace != null) {
                    timing.Operation = DescribeOperation(name, arguments);
                    task?.Begin(timing.Operation, resumedFrom);
                    if (trace != null) {
                        timing.Operation.TraceId = trace.Id;
                        trace.Operation = timing.Operation;
                    }
                }
                _activeOperation = timing.Operation;
                EnterOperation(timing, IsRunning ? "target" : "prepare");
                if (name == "open_folder") {
                    var inputAt = timing.Clock.ElapsedMilliseconds;
                    try {
                        var path = (string)arguments["path"]!;
                        return CompleteOperation(timing, OpenFolder(path, generation, timing, cancellation));
                    } finally { timing.Input = timing.Clock.ElapsedMilliseconds - inputAt; }
                }
                var readyAt = timing.Clock.ElapsedMilliseconds;
                try { await ReadyAsync(cancellation).ConfigureAwait(false); }
                catch (ControlException error) { return CompleteOperation(timing, error.Result()); }
                finally { timing.Ready = timing.Clock.ElapsedMilliseconds - readyAt; }
                if (!ReferenceEquals(grant, _grant)) throw new ControlException("GrantRevoked", "电脑控制授权已更新，请重新连接");
                EnterOperation(timing, "target");
                return CompleteOperation(timing, await CallSimpleCoreAsync(name, arguments, timing, cancellation).ConfigureAwait(false), arguments);
            } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
                return CompleteOperation(timing, ControlCancellation.Error().Result());
            } catch (ControlException error) {
                timing.Error = error.Code;
                if (timing.Operation?.Task != null) return CompleteOperation(timing, error.Result());
                FinishOperation(timing.Operation, (JObject)error.Result()["structuredContent"]!["error"]!);
                throw;
            } catch {
                timing.Error = "InternalError";
                if (timing.Operation?.Task != null)
                    return CompleteOperation(timing, new ControlException("InternalError", "电脑控制遇到内部错误", "Unknown").Result());
                FinishOperation(timing.Operation, new JObject { ["code"] = "InternalError", ["message"] = "电脑控制遇到内部错误", ["execution_state"] = "Unknown" });
                throw;
            } finally {
                _activeOperation = null;
                _interaction.Release();
                if (HasOperationFeedback(name) || name == "get_window_state" || name == "get_desktop_state" || name == "open_folder") {
                    int? worker = null;
                    try { worker = WorkerProcessId; } catch (InvalidOperationException) { }
                    timing.Write(name, worker);
                }
            }
        }

        private static bool CanOpenFolder(ControlSessionOptions grant) =>
            !grant.ReadOnly && grant.AllowForeground && !UsesResourceManifest(grant);

        private void RequireFolderAccess(string generation)
        {
            var grant = _grant;
            if (!IsEnabled || grant == null)
                throw new ControlException("ControlDisabled", "电脑控制已关闭，请在本机开启");
            if (_generation != generation)
                throw new ControlException("RuntimeGenerationMismatch", "连接已更新，请重新发起打开文件夹请求");
            if (!CanOpenFolder(grant))
                throw new ControlException("ScopeDenied", "当前授权范围不允许打开文件夹");
            WindowsControlTarget.RequireDesktop();
        }

        private JObject OpenFolder(string path, string generation, ControlTiming timing, CancellationToken cancellation)
        {
            RequireFolderAccess(generation);
            var start = WindowsApplicationControl.FolderStartInfo(path);
            RequireFolderAccess(generation);
            ControlCancellation.Check(cancellation);
            EnterOperation(timing, "launch");
            try
            {
                using var process = Process.Start(start);
            }
            catch (Win32Exception error)
            {
                throw new ControlException("OpenFolderFailed", "Windows 未能打开文件夹。" + error.Message);
            }
            return ControlJson.Result(new JObject { ["path"] = start.FileName, ["status"] = "accepted" });
        }
        private async Task<JObject> CallSimpleCoreAsync(string name, JObject arguments, ControlTiming timing, CancellationToken cancellation)
        {
            WindowsControlTarget? target = null;
            var actionCompleted = false;
            try {
                ControlCancellation.Check(cancellation);
                if (name == "system_status") {
                    return ControlJson.Result(new JObject { ["enabled"] = IsEnabled, ["state"] = Phase, ["backend"] = "windows-native", ["message"] = StatusText,
                        ["version"] = YTrayBuildInfo.Version, ["build_id"] = YTrayBuildInfo.BuildId, ["worker_build_id"] = _connection?.ServerInfo["build_id"], ["worker_pid"] = WorkerProcessId });
                }
                if (name == "list_apps") return await ListSimpleAppsAsync((string?)arguments["query"] ?? "", timing, cancellation).ConfigureAwait(false);
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
                    var app = await Task.Run(() => WindowsApplicationControl.Find((string)arguments["app"]!, CurrentWindows()), cancellation).ConfigureAwait(false);
                    if (!AllowedApp(app.Path))
                        throw new ControlException("ScopeDenied", "当前授权范围不允许打开这个应用");
                    var url = (string?)arguments["url"];
                    var newWindow = arguments["new_window"]?.Value<bool>() ?? (app.IsBrowser && url != null);
                    var opened = await WindowsApplicationControl.LaunchAsync(app, newWindow, url, cancellation, (step, window) => {
                        if (window != null) {
                            if (!Allowed(window)) throw new ControlException("ScopeDenied", "系统打开的窗口超出当前授权范围。");
                            target = window;
                            timing.Operation?.Bind(window);
                        }
                        EnterOperation(timing, step);
                    }, (selected, processes) => RestoreTrayAppAsync(selected, processes, timing, cancellation)).ConfigureAwait(false);
                    actionCompleted = true;
                    target = RegisterTarget(opened.Target.Handle.ToInt64());
                    timing.Operation?.Bind(target);
                    EnterOperation(timing, "readback");
                    var waitForBrowser = app.IsBrowser && (!opened.Reused || url != null);
                    var message = opened.RestoredFromTray ? "已从托盘打开现有应用窗口"
                        : opened.Reused && url == null ? "已切到现有应用窗口" : "应用已打开并切到前台";
                    var state = await UpdatedAsync(target, message, waitForBrowser, "", arguments, waitForUpdate: waitForBrowser,
                        timing: timing, cancellation: cancellation, nativeFirst: true).ConfigureAwait(false);
                    state["structuredContent"]!["reused_window"] = opened.Reused;
                    if (opened.RestoredFromTray) state["structuredContent"]!["restored_from_tray"] = true;
                    return state;
                }
                if (name != "get_desktop_state" && windowId != 0) {
                    lock (_sync) target = _targets.Values.FirstOrDefault(w => w.IsOwned && w.Handle.ToInt64() == windowId);
                    try { target = RegisterTarget(windowId); }
                    catch (ControlException error) when (target?.IsOwned == true && error.Code == "TargetChanged") { throw WindowsControlTarget.PopupChanged(); }
                    timing.Operation?.Bind(target);
                    if (name != "activate_window") target.RequireVisibleOwned();
                }
                if (name == "get_window_state" || name == "get_desktop_state") {
                    EnterOperation(timing, "readback");
                    var raw = target == null
                        ? await ObserveSimpleAsync(null, arguments, timing, cancellation).ConfigureAwait(false)
                        : await ObserveWindowAsync(target, arguments, timing, cancellation).ConfigureAwait(false);
                    if (target?.IsOwned == true && (string?)raw["structuredContent"]?["error"]?["code"] is "PopupChanged" or "TargetChanged")
                        throw WindowsControlTarget.PopupChanged();
                    return await PublicStateAsync(raw,
                        target, options: arguments, timing: timing, cancellation: cancellation).ConfigureAwait(false);
                }
                if (name == "activate_window") {
                    if (target == null) throw new ControlException("InvalidArguments", "请选择应用窗口");
                    if (_grant!.AllowForeground != true) throw new ControlException("BackgroundUnavailable", "当前模式不切换前台窗口");
                    EnterOperation(timing, "activate");
                    await WindowsApplicationControl.ActivateAsync(target, cancellation).ConfigureAwait(false);
                    actionCompleted = true;
                    EnterOperation(timing, "readback");
                    return await UpdatedAsync(target, "窗口已切到前台", options: arguments, timing: timing, cancellation: cancellation, nativeFirst: true).ConfigureAwait(false);
                }
                return await ActSimpleAsync(name, arguments, target, timing, cancellation).ConfigureAwait(false);
            } catch (Exception error) when (actionCompleted && cancellation.IsCancellationRequested && (error is ControlException || error is OperationCanceledException)) {
                return ControlCancellation.Error("Completed").Result();
            } catch (ControlException error) {
                var result = (actionCompleted ? new ControlException(error.Code, error.Message, "Completed", error.Verification) : error).Result();
                if ((error.Code == "PopupChanged" || error.Verification != null) && IsScreenshotClick(name, arguments, target)) EnterOperation(timing, "verify_capture");
                FinishOperation(timing.Operation, (JObject)result["structuredContent"]!["error"]!);
                var staleDesktop = target == null && _grant?.AllowDesktop == true && (error.Code == "StaleObservation" || error.Code == "CaptureExpired");
                if (error.Code != "Cancelled" && (target?.IsOwned == true || timing.Operation?.CurrentStep != "readback")
                    && !cancellation.IsCancellationRequested && IsRunning && (target != null || staleDesktop)) try {
                    var refreshWindowImage = IsScreenshotClick(name, arguments, target) && error.Code is "StaleObservation" or "CaptureExpired"
                        or "CaptureVerificationUnavailable" or "TargetChanged" or "CaptureOccluded" or "WindowNotActive" or "FocusChanged";
                    var owner = FindPopupOwner(target);
                    var state = owner != null
                        ? await ObservePopupOwnerAsync(target!, owner, error.Message, refreshWindowImage ? VisualReadOptions() : arguments,
                            timing, cancellation).ConfigureAwait(false)
                        : refreshWindowImage
                        ? await PublicStateAsync(await ObserveSimpleAsync(target, VisualReadOptions(), timing, cancellation).ConfigureAwait(false), target,
                            error.Message, timing: timing, cancellation: cancellation).ConfigureAwait(false)
                        : staleDesktop
                        ? await PublicStateAsync(await ObserveSimpleAsync(null, arguments, timing, cancellation).ConfigureAwait(false), null,
                            error.Message, arguments, timing, cancellation).ConfigureAwait(false)
                        : await UpdatedAsync(target, error.Message, options: arguments, timing: timing, cancellation: cancellation).ConfigureAwait(false);
                    state["isError"] = true; state["structuredContent"]!["error"] = result["structuredContent"]!["error"]!.DeepClone(); return state;
                } catch when (!cancellation.IsCancellationRequested) { }
                return result;
            }
        }
        private async Task<JObject> ListSimpleAppsAsync(string query, ControlTiming timing, CancellationToken cancellation)
        {
            var windows = CurrentWindows();
            var byExecutable = windows.ToLookup(window => window.Executable, StringComparer.OrdinalIgnoreCase);
            var matches = WindowsApplicationControl.Available(windows)
                .Where(app => query.Length == 0 || app.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || app.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || app.Labels.Any(label => label.Contains(query, StringComparison.OrdinalIgnoreCase)))
                .Take(101).ToArray();
            var shown = matches.Take(100).ToArray();
            var running = WindowsApplicationControl.RunningProcesses(shown.Where(app => AllowedApp(app.Path) && !byExecutable.Contains(app.Path)).Select(app => app.Path));
            string? detectedTrayApp = null;
            var trayApps = shown.Where(app => running.ContainsKey(app.Path)).ToArray();
            if (_grant?.AllowDesktop == true && trayApps.Length == 1) {
                var app = trayApps[0];
                try {
                    var tray = await TrayRequestAsync(app, running[app.Path], restore: false, timing, cancellation).ConfigureAwait(false);
                    if (tray["structuredContent"]?["available"]?.Value<bool>() == true) detectedTrayApp = app.Path;
                } catch (ControlException error) when (!cancellation.IsCancellationRequested && error.Code is not ("Cancelled" or "RuntimeGenerationMismatch" or "GrantRevoked")) {
                    DiagnosticLog.Info("control.tray", "notification-area inspection unavailable · " + error.Code);
                }
            }
            ControlCancellation.Check(cancellation);
            return ControlJson.Result(new JObject {
                ["apps"] = new JArray(shown.Select(app => app.Json(byExecutable[app.Path].ToArray(), AllowedApp(app.Path) ? running.ContainsKey(app.Path) : (bool?)null,
                    detectedTrayApp != null && WindowsApplicationControl.SamePath(app.Path, detectedTrayApp)))),
                ["has_more"] = matches.Length > 100
            });
        }

        private async Task<(WindowsControlTarget Target, bool Invoked)> RestoreTrayAppAsync(WindowsApplicationControl.App app,
            List<(int Pid, long Created)> processes, ControlTiming timing, CancellationToken cancellation)
        {
            var reply = await TrayRequestAsync(app, processes, restore: true, timing, cancellation).ConfigureAwait(false);
            var state = reply["structuredContent"] as JObject ?? new JObject();
            if (reply.Value<bool>("isError")) throw new ControlException((string?)state["error"]?["code"] ?? "TrayRestoreUnavailable",
                ActionErrorMessage(reply), (string?)state["error"]?["execution_state"] ?? "Unknown");
            var invoked = state.Value<bool>("invoked");
            try {
                var target = RegisterTarget(state.Value<long>("window_id"));
                if (!WindowsApplicationControl.SamePath(target.Executable, app.Path) || !processes.Contains((target.Pid, target.Created)))
                    throw new ControlException("TargetChanged", "应用窗口身份已变化，请重新查找。");
                timing.Operation?.Bind(target);
                return (target, invoked);
            } catch (ControlException error) when (invoked && error.ExecutionState == "NotStarted") {
                throw new ControlException(error.Code, error.Message, "Unknown");
            }
        }

        private async Task<JObject> TrayRequestAsync(WindowsApplicationControl.App app, List<(int Pid, long Created)> processes,
            bool restore, ControlTiming timing, CancellationToken cancellation)
        {
            var generation = _generation;
            var grant = _grant;
            await _operation.WaitAsync(cancellation).ConfigureAwait(false);
            try {
                RequireGeneration(generation);
                if (!ReferenceEquals(grant, _grant)) throw new ControlException("GrantRevoked", "电脑控制授权已更新，请重新连接");
                if (grant?.AllowDesktop != true || restore && (!grant.AllowForeground || grant.ReadOnly) || !AllowedApp(app.Path))
                    throw new ControlException("TrayScopeUnavailable", "当前授权无法操作 Windows 通知区。");
                EnterOperation(timing, "find_tray_icon");
                var parameters = new JObject {
                    ["executable"] = app.Path,
                    ["labels"] = new JArray(app.NotificationLabels()),
                    ["processes"] = new JArray(processes.Select(process => new JObject { ["pid"] = process.Pid, ["created"] = process.Created })),
                    ["_operation_id"] = timing.Operation?.Id ?? 0
                };
                var reply = await RequestNativeAsync(_connection!, restore ? "tray/restore" : "tray/inspect", parameters, _lifetime!.Token,
                    cancellation, restore ? 6000 : 1800).ConfigureAwait(false);
                RequireGeneration(generation);
                return reply;
            } finally { _operation.Release(); }
        }
        private WindowsControlTarget[] CurrentWindows(string? executable = null)
        {
            var windows = WindowsControlTarget.Enumerate(executable).Where(Allowed).ToArray();
            lock (_sync) foreach (var target in windows) {
                var previous = _targets.Values.FirstOrDefault(t => t.Handle == target.Handle && t.Pid == target.Pid && t.Created == target.Created && t.Owner == target.Owner);
                if (previous != null) target.Reference = previous.Reference;
                _targets[target.Reference] = target;
            }
            return windows;
        }
        private WindowsControlTarget RegisterTarget(long id) => RegisterTarget(WindowsControlTarget.Resolve(new IntPtr(id)));

        private WindowsControlTarget RegisterTarget(WindowsControlTarget target)
        {
            if (!Allowed(target)) throw new ControlException("TargetUnavailable", "这个窗口当前不可操作，请 list_windows 选择目标");
            lock (_sync) {
                var oldHandle = _targets.Values.FirstOrDefault(t => t.Handle == target.Handle);
                if (oldHandle != null && (oldHandle.Pid != target.Pid || oldHandle.Created != target.Created)) throw new ControlException("TargetChanged", "原窗口已关闭，请从最新窗口列表选择目标");
                if (oldHandle != null && oldHandle.Owner != target.Owner) throw WindowsControlTarget.PopupChanged();
                if (oldHandle != null) target.Reference = oldHandle.Reference;
                else if (target.IsOwned) {
                    // Keep recent closed popups so a stale click can return its owner's state.
                    var expired = _targets.Values.Where(w => w.IsOwned && !w.IsDisplayed)
                        .OrderByDescending(w => _observations.TryGetValue(w.Reference, out var observation) ? observation.Ticks : 0)
                        .Skip(16).ToArray();
                    foreach (var popup in expired) {
                        _targets.Remove(popup.Reference);
                        _observations.Remove(popup.Reference);
                    }
                }
                _targets[target.Reference] = target;
            }
            return target;
        }

        private WindowsControlTarget? FindPopupOwner(WindowsControlTarget? popup)
        {
            if (popup?.IsOwned != true) return null;
            try {
                var owner = RegisterTarget(popup.Owner.ToInt64());
                if (owner.Pid == popup.Pid && owner.Created == popup.Created) return owner;
            } catch (Exception error) when (error is ControlException or System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException) { }
            return null;
        }

        private async Task<JObject> ObservePopupOwnerAsync(WindowsControlTarget popup, WindowsControlTarget owner, string message, JObject options,
            ControlTiming timing, CancellationToken cancellation)
        {
            var read = new JObject();
            foreach (var field in new[] { "include_screenshot", "max_image_dimension" })
                if (options[field] is JToken value) read[field] = value.DeepClone();
            var raw = await ObserveSimpleAsync(owner, read, timing, cancellation, capture: WindowCaptureMode.NativeFirst).ConfigureAwait(false);
            var state = await PublicStateAsync(raw, owner, message, read, timing, cancellation).ConfigureAwait(false);
            if (!popup.IsDisplayed || !popup.IsOwnedBy(owner.Handle))
                state["structuredContent"]!["closed_window_id"] = popup.Handle.ToInt64();
            return state;
        }
        private async Task<JObject> ObserveWindowAsync(WindowsControlTarget target, JObject options, ControlTiming timing, CancellationToken cancellation)
        {
            var grant = _grant;
            var raw = await ObserveSimpleAsync(target, options, timing, cancellation, capture: WindowCaptureMode.NativeFirst).ConfigureAwait(false);
            if (raw.Value<bool>("isError") || raw["structuredContent"] is not JObject state || state["screenshot_error"] == null
                || grant is not { AllowForeground: true, ReadOnly: false } || WindowsApplicationControl.IsForeground(target.Handle)
                || target.IsOwned || target.IsCaptureRestricted || target.FindOwnedWindows(1).Count != 0) return raw;

            if (!ReferenceEquals(grant, _grant)) throw new ControlException("GrantRevoked", "电脑控制授权已更新，请重新连接");
            RequireGeneration((string)state["runtime_generation"]!);
            EnterOperation(timing, "activate_capture");
            var activateAt = timing.Clock.ElapsedMilliseconds;
            try { await WindowsApplicationControl.ActivateAsync(target, cancellation).ConfigureAwait(false); }
            catch (ControlException error) when (error.Code == "WindowNotActive") {
                state["screenshot_error"] = "后台未能取得可靠画面，Windows 也未能将窗口切到前台。";
                return raw;
            } finally { timing.Activate += timing.Clock.ElapsedMilliseconds - activateAt; }
            var foreground = await ObserveSimpleAsync(target, options, timing, cancellation, capture: WindowCaptureMode.ForegroundRetry).ConfigureAwait(false);
            if (foreground["structuredContent"] is JObject foregroundState) foregroundState["foreground_for_capture"] = true;
            return foreground;
        }
        private async Task<JObject> ObserveSimpleAsync(WindowsControlTarget? target, JObject? options, ControlTiming? timing, CancellationToken cancellation,
            bool inspection = false, WindowCaptureMode capture = WindowCaptureMode.Requested)
        {
            for (var attempt = 0; ; attempt++) {
                await ReadyAsync(cancellation).ConfigureAwait(false);
                if (target != null) target = RegisterTarget(target.Handle.ToInt64());
                var args = new JObject { ["max_image_dimension"] = (int?)options?["max_image_dimension"] ?? 1400 };
                if (target != null) {
                    args["target_ref"] = target.Reference;
                    if (capture != WindowCaptureMode.NativeFirst || options?["include_screenshot"] != null || options?["region"] != null)
                        args["include_screenshot"] = options?["include_screenshot"]?.Value<bool>() == true;
                    args["max_elements"] = (string?)options?["detail"] == "full" ? 1000 : 500;
                    args["max_depth"] = 22; args["timeout_ms"] = 1500;
                    if (options?["_geometry_only"]?.Value<bool>() == true) args["include_accessibility_tree"] = false;
                    args["tree_format"] = options?["_controls_only"]?.Value<bool>() == true ? "elements" : "both";
                    if (options?["query"] != null) args["query"] = options["query"]!.DeepClone();
                }
                var readAt = timing?.Clock.ElapsedMilliseconds ?? 0;
                if (timing != null) timing.Reads++;
                try {
                    var textOnlyDesktop = target == null && options?["include_screenshot"]?.Value<bool>() != true;
                    var result = await CallLegacyAsync(target == null ? "get_desktop_state" : "get_window_state", args,
                        waitForOperation: true, timing: timing, textOnlyDesktop: textOnlyDesktop, cancellation: cancellation, inspection: inspection, capture: capture).ConfigureAwait(false);
                    ControlCancellation.Check(cancellation);
                    if (result["isError"]?.Value<bool>() == true && attempt == 0 && (!IsRunning || IsConnectionFailure(result))) {
                        if (IsRunning) StopRuntime("正在恢复控制连接。", "Reconnecting");
                        continue;
                    }
                    return result;
                } catch (ControlException error) when (!cancellation.IsCancellationRequested && attempt == 0 && (!IsRunning || error.Code == "RuntimeGenerationMismatch")) { }
                finally { if (timing != null) timing.State += timing.Clock.ElapsedMilliseconds - readAt; }
            }
        }
        private static bool IsConnectionFailure(JObject result)
        {
            var code = (string?)result["structuredContent"]?["error"]?["code"];
            return code == "DriverUnavailable" || code == "DriverFailure" || code == "RuntimeGenerationMismatch" || code == "DriverTimeout";
        }
        private async Task<JObject> UpdatedAsync(WindowsControlTarget? target, string message, bool waitForNavigation = false, string? oldTitle = null,
            JObject? options = null, bool waitForUpdate = false, ControlTiming? timing = null, CancellationToken cancellation = default, bool nativeFirst = false)
        {
            ControlCancellation.Check(cancellation);
            if (target == null) try { target = RegisterTarget(WindowsApplicationControl.ForegroundWindow); } catch { }
            if (target != null) {
                if (waitForUpdate) await Task.Delay(180, cancellation).ConfigureAwait(false);
                try { target = RegisterTarget(target.Handle.ToInt64()); }
                catch (ControlException) {
                    var remaining = CurrentWindows(target.Executable);
                    if (remaining.Length == 0) return ControlJson.Result(new JObject { ["message"] = message, ["windows"] = new JArray(), ["window_closed"] = true });
                    target = remaining[0];
                }
            }
            var navigationOptions = options == null ? new JObject() : (JObject)options.DeepClone();
            if (waitForNavigation) { navigationOptions["include_screenshot"] = false; navigationOptions["_controls_only"] = true; }
            var capture = nativeFirst || options?["include_screenshot"]?.Value<bool>() == true ? WindowCaptureMode.NativeFirst : WindowCaptureMode.Requested;
            var raw = await ObserveSimpleAsync(target, waitForNavigation ? navigationOptions : options, timing, cancellation,
                capture: waitForNavigation ? WindowCaptureMode.Requested : capture).ConfigureAwait(false);
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
                    await Task.Delay(120, cancellation).ConfigureAwait(false);
                    raw = await ObserveSimpleAsync(target, navigationOptions, timing, cancellation).ConfigureAwait(false);
                }
                // Poll only text while a page settles; capture one image for the final
                // response rather than PNG-encoding every intermediate loading frame.
                if (options?["include_screenshot"]?.Value<bool>() == true
                    || nativeFirst && options?["include_screenshot"] == null && (string?)raw["structuredContent"]?["layout_status"] == "window_only")
                    raw = await ObserveSimpleAsync(target, options, timing, cancellation, capture: capture).ConfigureAwait(false);
            }
            return await PublicStateAsync(raw, target, message, options, timing, cancellation).ConfigureAwait(false);
        }
        private static bool MatchesDestination(string? actual, string? expected)
        {
            if (expected == null) return true;
            if (!Uri.TryCreate(actual, UriKind.Absolute, out var current) || !Uri.TryCreate(expected, UriKind.Absolute, out var requested)) return false;
            static string Host(Uri uri) => uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host.Substring(4) : uri.Host;
            return Host(current).Equals(Host(requested), StringComparison.OrdinalIgnoreCase)
                && (requested.AbsolutePath == "/" || current.AbsolutePath.StartsWith(requested.AbsolutePath, StringComparison.Ordinal));
        }
        private async Task<JObject> PublicStateAsync(JObject raw, WindowsControlTarget? target, string? message = null, JObject? options = null,
            ControlTiming? timing = null, CancellationToken cancellation = default, bool relatedWindow = false)
        {
            ControlCancellation.Check(cancellation);
            if (!relatedWindow && raw["isError"]?.Value<bool>() != true) EnterOperation(timing, "format_state");
            var source = raw["structuredContent"] as JObject ?? new JObject();
            var state = new JObject { ["window_id"] = target?.Handle.ToInt64() ?? 0, ["title"] = (string?)source["window_title"] ?? target?.Title ?? "Desktop" };
            if (target?.IsOwned == true) state["owner_window_id"] = target.Owner.ToInt64();
            var noActivate = target?.IsNonActivating == true;
            if (noActivate) state["no_activate"] = true;
            if (source["observation_id"] is JToken id) state["state_id"] = id.DeepClone();
            if (message != null) state["message"] = message;
            foreach (var field in new[] { "window_bounds", "screenshot_width", "screenshot_height", "image_width", "image_height", "coordinate_space", "layout_status", "screenshot_source", "screenshot_error", "foreground_for_capture", "error" })
                if (source[field] != null) state[field] = source[field]!.DeepClone();
            var full = (string?)options?["detail"] == "full";
            var region = raw["isError"]?.Value<bool>() == true ? null : ControlRegion.Create(source, options);
            // Internal input preflights and loading polls skip the focus read.
            if (!relatedWindow && target != null && raw["isError"]?.Value<bool>() != true) {
                var focusAt = timing?.Clock.ElapsedMilliseconds ?? 0;
                source["focus"] = await ReadNativeContextAsync(target, source, region, cancellation).ConfigureAwait(false);
                if (timing != null) timing.Focus += timing.Clock.ElapsedMilliseconds - focusAt;
            }
            var context = ControlContext.Format(source, full, options?["query"] != null, region, (string?)options?["query"]);
            state["controls_shown"] = context.Shown; state["controls_total"] = context.Total; state["truncated"] = context.Truncated;
            if (context.Focus != null) state["focus"] = context.Focus;
            if (context.DuplicateLinksOmitted > 0) state["duplicate_links_omitted"] = context.DuplicateLinksOmitted;
            if (region != null) { state["region"] = region.ImageBounds; state["coordinate_width"] = region.ImageWidth; state["coordinate_height"] = region.ImageHeight; }
            // Full is an explicit compatibility/inspection escape hatch. The default
            // response carries the control text once, readable by every MCP client.
            if (full) state["tree_markdown"] = context.Text;
            if (!relatedWindow)
                state["windows"] = new JArray(CurrentWindows(target?.Executable).Select(WindowsApplicationControl.WindowJson));
            var text = (message == null ? "" : message + "\n") + "window_id: " + state["window_id"] + " · " + state["title"] + "\n"
                + (state["state_id"] is JToken stateId ? "state_id: " + stateId + "\n" : "")
                + (context.FocusText.Length == 0 ? "" : context.FocusText + "\n") + (region == null ? "" : "Region: " + region.ImageBounds.ToString(Formatting.None) + "\n") + context.Text;
            if (noActivate) text += "\nThis window opts out of automatic activation. Verify the keyboard focus before sending keys or text.";
            if (context.Truncated) text += region == null ? "\nMore: get_window_state(query:\"control text\") or detail:\"full\"." : "\nMore: narrow the region, add query, or use detail:\"full\".";
            if ((string?)state["screenshot_error"] is string captureError) text += "\n" + captureError;
            else if (target != null && state.Value<int>("image_width") == 0 && raw["isError"]?.Value<bool>() != true) {
                var windowOnly = (string?)state["layout_status"] == "window_only";
                if (windowOnly || context.Shown == 0 && !string.IsNullOrWhiteSpace((string?)options?["query"])) {
                    text += windowOnly ? "\nWindows exposes only the frame or containers for this window." : "\nNo native controls matched the query.";
                    text += " For a visual target, request get_window_state(window_id:" + target.Handle.ToInt64()
                        + ",include_screenshot:true), then click using that image's x, y and state_id.";
                }
            }
            var content = new JArray(new JObject { ["type"] = "text", ["text"] = text.Trim() });
            foreach (var image in (raw["content"] as JArray ?? new JArray()).Where(c => (string?)c["type"] == "image")) content.Add(image.DeepClone());
            if (!relatedWindow && region == null && target != null && raw["isError"]?.Value<bool>() != true)
                state["related_windows"] = await ReadRelatedWindowsAsync(target, options, content, timing, cancellation).ConfigureAwait(false);
            return new JObject { ["isError"] = raw["isError"]?.Value<bool>() ?? false, ["content"] = content, ["structuredContent"] = state };
        }

        private async Task<JArray> ReadRelatedWindowsAsync(WindowsControlTarget owner, JObject? options, JArray content,
            ControlTiming? timing, CancellationToken cancellation)
        {
            var windows = new JArray();
            var read = new JObject { ["max_image_dimension"] = Math.Min(1400, (int?)options?["max_image_dimension"] ?? 1400) };
            if (options?["include_screenshot"]?.Value<bool>() == false) read["include_screenshot"] = false;
            foreach (var field in new[] { "query", "detail" })
                if (options?[field] is JToken value) read[field] = value.DeepClone();
            foreach (var popup in owner.FindOwnedWindows(3)) {
                ControlCancellation.Check(cancellation);
                try {
                    RegisterTarget(popup);
                    var raw = await ObserveSimpleAsync(popup, read, timing, cancellation, capture: WindowCaptureMode.NativeFirst).ConfigureAwait(false);
                    if (raw.Value<bool>("isError")) continue;
                    popup.RequireVisibleOwned();
                    if (!popup.IsOwnedBy(owner.Handle)) continue;
                    var result = await PublicStateAsync(raw, popup, options: read, cancellation: cancellation, relatedWindow: true).ConfigureAwait(false);
                    var state = (JObject)result["structuredContent"]!;
                    var blocks = (JArray)result["content"]!;
                    content.Add(new JObject { ["type"] = "text", ["text"] = "Related popup · owner_window_id: " + popup.Owner.ToInt64() + "\n" + blocks[0]!["text"] });
                    if (blocks.Count > 1) {
                        state["image_content_index"] = content.Count;
                        content.Add(blocks[1]!.DeepClone());
                    }
                    windows.Add(state);
                } catch (ControlException error) when (error.Code is "PopupChanged" or "TargetChanged" or "TargetUnavailable") { }
            }
            return windows;
        }
        private async Task<JObject> ReadNativeContextAsync(WindowsControlTarget target, JObject source, ControlRegion? region, CancellationToken cancellation)
        {
            try {
                var args = new JObject { ["window_id"] = target.Handle.ToInt64() };
                if (region != null) {
                    var frame = region.ScreenBounds;
                    args["region"] = new JObject { ["x"] = frame.X, ["y"] = frame.Y, ["w"] = frame.Width, ["h"] = frame.Height };
                    args["controls"] = source["elements"]?.DeepClone();
                }
                var connection = _connection;
                if (connection != null) {
                    var result = await RequestNativeAsync(connection, "read_context", args, _lifetime?.Token ?? CancellationToken.None,
                        cancellation, 1500).ConfigureAwait(false);
                    ControlCancellation.Check(cancellation);
                    return result;
                }
            } catch when (!cancellation.IsCancellationRequested) { }
            return new JObject { ["status"] = "unavailable", ["region_text_unavailable"] = region != null };
        }
        private async Task<JObject> ActSimpleAsync(string name, JObject input, WindowsControlTarget? target, ControlTiming timing, CancellationToken cancellation)
        {
            var preflightAt = timing.Clock.ElapsedMilliseconds;
            var mode = (string?)input["delivery_mode"] ?? (_grant!.AllowForeground ? "foreground" : "background");
            if (mode == "foreground" && _grant?.AllowForeground != true)
                throw new ControlException("ScopeDenied", "当前授权不允许前台输入");
            var replace = name == "type_text" && input["replace"]?.Value<bool>() == true;
            var submit = name == "type_text" && input["submit"]?.Value<bool>() == true;
            var inputMethod = (string?)input["input_method"];
            if (inputMethod == "window_message" && target == null)
                throw new ControlException("InvalidArguments", "窗口字符消息需要应用窗口。");
            if ((replace || submit) && mode != "foreground") throw new ControlException("InvalidArguments", "replace / submit 组合输入需要 foreground 模式");

            var reference = target?.Reference ?? "desktop_primary";
            Observation? previous;
            lock (_sync) _observations.TryGetValue(reference, out previous);
            var coordinates = input["x"] != null || input["y"] != null;
            var boundState = (string?)input["state_id"];
            var verifyCapture = IsScreenshotClick(name, input, target);
            if (target?.IsOwned == true && name == "click" && coordinates) {
                if (!verifyCapture) throw new ControlException("ScreenshotRequired", "点击弹层请提供弹层截图的 window_id、state_id 和像素坐标。");
                if (mode != "foreground") throw new ControlException("ForegroundRequired", "点击弹层截图需要其窗口组处于前台。");
            }
            bool MatchesWindow() => previous != null && previous.Generation == _generation
                && (target == null || previous.Target != null && previous.Target.Bounds.Equals(target.Bounds));
            if (verifyCapture && target != null && previous?.Target is WindowsControlTarget observedTarget
                && previous.Generation == _generation && previous.Id == boundState && !observedTarget.Bounds.Equals(target.Bounds))
                throw new ControlException("StaleObservation", "窗口位置已改变，请按新截图选择位置。",
                    verification: WindowsClickVerification.BoundsChanged(observedTarget.Bounds, target.Bounds));
            if (boundState != null && (!MatchesWindow() || previous!.Id != boundState
                || !verifyCapture && Stopwatch.GetTimestamp() - previous.Ticks >= Stopwatch.Frequency * (coordinates ? 10L : 30L)))
                throw new ControlException("StaleObservation", "该次界面观察已失效，请使用返回的新 state_id 和控件编号或坐标");
            var selected = input["element_index"] == null ? null : (previous?.View["elements"] as JArray)?.OfType<JObject>()
                .FirstOrDefault(row => (int?)row["element_index"] == (int?)input["element_index"]);
            if (input["element_index"] != null && selected == null)
                throw new ControlException("ElementChanged", "请使用最新返回界面的控件编号");
            if (coordinates && (input["x"] == null || input["y"] == null))
                throw new ControlException("InvalidCoordinates", "请同时提供截图上的 x 和 y");
            if (name == "click" && selected == null && !coordinates)
                throw new ControlException("InvalidArguments", "请提供控件编号或截图坐标");

            var preferNative = input["delivery_mode"] == null && mode == "foreground" && target != null && selected != null && !coordinates
                && (name == "scroll" || name == "click" && ((string?)input["button"] ?? "left") == "left" && ((int?)input["count"] ?? 1) == 1);
            if (boundState == null && mode == "foreground" && !preferNative && target != null && WindowsApplicationControl.IsMinimized(target.Handle)) {
                EnterOperation(timing, "activate");
                await WindowsApplicationControl.ActivateAsync(target, cancellation).ConfigureAwait(false);
                target = RegisterTarget(target.Handle.ToInt64());
            }
            var matchingWindow = MatchesWindow();
            var recent = boundState != null || matchingWindow && Stopwatch.GetTimestamp() - previous!.Ticks < Stopwatch.Frequency * (coordinates ? 10L : 25L);
            if (coordinates && (previous == null || previous.Width <= 0))
                throw new ControlException("ScreenshotRequired", "请先获取带截图的窗口状态，再选择图片坐标");
            if (coordinates && !matchingWindow)
                throw new ControlException("CaptureExpired", "窗口位置已改变，请按新截图选择位置");
            var verifyRegions = input["verify_regions"] as JArray;
            var targetRegion = input["target_region"] as JObject;
            if (targetRegion != null || verifyRegions != null) {
                if (!verifyCapture) throw new ControlException("InvalidRegion", "复核区域需要绑定窗口截图。");
                var imageBounds = new Rectangle(0, 0, previous!.Width, previous.Height);
                if (targetRegion != null) {
                    var area = WindowsClickVerification.Region(targetRegion);
                    if (!imageBounds.Contains(area) || !area.Contains((int)input.Value<double>("x"), (int)input.Value<double>("y")))
                        throw new ControlException("InvalidRegion", "目标区域必须位于原截图内并包含点击坐标。");
                }
                if (verifyRegions != null && (verifyRegions.Count > 4
                    || verifyRegions.OfType<JObject>().Any(region => !imageBounds.Contains(WindowsClickVerification.Region(region)))))
                    throw new ControlException("InvalidRegion", "verify_regions 最多四个区域，且都必须位于原截图内。");
            }

            JObject view;
            string observationId;
            var centeredScroll = name == "scroll" && selected == null && !coordinates;
            if (recent) {
                view = previous!.View;
                observationId = previous.Id;
            } else {
                var options = new JObject { ["include_screenshot"] = coordinates, ["_geometry_only"] = selected == null && !coordinates };
                if (coordinates) options["max_image_dimension"] = Math.Max(previous!.Width, previous.Height);
                var fresh = await ObserveSimpleAsync(target, options, timing, cancellation).ConfigureAwait(false);
                if (fresh["isError"]?.Value<bool>() == true) return await PublicStateAsync(fresh, target, timing: timing, cancellation: cancellation).ConfigureAwait(false);
                view = (JObject)fresh["structuredContent"]!;
                observationId = (string)view["observation_id"]!;
                if (selected != null) selected = MatchControl(view, selected);
            }

            var args = new JObject { ["target_ref"] = reference, ["observation_id"] = observationId,
                ["request_id"] = Guid.NewGuid().ToString(), ["deadline"] = DateTimeOffset.UtcNow.AddSeconds(25).ToString("O"), ["delivery_mode"] = mode };
            foreach (var field in new[] { "x", "y", "button", "count", "text", "direction", "amount" })
                if (input[field] != null) args[field] = input[field]!.DeepClone();
            if (coordinates && !recent) {
                var width = view.Value<int>("image_width"); var height = view.Value<int>("image_height");
                if (width <= 0 || height <= 0) throw new ControlException("CaptureExpired", "无法更新截图，请重新观察");
                args["x"] = input.Value<double>("x") * width / previous!.Width;
                args["y"] = input.Value<double>("y") * height / previous.Height;
            }
            if (selected != null) args["element_token"] = selected["element_token"];
            timing.Operation?.SetElement((string?)selected?["element_token"], (string?)selected?["label"]);
            if (name == "press_key") {
                var parts = ((string)input["key"]!).Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries).Select(part => part.Trim()).Where(part => part.Length > 0).ToArray();
                if (parts.Length == 0) throw new ControlException("InvalidArguments", "请提供按键，例如 ctrl+l 或 enter");
                args["key"] = parts[parts.Length - 1].Equals("enter", StringComparison.OrdinalIgnoreCase) ? "return" : parts[parts.Length - 1];
                args["modifiers"] = new JArray(parts.Take(parts.Length - 1).Select(part => part.ToLowerInvariant()));
            }
            if (name == "scroll") {
                args["amount"] ??= 3;
                args["by"] = "line";
            }
            var compact = new JObject { ["replace"] = replace, ["submit"] = submit, ["center_scroll"] = centeredScroll };
            if (inputMethod != null) compact["input_method"] = inputMethod;
            if (verifyCapture) {
                compact["verify_capture"] = true;
                if (targetRegion != null) compact["target_region"] = targetRegion.DeepClone();
                if (verifyRegions is { Count: > 0 }) compact["verify_regions"] = verifyRegions.DeepClone();
            }
            if (preferNative) compact["prefer_native_control"] = true;
            if (selected != null) compact["expected_control"] = new JObject {
                ["role"] = selected["role"], ["label"] = selected["label"], ["automation_id"] = selected["automation_id"], ["frame"] = selected["frame"]
            };
            timing.Preflight = timing.Clock.ElapsedMilliseconds - preflightAt;
            timing.PreflightReads = timing.Reads;
            timing.PreflightState = timing.State;
            var inputAt = timing.Clock.ElapsedMilliseconds;
            JObject result;
            try { result = await CallLegacyAsync(name, args, waitForOperation: true, compact: compact, timing: timing, cancellation: cancellation).ConfigureAwait(false); }
            finally { timing.Input = timing.Clock.ElapsedMilliseconds - inputAt; }
            var browserEnter = (submit || name == "press_key" && (string?)args["key"] == "return") && target != null
                && WindowsApplicationControl.IsBrowserExecutable(target.Executable);
            return await FinishSimpleAsync(result, target, name, input, timing, cancellation, browserEnter, (string?)view["window_title"],
                verifyCapture ? Math.Max(previous!.Width, previous.Height) : null).ConfigureAwait(false);
        }
        private static bool IsScreenshotClick(string name, JObject input, WindowsControlTarget? target) => name == "click" && target != null
            && input["state_id"] != null && input["x"] != null && input["y"] != null;
        private static JObject VisualReadOptions(int? maximum = null)
        {
            var options = new JObject { ["include_screenshot"] = true };
            if (maximum.HasValue) options["max_image_dimension"] = maximum.Value;
            return options;
        }
        private static JObject MatchControl(JObject view, JObject selected)
        {
            var elements = (view["elements"] as JArray ?? new JArray()).OfType<JObject>();
            var matching = elements.Where(row => (string?)row["role"] == (string?)selected["role"] && (string?)row["label"] == (string?)selected["label"]
                && (string?)row["automation_id"] == (string?)selected["automation_id"]).ToArray();
            var sameToken = matching.FirstOrDefault(row => JToken.DeepEquals(row["element_token"], selected["element_token"]));
            if (sameToken != null) return sameToken;
            var sameFrame = matching.Where(row => JToken.DeepEquals(row["frame"], selected["frame"])).ToArray();
            return (sameFrame.Length == 1 ? sameFrame[0] : null)
                ?? throw new ControlException("ElementChanged", "控件已变化，已刷新界面，请重新选择");
        }
        private async Task<JObject> FinishSimpleAsync(JObject result, WindowsControlTarget? target, string step, JObject options, ControlTiming timing,
            CancellationToken cancellation, bool navigate = false, string? oldTitle = null, int? verificationImageDimension = null)
        {
            var failed = result["isError"]?.Value<bool>() == true;
            var failure = result["structuredContent"]?["error"] ?? result["structuredContent"]?["refusal"] ?? result["structuredContent"];
            JObject Cancelled() {
                var cancelled = ControlCancellation.Error(failed ? (string?)failure?["execution_state"] ?? "Unknown" : "Completed").Result();
                cancelled["structuredContent"]!["window_id"] = target?.Handle.ToInt64() ?? 0;
                var error = (JObject)cancelled["structuredContent"]!["error"]!;
                error["step"] = failure?["step"] ?? new JValue(step);
                foreach (var field in new[] { "selection_sent", "text_sent" })
                    if (failure?[field] is JToken value) error[field] = value.DeepClone();
                return cancelled;
            }
            if ((string?)failure?["code"] == "Cancelled" || cancellation.IsCancellationRequested) return Cancelled();
            JObject? actionError = null;
            if (failed) {
                actionError = new JObject { ["code"] = failure?["code"] ?? "ActionIncomplete", ["message"] = ActionErrorMessage(result),
                    ["step"] = failure?["step"] ?? new JValue(step), ["execution_state"] = failure?["execution_state"] ?? "Unknown" };
                foreach (var field in new[] { "selection_sent", "text_sent" })
                    if (failure?[field] is JToken value) actionError[field] = value.DeepClone();
                if (failure?["verification"] is JObject difference) actionError["verification"] = difference.DeepClone();
                FinishOperation(timing.Operation, actionError);
            } else EnterOperation(timing, "readback");
            var waited = result["structuredContent"]?["_state_waited"]?.Value<bool>() == true;
            var owner = FindPopupOwner(target);
            var message = failed ? "操作未完成，已返回最新界面" : "已执行 " + step;
            async Task<JObject> ReadOwnerAsync()
            {
                try {
                    var readback = await ObservePopupOwnerAsync(target!, owner!, message,
                        failed && verificationImageDimension.HasValue ? VisualReadOptions(verificationImageDimension) : options, timing, cancellation).ConfigureAwait(false);
                    if (!readback.Value<bool>("isError") || (string?)readback["structuredContent"]?["error"]?["code"] is not ("PopupChanged" or "TargetChanged"))
                        return readback;
                } catch (ControlException error) when (!cancellation.IsCancellationRequested && error.Code is "PopupChanged" or "TargetChanged") { }

                // Both a popup and its owner can disappear after an action. Preserve
                // the input result and let the caller select the next authorized HWND.
                var notice = failed ? "操作未完成，原窗口无法读回，请核对窗口列表。"
                    : "输入已投递，原窗口无法读回，请核对窗口列表。";
                var state = new JObject { ["message"] = notice, ["windows"] = new JArray(CurrentWindows().Select(WindowsApplicationControl.WindowJson)) };
                if (!target!.IsDisplayed || !target.IsOwnedBy(owner!.Handle)) {
                    state["window_closed"] = true;
                    state["closed_window_id"] = target.Handle.ToInt64();
                }
                return ControlJson.Result(state, notice);
            }
            JObject updated;
            try {
                if (owner != null && (verificationImageDimension.HasValue || (string?)failure?["code"] == "PopupChanged"
                    || !target!.IsDisplayed || !target.IsOwnedBy(owner.Handle))) {
                    updated = await ReadOwnerAsync().ConfigureAwait(false);
                } else if (failed && verificationImageDimension.HasValue && target != null) {
                    updated = await PublicStateAsync(await ObserveSimpleAsync(target, VisualReadOptions(verificationImageDimension), timing, cancellation).ConfigureAwait(false),
                        target, "点击已停止，已返回最新界面", timing: timing, cancellation: cancellation).ConfigureAwait(false);
                } else if (target == null && failed && options["state_id"] != null && _grant?.AllowDesktop == true) {
                    // A rejected desktop action returns an observation of the primary display.
                    updated = await PublicStateAsync(await ObserveSimpleAsync(null, options, timing, cancellation).ConfigureAwait(false),
                        null, "操作未完成，已返回最新界面", options, timing, cancellation).ConfigureAwait(false);
                } else {
                    updated = await UpdatedAsync(target, message,
                        navigate && !failed, oldTitle, options, waitForUpdate: !failed && !waited, timing: timing, cancellation: cancellation).ConfigureAwait(false);
                    if (owner != null && updated.Value<bool>("isError")
                        && (string?)updated["structuredContent"]?["error"]?["code"] is "PopupChanged" or "TargetChanged")
                        updated = await ReadOwnerAsync().ConfigureAwait(false);
                }
            } catch (Exception error) when (cancellation.IsCancellationRequested && (error is ControlException || error is OperationCanceledException)) {
                return Cancelled();
            } catch (ControlException error) {
                if (owner != null && !cancellation.IsCancellationRequested && error.Code is "PopupChanged" or "TargetChanged")
                    updated = await ReadOwnerAsync().ConfigureAwait(false);
                else {
                    if (actionError == null) throw new ControlException(error.Code, error.Message, "Completed");
                    return new JObject { ["isError"] = true, ["structuredContent"] = new JObject { ["error"] = actionError },
                        ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = actionError["message"] }) };
                }
            }
            if (actionError != null) {
                updated["isError"] = true;
                updated["structuredContent"]!["error"] = actionError;
            }
            if (result["structuredContent"]?["visual_verification"] is JToken verification)
                updated["structuredContent"]!["visual_verification"] = verification.DeepClone();
            if (result["structuredContent"]?["visual_verification_caret"] is JToken caret)
                updated["structuredContent"]!["visual_verification_caret"] = caret.DeepClone();
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
