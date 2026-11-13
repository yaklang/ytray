#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using YTray.Native;

namespace YTray.Core.SystemControl
{
    public sealed partial class SystemControlController
    {
        private long _feedbackSequence;
        private WindowsActionOverlay? _previewOverlay;
        internal bool IsPipelinePreviewVisible { get; private set; }
        private volatile ControlOperation? _activeOperation, _latestFailure;
        internal ControlOperation? LatestFailure => _latestFailure;
        internal event Action<string>? TraceDetailsRequested;

        internal static bool HasOperationFeedback(string name) => name is "launch_app" or "open_folder" or "activate_window" or "click" or "type_text" or "press_key" or "scroll";

        internal void ToggleOperationPreview()
        {
            if (IsPipelinePreviewVisible) {
                _previewOverlay?.DismissOperation();
                IsPipelinePreviewVisible = false;
                Changed();
                return;
            }
            var id = Interlocked.Increment(ref _feedbackSequence);
            var labels = new[] { "查找应用", "打开百度", "观察窗口", "打开 B 站", "切换窗口", "读回界面" };
            var calls = new JArray(labels.Select((label, index) => new JObject {
                ["id"] = index == labels.Length - 1 ? id : 0, ["position"] = index + 1,
                ["label"] = label, ["phase"] = "completed"
            }));
            var task = new JObject { ["preview"] = true, ["call_count"] = labels.Length, ["calls"] = calls };
            _previewOverlay ??= new WindowsActionOverlay();
            IsPipelinePreviewVisible = _previewOverlay.Operation(id, "completed", new IntPtr(WindowsApplicationControl.ForegroundWindow),
                "", "打开百度和 B 站", task: task, showEdges: false);
            Changed();
        }

        private void EnterOperation(ControlTiming? timing, string step)
        {
            if (timing?.Operation is ControlOperation operation && operation.Enter(step)) PublishOperation(operation);
        }

        private void FinishOperation(ControlOperation? operation, JObject? error)
        {
            if (operation == null || !operation.Finish(error)) return;
            if (error != null) {
                operation.Task?.Fail(operation);
                _latestFailure = operation;
                Changed();
            }
            PublishOperation(operation);
        }

        private void PublishOperation(ControlOperation operation)
        {
            if (operation.Task == null && !HasOperationFeedback(operation.Tool)) return;
            if (_connection is NativeControlConnection connection && _lifetime is CancellationTokenSource lifetime) {
                var snapshot = operation.Snapshot();
                if (operation.Task != null) snapshot["task"] = operation.Task.Summary(64);
                _ = NotifyOperationFeedbackAsync(connection, lifetime.Token, snapshot);
            }
        }

        private JObject CompleteOperation(ControlTiming timing, JObject result, JObject? arguments = null)
        {
            var failed = result["isError"]?.Value<bool>() == true;
            var structured = result["structuredContent"] as JObject;
            var error = structured?["error"] as JObject;
            if (failed) timing.Error = (string?)error?["code"] ?? "Unknown";
            var operation = timing.Operation;
            if (operation == null || structured == null) return result;
            if (failed && error == null) {
                error = new JObject { ["code"] = "ActionIncomplete", ["message"] = ActionErrorMessage(result), ["execution_state"] = "Unknown" };
                structured["error"] = error;
            }
            if (!operation.Failed && error != null && operation.CurrentStep == "readback" && HasOperationFeedback(operation.Tool))
                error["execution_state"] = "Completed";
            FinishOperation(operation, error);
            var snapshot = operation.Snapshot();
            if (error != null && snapshot["error"]?["step"] is JToken step) {
                error["step"] = step.DeepClone();
                if (result["content"]?[0] is JObject block && (string?)block["text"] is string text) {
                    var reason = (string?)error["message"] ?? "";
                    block["text"] = "失败阶段 " + ControlOperation.StepTitle((string?)step) + "\n"
                        + (reason.Length != 0 && !text.StartsWith(reason, StringComparison.Ordinal) ? reason + "\n" : "") + text;
                }
            }
            structured["operation"] = snapshot;
            if (operation.Task is ControlTask task) {
                RecordTaskObservation(operation, structured, failed, arguments);
                var taskSummary = task.Summary();
                structured["task"] = taskSummary;
                if (taskSummary["checkpoint"]?["id"] is JToken checkpoint && result["content"] is JArray content)
                    content.Add(new JObject { ["type"] = "text", ["text"] = "任务断点 resume_from " + checkpoint });
            }
            return result;
        }

