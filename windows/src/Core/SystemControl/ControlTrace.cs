#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace YTray.Core.SystemControl
{
    internal sealed class ControlTraceCall
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Lazy<JObject> _request;
        private volatile Completion? _completion;
        internal readonly string Id = Guid.NewGuid().ToString("N");
        internal readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;
        internal string Tool { get; }
        internal string Label { get; }
        internal string TaskId { get; }
        internal string Session { get; }
        internal string ClientName { get; }
        internal string RunId { get; }
        internal volatile ControlOperation? Operation;
        internal bool IsFinished => _completion != null;
        internal string Status => _completion?.Status ?? "running";
        internal long? Duration => _completion?.Duration;

        private sealed class Completion
        {
            internal readonly int HttpStatus;
            internal readonly long Duration;
            internal readonly string Status;
            internal readonly Lazy<JObject?> Response;

            internal Completion(int httpStatus, long duration, JObject? result, JObject? error)
            {
                HttpStatus = httpStatus;
                Duration = duration;
                Status = httpStatus == 202 || (string?)result?["structuredContent"]?["error"]?["code"] == "Cancelled" ? "cancelled"
                    : httpStatus >= 400 || error != null || result?.Value<bool?>("isError") == true ? "failed" : "completed";
                Response = new Lazy<JObject?>(() => result != null ? ProjectResult(result) : error == null ? null : new JObject { ["error"] = error.DeepClone() });
            }
        }

        internal ControlTraceCall(string runtime, string session, string clientName, JToken requestId, string tool, JObject arguments)
        {
            Tool = tool.Length <= 160 ? tool : tool.Substring(0, 160) + "…";
            Label = ControlOperation.ToolTitle(Tool, arguments);
            TaskId = arguments["task_id"]?.Type == JTokenType.String && (string?)arguments["task_id"] is string id
                && ControlTask.IsValidId(id) ? id : "";
            Session = session;
            ClientName = clientName;
            RunId = RunKey(TaskId, (session.Length == 0 ? runtime : session) + "/");
            _request = new Lazy<JObject>(() => new JObject { ["id"] = Bounded(requestId, 1024), ["name"] = Tool, ["arguments"] = Bounded(arguments, 128 * 1024) });
        }

        internal static string RunKey(string? taskId, string fallback) => taskId != null && ControlTask.IsValidId(taskId)
            ? "task:" + taskId : fallback;

        internal void Finish(int status, JObject? result, JObject? error) =>
            _completion = new Completion(status, _clock.ElapsedMilliseconds, result, error);

        internal JObject Snapshot()
        {
            var completion = _completion;
            var record = new JObject {
                ["id"] = Id, ["run_id"] = RunId, ["task_id"] = TaskId, ["session"] = Session, ["client"] = ClientName, ["tool"] = Tool, ["label"] = Label,
                ["started_at"] = StartedAt.ToString("O", CultureInfo.InvariantCulture),
                ["status"] = completion?.Status ?? "running", ["request"] = _request.Value.DeepClone()
            };
            if (Operation is ControlOperation operation) record["operation"] = operation.Snapshot();
            if (completion != null) {
                record["duration_ms"] = completion.Duration;
                record["http_status"] = completion.HttpStatus;
                if (completion.Response.Value is JObject response) record["response"] = response.DeepClone();
            }
            return record;
        }

        private static JObject ProjectResult(JObject result)
        {
            var projection = new JObject();
            foreach (var field in result.Properties()) {
                if (field.Name == "content" && field.Value is JArray blocks) {
                    projection[field.Name] = new JArray(blocks.Select(block => block is JObject media && (string?)media["type"] is "image" or "audio"
                        ? new JObject { ["type"] = media["type"]!.DeepClone(), ["mimeType"] = (string?)media["mimeType"], ["data_recorded"] = false }
                        : Bounded(block, 96 * 1024)));
                } else projection[field.Name] = Bounded(field.Value, 128 * 1024);
            }
            return projection;
        }

        private static JToken Bounded(JToken value, int limit)
        {
            var text = value.ToString(Formatting.None);
            return text.Length <= limit ? value.DeepClone()
                : new JObject { ["truncated"] = true, ["preview"] = text.Substring(0, limit) };
        }
    }

    internal sealed class ControlTrace : IDisposable
    {
        private const int CallLimit = 300;
        private const long ByteLimit = 24 * 1024 * 1024;
        private readonly string _directory;
        private readonly string _runtime = Guid.NewGuid().ToString("N");
        private readonly object _sync = new object();
        private readonly List<ControlTraceCall> _recent = new List<ControlTraceCall>();
        private readonly Dictionary<string, string> _clients = new Dictionary<string, string>();
        private readonly Queue<ControlTraceCall> _pending = new Queue<ControlTraceCall>();
        private Task? _writer;
        private bool _disposed, _directoryReady;
        internal event Action? Changed;

        internal ControlTrace(string applicationDirectory) => _directory = Path.Combine(applicationDirectory, "ComputerControl", "Trace");

        internal void RegisterClient(string session, string name)
        {
            name = name.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (name.Length == 0) return;
            lock (_sync) {
                if (_clients.Count >= 128) _clients.Remove(_clients.Keys.First());
                _clients[session] = name.Length <= 80 ? name : name.Substring(0, 79) + "…";
            }
        }

        internal ControlTraceCall Begin(string session, JToken requestId, string tool, JObject arguments)
        {
            // The first 32 characters identify the session. Its authentication signature is never recorded.
            var sessionId = session.Length <= 32 ? session : session.Substring(0, 32);
            ControlTraceCall call;
            lock (_sync) {
                _clients.TryGetValue(sessionId, out var client);
                call = new ControlTraceCall(_runtime, sessionId, client ?? "", requestId, tool, arguments);
                _recent.Add(call);
                if (_recent.Count > CallLimit) {
                    var oldest = _recent.FindIndex(entry => entry.IsFinished);
                    _recent.RemoveAt(Math.Max(0, oldest));
                }
            }
            Changed?.Invoke();
            return call;
        }

        internal void Finish(ControlTraceCall call, int status, JObject? result, JObject? error = null)
        {
            call.Finish(status, result, error);
            lock (_sync) {
                if (!_disposed) {
                    if (_pending.Count == CallLimit) _pending.Dequeue();
                    _pending.Enqueue(call);
                    _writer ??= Task.Run(WritePending);
                }
            }
            Changed?.Invoke();
        }

        internal ControlTraceCall[] Recent()
        {
            lock (_sync) return _recent.ToArray();
        }

        internal Task<JObject[]> ReadSavedAsync() => Task.Run(() => {
            var entries = new List<JObject>();
            try {
                foreach (var file in Files().Take(CallLimit)) {
                    try {
                        using var reader = new JsonTextReader(file.OpenText()) { DateParseHandling = DateParseHandling.None, MaxDepth = 80 };
                        var record = JObject.Load(reader);
                        record["run_id"] = ControlTraceCall.RunKey((string?)record["task_id"], (string?)record["run_id"] ?? "");
                        entries.Add(record);
                    } catch (IOException) { }
                    catch (JsonException) { }
                }
            } catch (Exception error) { DiagnosticLog.Error("control.trace", error, "could not read call history"); }
            return entries.ToArray();
        });

        internal Task FlushAsync()
        {
            lock (_sync) return _writer ?? Task.CompletedTask;
        }

        private IEnumerable<FileInfo> Files() => Directory.Exists(_directory)
            ? new DirectoryInfo(_directory).EnumerateFiles("trace-*.json").OrderByDescending(file => file.Name, StringComparer.Ordinal)
            : Enumerable.Empty<FileInfo>();

        private void WritePending()
        {
            while (true) {
                ControlTraceCall call;
                lock (_sync) {
                    if (_pending.Count == 0) { _writer = null; return; }
                    call = _pending.Dequeue();
                }
                try {
                    if (!_directoryReady) { ControlAccessStore.PrivateDirectory(_directory); _directoryReady = true; }
                    var file = Path.Combine(_directory, "trace-" + call.StartedAt.UtcDateTime.Ticks.ToString("D19", CultureInfo.InvariantCulture) + "-" + call.Id + ".json");
                    var staging = file + ".tmp";
                    try {
                        File.WriteAllText(staging, call.Snapshot().ToString(Formatting.None), new UTF8Encoding(false));
                        File.Move(staging, file);
                    } finally { if (File.Exists(staging)) File.Delete(staging); }
                    var count = 0;
                    long bytes = 0;
                    foreach (var saved in Files()) {
                        bytes += saved.Length;
                        if (++count > CallLimit || bytes > ByteLimit) saved.Delete();
                    }
                } catch (Exception error) {
                    _directoryReady = false;
                    DiagnosticLog.Error("control.trace", error, "could not persist call history");
                }
            }
        }

        public void Dispose()
        {
            lock (_sync) _disposed = true;
            FlushAsync().Wait(1000);
        }
    }
}
