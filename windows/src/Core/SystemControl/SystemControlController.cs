#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YTray.Models;
using YTray.Native;

namespace YTray.Core.SystemControl
{
    /// <summary>The native master switch stays enabled until the user switches it off.
    /// Driver restarts and desktop suspension do not revoke the client's configuration.</summary>
    public sealed partial class SystemControlController : INotifyPropertyChanged, IDisposable
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private readonly Dispatcher _dispatcher;
        private readonly ControlAccessStore _access;
        private readonly SemaphoreSlim _operation = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _starting = new SemaphoreSlim(1, 1);
        private readonly object _sync = new object();
        private readonly Dictionary<string, WindowsControlTarget> _targets = new Dictionary<string, WindowsControlTarget>();
        private readonly Dictionary<string, Observation> _observations = new Dictionary<string, Observation>();
        private readonly Dictionary<string, (string Hash, Task<JObject> Task, long Created)> _requests = new Dictionary<string, (string, Task<JObject>, long)>();
        private Dictionary<string, JObject> _tools = new Dictionary<string, JObject>();
        private NativeControlConnection? _connection;
        private CancellationTokenSource? _lifetime;
        private ControlSessionOptions? _grant;
        private string? _token;
        private string _generation = "";
        private string _session = "";
        private Timer? _reconnect;
        private bool _disposed;
        public string Phase { get; private set; } = "Stopped";
        public string LastError { get; private set; } = "";
        public string Activity { get; private set; } = "尚无电脑控制活动";
        public bool IsEnabled { get; private set; }
        public bool IsActive => IsEnabled;
        public bool IsRunning => Phase == "Running" && _connection != null && _lifetime?.IsCancellationRequested == false;
        // UI refreshes every second; full hash verification belongs to startup/install,
        // not the status getter (which would repeatedly read the complete runtime).
        public bool IsInstalled => true;
        public string StatusText => !IsEnabled ? "电脑控制已关闭" : Phase == "Installing" ? "正在准备控制组件" : Phase == "Paused" ? "电脑控制已开启 · 等待桌面解锁"
            : IsRunning ? "电脑控制已开启" : "电脑控制已开启 · 正在连接";
        public bool IsElevated => ControlElevation.IsElevated;
        public string Endpoint => "http://127.0.0.1:" + ControlGateway.Port + "/system/mcp";
        internal string Generation => _generation;
        internal int? WorkerProcessId => _connection?.ProcessId;
        internal SystemControlController(string applicationDirectory)
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            _access = new ControlAccessStore(applicationDirectory);
            SystemEvents.SessionSwitch += SessionChanged;
            SystemEvents.DisplaySettingsChanged += DisplayChanged;
        }
        private void DisplayChanged(object? sender, EventArgs e) {
            lock (_sync) { _observations.Clear(); _targets.Clear(); }
            if (IsEnabled) { Activity = "显示设置已变化，下次操作会重新观察。"; Changed(); }
        }
        private void SessionChanged(object? sender, SessionSwitchEventArgs e) {
            if (!IsEnabled) return;
            if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.ConsoleConnect || e.Reason == SessionSwitchReason.RemoteConnect)
                _ = EnsureConnectedAsync();
            else StopRuntime("桌面恢复后自动继续，无需重新授权。", "Paused");
        }
        private void Changed()
        {
            if (_dispatcher.HasShutdownStarted) return;
            if (_dispatcher.CheckAccess()) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
            else _dispatcher.BeginInvoke(new Action(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""))));
        }
        public Task EnableAsync() => IsEnabled ? EnsureConnectedAsync() : EnableCoreAsync(new ControlSessionOptions(), false);
        internal Task RestoreAsync() => IsEnabled ? EnsureConnectedAsync() : EnableCoreAsync(new ControlSessionOptions(), true);
        // Scoped options are retained for isolated integration fixtures, not exposed in the UI.
        internal Task StartAsync(ControlSessionOptions options) { Stop(); return EnableCoreAsync(options, false); }
        internal Task InstallAsync() => Task.CompletedTask;
        public async Task RepairAsync()
        {
            await _starting.WaitAsync().ConfigureAwait(false);
            try {
                StopRuntime(); LastError = "";
                Activity = "原生控制进程已重置。"; Changed();
            } catch (Exception error) { LastError = error.Message; Changed(); throw; }
            finally { _starting.Release(); }
            if (IsEnabled) await EnsureConnectedAsync().ConfigureAwait(false);
        }
        private async Task EnableCoreAsync(ControlSessionOptions options, bool restore)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SystemControlController));
            var grant = options.Copy(); grant.Origin = ControlJson.Origin(grant.Origin);
            if (grant.ClientName.Trim().Length == 0 || grant.ClientName.Length > 80) throw new ControlException("InvalidGrant", "客户端名称无效");
            if (grant.Executables.Length == 0 && !grant.AllowDesktop) throw new ControlException("InvalidGrant", "请选择允许操作的应用或主屏范围");
            grant.Executables = grant.Executables.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            _token = _access.Open(restore); _grant = grant; IsEnabled = true;
            lock (_sync) _tools = NativeControlSchema.Review(ReadReferenceCatalog(), grant);
            _reconnect = new Timer(_ => { if (IsEnabled) _ = MaintainConnectionAsync(); }, null, 5000, 5000);
            await EnsureConnectedAsync().ConfigureAwait(false);
        }
        internal static JObject CapabilityManifest(ControlSessionOptions grant)
        {
            var allowed = new JArray("start_session", "end_session", "get_window_state", "check_permissions");
            if (grant.AllowDesktop) allowed.Add("get_desktop_state");
            if (!grant.ReadOnly) foreach (var tool in NativeControlSchema.Actions) allowed.Add(tool);
            return new JObject { ["version"] = 3, ["allow"] = new JObject { ["tools"] = allowed }, ["resources"] = new JObject {
                ["apps"] = new JArray(grant.Executables.Select(path => new JObject { ["executable"] = path, ["windows"] = "all" })),
                ["desktop"] = new JObject { ["display"] = grant.AllowDesktop } } };
        }
        internal static bool UsesResourceManifest(ControlSessionOptions grant) => grant.Executables.Length > 0 || !grant.AllowDesktop;
        internal static JObject ToolPolicy(ControlSessionOptions grant) => new JObject { ["allow"] = CapabilityManifest(grant)["allow"]!.DeepClone() };
        private async Task EnsureConnectedAsync()
        {
            if (_disposed || !IsEnabled || IsRunning || !await _starting.WaitAsync(0).ConfigureAwait(false)) return;
            var id = "";
            try {
                if (_disposed || !IsEnabled) return;
                try { WindowsControlTarget.RequireDesktop(); }
                catch (ControlException) { Phase = "Paused"; Activity = "解锁后自动恢复电脑控制。"; Changed(); return; }
                StopRuntime();
                id = _generation; _session = "yt_" + id;
                var lifetime = new CancellationTokenSource(); _lifetime = lifetime;
                Phase = "Connecting"; LastError = ""; Changed();
                RequireGeneration(id);
                var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (DictionaryEntry pair in Environment.GetEnvironmentVariables()) environment[(string)pair.Key] = pair.Value as string ?? "";
                var connection = new NativeControlConnection(); _connection = connection;
                connection.OnExit = () => { lock (_sync) if (_generation == id && IsEnabled) StopRuntime("控制组件已断开，正在自动重连。", "Reconnecting"); };
                var tools = await connection.StartAsync(typeof(App).Assembly.Location, environment, lifetime.Token).ConfigureAwait(false);
                RequireGeneration(id);
                lock (_sync) _tools = NativeControlSchema.Review(tools, _grant!);
                lock (_sync) {
                    RequireGeneration(id); Phase = "Running";
                    Activity = "可随时连接；全部应用与操作已允许。";
                }
                Changed();
            } catch (Exception error) {
                var diagnostic = _connection?.DiagnosticTail ?? "";
                lock (_sync) if (_generation == id && IsEnabled) StopRuntime((error is ControlException ? error.Message : "控制组件连接失败：" + error.Message) + (diagnostic.Length == 0 ? "" : "\n" + diagnostic.Substring(Math.Max(0, diagnostic.Length - 3000))), "Reconnecting");
            }
            finally { _starting.Release(); }
        }
        public void Stop()
        {
            IsEnabled = false; _token = null;
            _reconnect?.Dispose(); _reconnect = null;
            StopRuntime();
            lock (_sync) { _requests.Clear(); _tools.Clear(); }
            _grant = null;
            try { _access.Clear(); } catch (Exception error) { LastError = error.Message; }
            Changed();
        }
        private void StopRuntime(string reason = "", string phase = "Stopped")
        {
            lock (_sync) {
            // Cancel before I/O; no pending action is replayed by reconnection.
            _generation = Guid.NewGuid().ToString("N"); Phase = phase;
            var cancellation = Interlocked.Exchange(ref _lifetime, null); cancellation?.Cancel();
            var connection = Interlocked.Exchange(ref _connection, null); if (connection != null) { connection.OnExit = null; connection.Dispose(); }
            lock (_sync) { _targets.Clear(); _observations.Clear(); }
            LastError = reason; Activity = reason.Length == 0 ? "已停止接收新操作" : reason; Changed();
            }
        }
        internal bool Authorized(string? bearer, string? origin)
        {
            var token = _token; var grant = _grant;
            return IsEnabled && token != null && grant != null
                && ControlJson.EqualSecret(bearer, "Bearer " + token)
                && (origin == null ? grant.Origin.Length == 0 : grant.Origin == origin);
        }
        internal bool AllowedOrigin(string origin) => IsEnabled && _grant?.Origin == origin;
        public string? ConfigurationJSON => !IsEnabled || _token == null ? null : new JObject { ["mcpServers"] = new JObject { ["ytray-system"] = new JObject {
            ["type"] = "http", ["url"] = Endpoint, ["headers"] = new JObject { ["Authorization"] = "Bearer " + _token } } } }.ToString(Formatting.Indented);
        internal JObject Status() => new JObject { ["state"] = Phase, ["session_id"] = _session, ["runtime_generation"] = _generation,
            ["enabled"] = IsEnabled, ["elevated"] = IsElevated, ["read_only"] = _grant?.ReadOnly, ["foreground_authorized"] = _grant?.AllowForeground,
            ["desktop_authorized"] = _grant?.AllowDesktop, ["client"] = _grant?.ClientName, ["backend"] = "windows-native", ["isolated"] = true };
        internal JArray Tools()
        {
            return PublicTools();
        }
        private static JArray ReadReferenceCatalog()
        {
            return NativeControlSchema.Catalog();
        }
        internal JArray ReferenceTools() => PublicTools();
        internal Task<JObject> CallAsync(string name, JObject arguments, string? expectedGeneration = null)
        {
            // Existing clients may finish an in-flight v1 operation. New discovery exposes
            // only the compact API; driver session bookkeeping stays behind this boundary.
            return arguments["target_ref"] != null || arguments["request_id"] != null || arguments["observation_id"] != null
                || name == "check_permissions" || name == "end_session" || name.StartsWith("ytray_", StringComparison.Ordinal)
                ? CallLegacyAsync(name, arguments, expectedGeneration) : CallSimpleAsync(name, arguments, expectedGeneration);
        }
        private async Task<JObject> CallLegacyAsync(string name, JObject arguments, string? expectedGeneration = null, bool waitForOperation = false)
        {
            RequirePublicGrant();
            if (expectedGeneration != null && expectedGeneration != _generation) throw new ControlException("RuntimeGenerationMismatch", "连接已更新，请重新观察");
            if (!IsEnabled || _grant == null) throw new ControlException("ControlDisabled", "电脑控制已关闭，请在本机开启");
            var generation = _generation;
            if (name == "end_session" || name == "ytray_session_close") {
                if (arguments.Count != 0) throw new ControlException("InvalidArguments", "结束操作不接受参数");
                StopRuntime("本次操作已结束，电脑控制保持开启。", "Reconnecting"); _ = EnsureConnectedAsync();
                return ControlJson.Result(new JObject { ["ended"] = true, ["enabled"] = true });
            }
            if (name == "system_status" || name == "ytray_session_get" || name == "ytray_session_open" || name == "ytray_capabilities") {
                if (arguments.Count != 0) throw new ControlException("InvalidArguments", "此工具不接受参数"); return ControlJson.Result(Status());
            }
            if (name == "ytray_request_get" || name == "ytray_request_cancel") {
                if (arguments.Count != 1 || arguments["request_id"]?.Type != JTokenType.String) throw new ControlException("InvalidArguments", "需要 request_id");
                Task<JObject>? request; lock (_sync) request = _requests.TryGetValue((string)arguments["request_id"]!, out var entry) ? entry.Task : null;
                if (request == null) throw new ControlException("RequestNotFound", "找不到本会话请求");
                if (name == "ytray_request_cancel" && !request.IsCompleted) { StopRuntime("请求已取消，正在恢复连接。", "Reconnecting"); _ = EnsureConnectedAsync(); return ControlJson.Result(new JObject { ["execution_state"] = "Unknown" }); }
                return request.IsCompleted ? await request.ConfigureAwait(false) : ControlJson.Result(new JObject { ["status"] = "pending" });
            }
            // A retry of an admitted request returns the original outcome, including
            // after its deadline or while another operation is busy. Never replay it.
            if (NativeControlSchema.Actions.Contains(name) && (string?)arguments["request_id"] is string retryId) {
                Task<JObject>? previous = null;
                lock (_sync) if (_requests.TryGetValue(retryId, out var entry)) {
                    if (entry.Hash != name + ":" + Canonical(arguments).ToString(Formatting.None)) throw new ControlException("RequestConflict", "request_id 已被不同参数使用"); previous = entry.Task;
                }
                if (previous != null) return await previous.ConfigureAwait(false);
            }
            if (!IsRunning || _lifetime == null) throw new ControlException("DesktopUnavailable", Phase == "Paused" ? "桌面已锁定，解锁后自动恢复。" : "控制组件正在连接，请稍后重试，无需重新授权。");
            if (waitForOperation) {
                try { await _operation.WaitAsync(_lifetime.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw new ControlException("DriverUnavailable", "正在恢复控制连接"); }
            } else if (!await _operation.WaitAsync(0).ConfigureAwait(false)) throw new ControlException("QueueFull", "已有操作执行中，请等待结果；停止始终可用");
            try {
                RequireGeneration(generation); WindowsControlTarget.RequireDesktop();
                if (name == "list_apps" || name == "list_windows") {
                    if (arguments.Count != 0) throw new ControlException("InvalidArguments", "请使用返回的精确 target_ref 选择目标");
                    var windows = WindowsControlTarget.Enumerate().Where(t => Allowed(t)).ToArray();
                    var array = new JArray(); lock (_sync) { foreach (var target in windows) {
                        var previous = _targets.Values.FirstOrDefault(t => t.Handle == target.Handle && t.Pid == target.Pid && t.Created == target.Created);
                        if (previous != null) target.Reference = previous.Reference;
                        _targets[target.Reference] = target; array.Add(target.Json());
                    } }
                    return ControlJson.Result(new JObject { [name == "list_apps" ? "apps" : "windows"] = array, ["desktop_target_ref"] = _grant!.AllowDesktop ? "desktop_primary" : null });
                }
                if (!_tools.TryGetValue(name, out var definition)) throw new ControlException("ScopeDenied", "本会话未授权该工具");
                ControlJson.Validate(arguments, (JObject)definition["inputSchema"]!);
                if ((string?)arguments["session_id"] is string session && session != _session || (string?)arguments["runtime_generation"] is string gen && gen != generation)
                    throw new ControlException("RuntimeGenerationMismatch", "会话代次不匹配");
                var action = NativeControlSchema.Actions.Contains(name);
                if (!action) return await ExecuteAsync(name, arguments, generation).ConfigureAwait(false);
                if (_grant!.ReadOnly) throw new ControlException("ScopeDenied", "只读会话不能输入");
                if (!Guid.TryParse((string?)arguments["request_id"], out _)) throw new ControlException("InvalidArguments", "动作需要 UUID request_id，以便查询结果并防止重复投递");
                if (!DateTimeOffset.TryParse((string?)arguments["deadline"], out var deadline) || deadline <= DateTimeOffset.UtcNow || deadline > DateTimeOffset.UtcNow.AddSeconds(30)) throw new ControlException("DeadlineExceeded", "动作 deadline 必须在未来 30 秒内");
                var requestId = (string)arguments["request_id"]!; var hash = name + ":" + Canonical(arguments).ToString(Formatting.None);
                Task<JObject> result; lock (_sync) {
                    if (_requests.TryGetValue(requestId, out var existing)) {
                        if (existing.Hash != hash) throw new ControlException("RequestConflict", "request_id 已被不同参数使用"); result = existing.Task;
                    } else {
                        if (_requests.Count >= 512) foreach (var expired in _requests.Where(p => p.Value.Task.IsCompleted && Stopwatch.GetTimestamp() - p.Value.Created > Stopwatch.Frequency * 30L).Select(p => p.Key).ToArray()) _requests.Remove(expired);
                        if (_requests.Count >= 512) throw new ControlException("QueueFull", "操作过于频繁，请稍后重试");
                        result = ExecuteAsync(name, arguments, generation); _requests.Add(requestId, (hash, result, Stopwatch.GetTimestamp()));
                    }
                }
                return await result.ConfigureAwait(false);
            } finally { _operation.Release(); }
        }
        private async Task<JObject> ExecuteAsync(string name, JObject original, string generation)
        {
            var arguments = (JObject)original.DeepClone(); var action = NativeControlSchema.Actions.Contains(name);
            var desktop = name == "get_desktop_state" || (string?)arguments["target_ref"] == "desktop_primary";
            WindowsControlTarget? target = null;
            if (!desktop && name != "check_permissions") {
                lock (_sync) {
                    if ((string?)arguments["target_ref"] is string reference) _targets.TryGetValue(reference, out target);
                    else if (long.TryParse((string?)arguments["window_id"], out var hwnd)) target = _targets.Values.FirstOrDefault(t => t.Handle.ToInt64() == hwnd && t.Pid == (int?)arguments["pid"]);
                }
                if (target == null || !Allowed(target)) throw new ControlException("TargetChanged", "请先 list_windows 并选择授权的精确 target_ref");
                target.Revalidate();
                if (arguments["pid"] != null && (int)arguments["pid"]! != target.Pid || arguments["window_id"] != null && (long)arguments["window_id"]! != target.Handle.ToInt64()) throw new ControlException("TargetChanged", "目标参数冲突");
                arguments["pid"] = target.Pid; arguments["window_id"] = target.Handle.ToInt64();
            }
            if (desktop && _grant?.AllowDesktop != true) throw new ControlException("ScopeDenied", "本会话未授权主屏范围");
            var requestedWindowImage = name == "get_window_state" && original["include_screenshot"]?.Value<bool>() != false;
            var scopedCapture = _grant?.AllowDesktop != true;
            var blockedWindowImage = requestedWindowImage && scopedCapture && target!.IsObscured(IntPtr.Zero);
            // The pinned driver's generic window path drops its BitBlt occlusion flag.
            // Until that provenance is available, never return an obscured region as
            // another application's window image. UIA background control still works.
            if (blockedWindowImage) {
                if (arguments["include_accessibility_tree"]?.Value<bool>() == false) throw new ControlException("CaptureOccluded", "窗口被遮挡，无法确认截图归属；请显示窗口后重新观察");
                arguments["include_screenshot"] = false;
            }
            Observation? observation = null;
            if (action) {
                var mode = (string?)arguments["delivery_mode"] ?? "background";
                if (mode != "background" && !(mode == "foreground" && _grant?.AllowForeground == true)) throw new ControlException("ScopeDenied", "前台输入需要本机单独授权并在调用中显式选择");
                if (desktop && mode != "foreground") throw new ControlException("BackgroundUnavailable", "主屏输入仅支持明确授权的前台模式");
                arguments["delivery_mode"] = mode;
                var key = desktop ? "desktop_primary" : target!.Reference;
                lock (_sync) _observations.TryGetValue(key, out observation);
                if (observation == null || observation.Generation != generation || Stopwatch.GetTimestamp() - observation.Ticks > Stopwatch.Frequency * 30
                    || (string?)arguments["observation_id"] != observation.Id) throw new ControlException("StaleObservation", "请先重新观察，并传回 observation_id");
                if (target != null) { observation.Target!.Revalidate(true); }
                if (arguments["element_token"] != null && !observation.Tokens.Contains((string)arguments["element_token"]!)) throw new ControlException("StaleObservation", "元素不属于此目标的最新观察");
                if (arguments["x"] != null || arguments["y"] != null) {
                    if (arguments["x"] == null || arguments["y"] == null || observation.Width <= 0 || Stopwatch.GetTimestamp() - observation.Ticks > Stopwatch.Frequency * 10
                        || arguments["x"]!.Value<double>() < 0 || arguments["y"]!.Value<double>() < 0 || arguments["x"]!.Value<double>() >= observation.Width || arguments["y"]!.Value<double>() >= observation.Height)
                        throw new ControlException("CaptureExpired", "坐标需要 10 秒内的有效截图，且必须位于返回图片范围内");
                    if (!desktop && scopedCapture && mode == "background" && target!.IsObscured(IntPtr.Zero)) throw new ControlException("CaptureOccluded", "窗口已被遮挡，请使用新观察中的控件，或显式授权前台输入");
                }
                if (arguments["capture_id"] != null && (string?)arguments["capture_id"] != observation.Id) throw new ControlException("CaptureExpired", "截图不属于最新观察");
                if (desktop) WindowsControlProtection.RequireSafeInput(arguments, observation.Width, observation.Height);
                if (!desktop && arguments["element_token"] == null && arguments["x"] == null && name != "press_key" && name != "type_text") throw new ControlException("InvalidArguments", "窗口动作需要明确元素或截图坐标");
            }
            foreach (var key in new[] { "target_ref", "observation_id", "runtime_generation", "session_id", "request_id", "deadline", "capture_id" }) arguments.Remove(key);
            if (observation != null && observation.Width > 0) arguments["_image_size"] = new JObject { ["width"] = observation.Width, ["height"] = observation.Height };
            if (desktop && action) arguments["target"] = new JObject { ["kind"] = "desktop", ["display_id"] = "primary" };
            if (name == "get_window_state") { arguments["tree_format"] ??= "both"; arguments["max_elements"] ??= 500; arguments["timeout_ms"] ??= 2000; }
            var cancellation = _lifetime?.Token ?? new CancellationToken(true);
            var timeout = 10000;
            if (action) {
                timeout = (int)Math.Min(10000, (DateTimeOffset.Parse((string)original["deadline"]!) - DateTimeOffset.UtcNow).TotalMilliseconds);
                if (timeout <= 0) throw new ControlException("DeadlineExceeded", "动作期限已过，尚未投递");
            }
            RequireGeneration(generation); WindowsControlTarget.RequireDesktop();
            if (action && target != null) target.Revalidate();
            if (action && target != null && (string?)arguments["delivery_mode"] == "foreground") {
                await WindowsApplicationControl.ActivateAsync(target, () => { RequirePublicGrant(); RequireGeneration(generation); }).ConfigureAwait(false);
                // WPF accepts plain window-addressed keys while foreground. This avoids
                // reattaching/resetting its input queue for an unmodified key; chords and
                // other toolkits retain the native foreground input path.
                if (name == "press_key" && (arguments["modifiers"] as JArray)?.Count is null or 0
                    && WindowsApplicationControl.UsesWpfKeyboardMessages(target.Handle)) {
                    arguments["delivery_mode"] = "background"; arguments["_focus_before_key"] = true;
                }
            }
            var dispatched = false;
            try {
                dispatched = action;
                var result = await CallNativeAsync(name, arguments, cancellation, timeout).ConfigureAwait(false); RequireGeneration(generation);
                var structured = result["structuredContent"] as JObject ?? new JObject();
                // The driver's window title can lag behind a navigated document.
                // Read the live HWND caption after capture so polling and the public
                // response describe the destination rather than the previous page.
                if (!action && name == "get_window_state" && target != null)
                    structured["window_title"] = WindowsControlTarget.Resolve(target.Handle).Title;
                if (requestedWindowImage && scopedCapture && (blockedWindowImage || target!.IsObscured(IntPtr.Zero))) {
                    result["content"] = new JArray((result["content"] as JArray ?? new JArray()).Where(c => (string?)c["type"] != "image").Select(c => c.DeepClone()));
                    foreach (var field in new[] { "screenshot_width", "screenshot_height", "screenshot_mime_type", "capture_id" }) structured.Remove(field);
                    structured["capture_blocked_by_occlusion"] = true; structured["degraded"] = true;
                    structured["screenshot_error"] = "窗口被遮挡，截图已省略以避免返回覆盖窗口的内容；可继续使用控件信息。";
                }
                structured["session_id"] = _session; structured["runtime_generation"] = generation;
                if (target != null) structured["target_ref"] = target.Reference;
                if (!action && (name == "get_window_state" || desktop) && result["isError"]?.Value<bool>() != true) {
                    var fresh = new Observation { Id = Guid.NewGuid().ToString("N"), Generation = generation, Ticks = Stopwatch.GetTimestamp(), Target = target };
                    fresh.View = (JObject)structured.DeepClone();
                    foreach (var element in (structured["elements"] as JArray ?? new JArray()).OfType<JObject>()) if ((string?)element["element_token"] is string token) {
                        fresh.Tokens.Add(token);
                        if (element["frame"] is JObject frame) fresh.Points[token] = new System.Windows.Point((double?)frame["x"] + (double?)frame["w"] / 2 ?? 0, (double?)frame["y"] + (double?)frame["h"] / 2 ?? 0);
                    }
                    var image = (result["content"] as JArray)?.OfType<JObject>().FirstOrDefault(c => (string?)c["type"] == "image");
                    if (image != null && structured["screenshot_frame_valid"]?.Value<bool>() != false) {
                        using var stream = new MemoryStream(Convert.FromBase64String((string)image["data"]!)); using var bitmap = System.Drawing.Image.FromStream(stream);
                        fresh.Width = bitmap.Width; fresh.Height = bitmap.Height;
                    }
                    lock (_sync) _observations[desktop ? "desktop_primary" : target!.Reference] = fresh;
                    structured["observation_id"] = fresh.Id; structured["capture_id"] = fresh.Id;
                    structured["image_width"] = fresh.Width; structured["image_height"] = fresh.Height;
                    structured["coordinate_space"] = desktop ? "primary_display_pixels" : "window_capture_pixels";
                    structured["observation_ttl_seconds"] = 30; structured["capture_ttl_seconds"] = 10;
                }
                if (action) {
                    structured["request_id"] = original["request_id"];
                    structured["execution_state"] = result["isError"]?.Value<bool>() == true ? "Unknown" : "Completed";
                    structured["verification_state"] = "unverified"; structured["next_observation_required"] = true;
                }
                Activity = DateTime.Now.ToString("HH:mm:ss") + " · " + name + " · " + (result["isError"]?.Value<bool>() == true ? "需重新观察" : action ? "已投递，等待读回确认" : "已观察"); Changed();
                result["structuredContent"] = structured;
                DescribePublicContext(result, structured, desktop ? "desktop_primary" : target?.Reference);
                return result;
            } catch (Exception error) {
                var failure = error as ControlException ?? new ControlException("DriverFailure", "Driver 调用失败", dispatched ? "Unknown" : "NotStarted");
                if (dispatched && failure.ExecutionState == "NotStarted" && failure.Code != "ViewChanged") failure = new ControlException(failure.Code, failure.Message, "Unknown");
                var result = failure.Result(); ((JObject)result["structuredContent"]!)["request_id"] = original["request_id"];
                return result;
            } finally { if (action) lock (_sync) _observations.Remove(desktop ? "desktop_primary" : target!.Reference); }
        }
        internal static void DescribePublicContext(JObject result, JObject structured, string? targetReference)
        {
            // Driver hints use its private session/PID/since arguments. Publish a
            // next call that a client can actually send to this host's tool schema.
            if (structured["next_call"] is JObject next && structured["refusal"] == null) {
                var tool = (string?)next["tool"];
                if (tool == "get_window_state" && targetReference != null) next["arguments"] = new JObject { ["target_ref"] = targetReference };
                else if (tool == "get_desktop_state") next["arguments"] = new JObject();
                if (((string?)structured["hint"])?.Contains("run_steps") == true) structured["hint"] = "重新观察目标并确认效果后，再执行下一步。";
            }
            // MCP clients that display text content without structuredContent still
            // need the host observation reference; image blocks stay untouched.
            var context = new JObject();
            foreach (var field in new[] { "session_id", "runtime_generation", "target_ref", "observation_id", "capture_id", "image_width", "image_height", "coordinate_space", "request_id", "execution_state", "verification_state", "next_observation_required", "next_call" })
                if (structured[field] != null) context[field] = structured[field]!.DeepClone();
            if (context.Count == 0) return;
            if (!(result["content"] is JArray content)) result["content"] = content = new JArray();
            content.Add(new JObject { ["type"] = "text", ["text"] = context.ToString(Formatting.None) });
        }
        private bool Allowed(WindowsControlTarget target) => target.Pid != Process.GetCurrentProcess().Id && target.Pid != WorkerProcessId
            && (_grant?.AllowDesktop == true || _grant?.Executables.Contains(target.Executable, StringComparer.OrdinalIgnoreCase) == true);
        private void RequireGeneration(string generation) { if (!IsEnabled || _generation != generation || _lifetime?.IsCancellationRequested != false) throw new ControlException("RuntimeGenerationMismatch", "连接已更新，请重新观察，旧请求不会重放。"); }
        internal async Task<JObject> CallNativeAsync(string name, JObject args, CancellationToken cancellation, int timeout = 10000)
        {
            RequirePublicGrant();
            var connection = _connection ?? throw new ControlException("DriverUnavailable", "原生控制进程已停止");
            if (_publicRequest.Value is ControlRequestScope scope && NativeControlSchema.Actions.Contains(name)) scope.InputMayHaveExecuted = true;
            return await connection.RequestAsync("tools/call", new JObject { ["name"] = name, ["arguments"] = args }, cancellation, timeout).ConfigureAwait(false);
        }
        private static bool NativeSessionEnded(JObject result) => (string?)result["structuredContent"]?["refusal"]?["code"] is string code
            && (code == "session_ended" || code == "session_not_found" || code == "session_expired");
        private static string Text(JObject result) => string.Join("\n", (result["content"] as JArray ?? new JArray()).OfType<JObject>().Select(c => (string?)c["text"]).Where(t => t != null));
        private static JToken Canonical(JToken value) => value is JObject obj ? new JObject(obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new JProperty(p.Name, Canonical(p.Value)))) : value is JArray array ? new JArray(array.Select(Canonical)) : value.DeepClone();
        public void Dispose() {
            _disposed = true; _reconnect?.Dispose(); _reconnect = null;
            SystemEvents.SessionSwitch -= SessionChanged; SystemEvents.DisplaySettingsChanged -= DisplayChanged;
            StopRuntime(); IsEnabled = false; _token = null; _grant = null;
            // The saved switch and DPAPI credential survive normal application exit.
        }
        private sealed class Observation { internal string Id = "", Generation = ""; internal long Ticks; internal WindowsControlTarget? Target; internal int Width, Height; internal JObject View = new JObject(); internal HashSet<string> Tokens = new HashSet<string>(); internal Dictionary<string, System.Windows.Point> Points = new Dictionary<string, System.Windows.Point>(); }
    }
}