        private static async Task NotifyOperationFeedbackAsync(NativeControlConnection connection, CancellationToken cancellation, JObject state)
        {
            try { await connection.NotifyAsync("feedback", state, cancellation).ConfigureAwait(false); }
            catch { /* Feedback is best effort. */ }
        }

        private void OnWorkerNotification(NativeControlConnection connection, string method, JObject parameters)
        {
            if (!ReferenceEquals(_connection, connection)) return;
            if (method == "operation/progress" && parameters["operation_id"]?.Type == JTokenType.Integer) {
                var operation = _activeOperation;
                if (operation?.Id == parameters.Value<long>("operation_id") && (string?)parameters["step"] is string step) operation.Enter(step);
            } else if (method == "operation/show_trace" && (string?)parameters["trace_id"] is string id
                && Guid.TryParseExact(id, "N", out _) && !_dispatcher.HasShutdownStarted) {
                _dispatcher.BeginInvoke(new Action(() => TraceDetailsRequested?.Invoke(id)));
            }
        }

        private ControlOperation DescribeOperation(string name, JObject arguments)
        {
            var window = (long?)arguments["window_id"] ?? 0;
            var context = "电脑桌面"; var control = ""; var role = "";
            string? elementToken = null;
            WindowsControlTarget? target;
            lock (_sync) {
                target = _targets.Values.FirstOrDefault(item => item.Handle.ToInt64() == window);
                if (target != null) {
                    context = target.Title;
                    if (arguments["element_index"] != null && _observations.TryGetValue(target.Reference, out var observation)) {
                        var selected = (observation.View["elements"] as JArray)?.OfType<JObject>()
                            .FirstOrDefault(element => (int?)element["element_index"] == (int?)arguments["element_index"]);
                        control = (string?)selected?["label"] ?? ""; role = (string?)selected?["role"] ?? "";
                        elementToken = (string?)selected?["element_token"];
                    }
                }
            }
            string description;
            switch (name) {
                case "launch_app":
                    var app = (string?)arguments["app"] ?? "应用";
                    try { app = Path.GetFileNameWithoutExtension(app); } catch (ArgumentException) { app = "应用"; }
                    description = "打开 " + (app.Equals("chrome", StringComparison.OrdinalIgnoreCase) ? "Google Chrome" : app.Equals("msedge", StringComparison.OrdinalIgnoreCase) ? "Microsoft Edge" : app);
                    if (Uri.TryCreate((string?)arguments["url"], UriKind.Absolute, out var uri)) description += "，进入 " + uri.Host;
                    context = "启动应用并切换到前台"; break;
                case "open_folder": description = "打开文件夹"; context = "Windows 文件夹"; break;
                case "activate_window": description = "切换到目标窗口"; break;
                case "click":
                    description = (string?)arguments["button"] == "right" ? "右键点击" : (int?)arguments["count"] == 2 ? "双击" : "点击";
                    description += role == "Edit" ? "输入框" : control.Length > 0 ? "「" + control + "」" : "界面上的指定位置"; break;
                case "type_text":
                    description = "输入 " + (((string?)arguments["text"] ?? "").Length) + " 个字符";
                    if (arguments["replace"]?.Value<bool>() == true) description += "，替换当前内容";
                    if (arguments["submit"]?.Value<bool>() == true) description += "并按回车提交";
                    break;
                case "press_key": description = "按下 " + ((string?)arguments["key"] ?? "").ToUpperInvariant().Replace("+", " + "); break;
                case "scroll":
                    var direction = (string?)arguments["direction"];
                    description = "滚动页面（" + (direction == "up" ? "向上" : direction == "left" ? "向左" : direction == "right" ? "向右" : "向下") + "）"; break;
                default: description = ControlOperation.ToolTitle(name); break;
            }
            return new ControlOperation(Interlocked.Increment(ref _feedbackSequence), name, window, ControlOperation.ToolTitle(name, arguments),
                description, context, target, elementToken);
        }
    }
}
