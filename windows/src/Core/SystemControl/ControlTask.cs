#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Newtonsoft.Json.Linq;
using YTray.Models;
using YTray.Native;

namespace YTray.Core.SystemControl
{
    internal sealed class ControlTask
    {
        private readonly object _sync = new object();
        private readonly List<ControlOperation> _calls = new List<ControlOperation>();
        private ControlOperation? _checkpoint;
        private string? _observedState;
        private long _observedAt, _count;
        internal string Id { get; }
        internal ControlOperation? Checkpoint { get { lock (_sync) return _checkpoint; } }
        internal string? ObservedState { get { lock (_sync) return _observedState; } }
        internal long LastUsed { get; set; }

        internal ControlTask(string id) => Id = id;

        internal static bool IsValidId(string id) => id.Length <= 96 && !string.IsNullOrWhiteSpace(id)
            && id == id.Trim() && !id.Any(char.IsControl);

        internal static bool NeedsObservation(ControlOperation failure)
        {
            var error = failure.Failure;
            return (string?)error?["execution_state"] != "NotStarted"
                || error?.Value<bool?>("selection_sent") == true || error?.Value<bool?>("text_sent") == true;
        }

        internal bool HasRecentObservation
        {
            get { lock (_sync) return _observedAt != 0 && Stopwatch.GetTimestamp() - _observedAt < 30L * Stopwatch.Frequency; }
        }

        internal void Begin(ControlOperation operation, long? resumedFrom)
        {
            lock (_sync) {
                operation.Task = this;
                operation.TaskPosition = ++_count;
                operation.ResumedFrom = resumedFrom;
                if (resumedFrom.HasValue) ClearCheckpoint();
                _calls.Add(operation);
                if (_calls.Count > 64) _calls.RemoveAt(_calls[0] == _checkpoint ? 1 : 0);
            }
        }

        internal void Fail(ControlOperation operation)
        {
            lock (_sync) {
                if (_checkpoint != null) return;
                _checkpoint = operation;
                _observedAt = 0;
                _observedState = null;
            }
        }

        internal void Observe(string? stateId)
        {
            lock (_sync) {
                if (_checkpoint == null) return;
                _observedAt = Stopwatch.GetTimestamp();
                _observedState = stateId;
            }
        }

        private void ClearCheckpoint()
        {
            _checkpoint = null;
            _observedAt = 0;
            _observedState = null;
        }

        internal ControlOperation[] Calls()
        {
            lock (_sync) return _calls.ToArray();
        }

        internal JObject Summary(int calls = 8)
        {
            lock (_sync) {
                var result = new JObject {
                    ["task_id"] = Id, ["call_count"] = _count, ["phase"] = _checkpoint == null ? "active" : "interrupted",
                    ["calls"] = new JArray(_calls.Skip(Math.Max(0, _calls.Count - calls)).Select(operation => operation.Summary()))
                };
                if (_checkpoint != null) {
                    result["checkpoint"] = _checkpoint.Snapshot();
                    result["checkpoint"]!["needs_observation"] = NeedsObservation(_checkpoint) && !HasRecentObservation;
                }
                return result;
            }
        }
    }

    public sealed partial class SystemControlController
    {
        private readonly Dictionary<string, ControlTask> _tasks = new Dictionary<string, ControlTask>(StringComparer.Ordinal);

        private ControlTask? FindTask(JObject arguments)
        {
            if (!((string?)arguments["task_id"] is string id)) {
                if (arguments["resume_from"] != null) throw new ControlException("InvalidTaskId", "继续任务时需传入原 task_id。");
                return null;
            }
            if (!ControlTask.IsValidId(id))
                throw new ControlException("InvalidTaskId", "task_id 需要是 1–96 个字符的任务标识。");
            lock (_tasks) {
                if (!_tasks.TryGetValue(id, out var task)) {
                    if (_tasks.Count >= 64) {
                        var oldest = _tasks.OrderBy(pair => pair.Value.LastUsed).First();
                        _tasks.Remove(oldest.Key);
                    }
                    _tasks.Add(id, task = new ControlTask(id));
                }
                task.LastUsed = Stopwatch.GetTimestamp();
                return task;
            }
        }

        private JObject? CheckTaskResume(ControlTask? task, bool action, long? resumedFrom)
        {
            if (task == null) return null;
            var failure = task.Checkpoint;
            if (failure == null)
                return resumedFrom.HasValue ? TaskError(task, "TaskCheckpointChanged", "任务当前没有这个断点。请核对返回的任务状态。") : null;
            if (!action && !resumedFrom.HasValue) return null;
            if (resumedFrom != failure.Id)
                return TaskError(task, "TaskInterrupted", "任务在失败节点暂停。核对失败详情后，用 resume_from 指定该节点，并调用下一项需要执行的操作。");
            if (ControlTask.NeedsObservation(failure) && !HasCurrentTaskObservation(task, failure))
                return TaskError(task, "TaskObservationRequired", "请带同一个 task_id 重新观察失败窗口。窗口已关闭或启动结果不明时可先读取窗口列表。");
            return null;
        }

        private bool HasCurrentTaskObservation(ControlTask task, ControlOperation failure)
        {
            if (!task.HasRecentObservation) return false;
            var id = task.ObservedState;
            if (id == null) return true; // A fresh window list showed the target is absent or unknown.
            var target = failure.Target;
            lock (_sync) return _observations.Values.Any(observation => observation.Id == id && observation.Generation == _generation
                && Stopwatch.GetTimestamp() - observation.Ticks < 30L * Stopwatch.Frequency
                && (failure.Desktop ? observation.Target == null
                    : target != null && observation.Target is WindowsControlTarget current && current.Handle == target.Handle
                        && current.Pid == target.Pid && current.Created == target.Created));
        }

        private static JObject TaskError(ControlTask task, string code, string message)
        {
            var result = new ControlException(code, message).Result();
            var summary = task.Summary();
            if (code == "TaskObservationRequired" && summary["checkpoint"] is JObject failure) failure["needs_observation"] = true;
            result["structuredContent"]!["task"] = summary;
            if (summary["checkpoint"]?["id"] is JToken id)
                ((JArray)result["content"]!).Add(new JObject { ["type"] = "text", ["text"] = "任务断点 resume_from " + id });
            return result;
        }

        private void RecordTaskObservation(ControlOperation operation, JObject structured, bool failed, JObject? arguments)
        {
            var task = operation.Task;
            var checkpoint = task?.Checkpoint;
            if (task == null || checkpoint == null || failed && checkpoint != operation) return;
            var target = checkpoint.Target;
            if (structured["state_id"]?.Type == JTokenType.String && structured.Value<long?>("window_id") is long window &&
                (target?.Handle.ToInt64() == window || checkpoint.Desktop && window == 0)
                && (target == null || operation.Target is WindowsControlTarget observed
                    && observed.Pid == target.Pid && observed.Created == target.Created)) {
                task.Observe((string?)structured["state_id"]);
            } else if (!failed && operation.Tool == "list_windows" && string.IsNullOrEmpty((string?)arguments?["app"])
                && !checkpoint.Desktop && structured["windows"] is JArray windows
                && (target == null || !windows.Any(item => item.Value<long>("window_id") == target.Handle.ToInt64()))) {
                task.Observe(null);
            }
        }
    }
}
