#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YTray.Models;
using YTray.Native;

namespace YTray.Core.SystemControl
{
    internal interface ICuaDriverTransport : IDisposable
    {
        Task<JArray> StartAsync(string executable, IDictionary<string, string> environment, CancellationToken cancellation);
        Task<JObject> RequestAsync(string method, JObject parameters, CancellationToken cancellation, int timeoutMs = 10000);
    }
    internal sealed class CuaDriverConnection : ICuaDriverTransport
    {
        internal const int MaximumFrameBytes = 24 * 1024 * 1024;
        private readonly ConcurrentDictionary<long, TaskCompletionSource<JObject>> _pending = new ConcurrentDictionary<long, TaskCompletionSource<JObject>>();
        private readonly SemaphoreSlim _writer = new SemaphoreSlim(1, 1);
        private DriverProcessLauncher? _process;
        private int _stopped;
        private long _next;
        private readonly StringBuilder _stderr = new StringBuilder();
        internal string DiagnosticTail { get { lock (_stderr) return _stderr.ToString(); } }
        internal Action? OnExit { get; set; }
        internal int? ProcessId => _process?.Process.Id;
        public async Task<JArray> StartAsync(string executable, IDictionary<string, string> environment, CancellationToken cancellation)
        {
            _process = DriverProcessLauncher.Start(executable, environment);
            var output = _process.Output; var error = _process.Error;
            _ = Task.Run(() => Read(output));
            _ = Task.Run(() => { try { using var reader = new StreamReader(error, new UTF8Encoding(false, true)); var buffer = new char[2048]; int count;
                while ((count = reader.Read(buffer, 0, buffer.Length)) > 0) lock (_stderr) { _stderr.Append(buffer, 0, count); if (_stderr.Length > 65536) _stderr.Remove(0, _stderr.Length - 65536); }
            } catch { } });
            var initialized = await RequestAsync("initialize", new JObject { ["protocolVersion"] = "2025-11-25", ["capabilities"] = new JObject(),
                ["clientInfo"] = new JObject { ["name"] = "YTray", ["version"] = "1" } }, cancellation).ConfigureAwait(false);
            var version = (string?)initialized["protocolVersion"];
            if (version != "2025-11-25" && version != "2025-06-18") throw new ControlException("VersionIncompatible", "Driver MCP 协议不兼容");
            await WriteAsync(new JObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, cancellation).ConfigureAwait(false);
            var tools = new JArray(); string? cursor = null;
            for (var page = 0; page < 20; page++) {
                var response = await RequestAsync("tools/list", cursor == null ? new JObject() : new JObject { ["cursor"] = cursor }, cancellation).ConfigureAwait(false);
                foreach (var tool in response["tools"] as JArray ?? new JArray()) tools.Add(tool.DeepClone());
                cursor = (string?)response["nextCursor"]; if (cursor == null) return tools;
            }
            throw new ControlException("ProtocolViolation", "Driver 工具分页超限");
        }
        public async Task<JObject> RequestAsync(string method, JObject parameters, CancellationToken cancellation, int timeoutMs = 10000)
        {
            if (Volatile.Read(ref _stopped) != 0) throw new ControlException("DriverUnavailable", "Driver 已停止");
            var id = Interlocked.Increment(ref _next); var completion = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(id, completion)) throw new InvalidOperationException();
            using var timeout = new CancellationTokenSource(timeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeout.Token);
            using var registration = linked.Token.Register(() => completion.TrySetCanceled());
            try {
                await WriteAsync(new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }, linked.Token).ConfigureAwait(false);
                return await completion.Task.ConfigureAwait(false);
            } catch (OperationCanceledException) { Dispose(); throw new ControlException(timeout.IsCancellationRequested ? "DeadlineExceeded" : "Cancelled", "请求已中断；若已投递，请重新观察，勿自动重放。", "Unknown"); }
            finally { _pending.TryRemove(id, out _); }
        }
        private async Task WriteAsync(JObject message, CancellationToken cancellation)
        {
            var bytes = new UTF8Encoding(false, true).GetBytes(message.ToString(Formatting.None) + "\n");
            if (bytes.Length > 1024 * 1024) throw new ControlException("InvalidArguments", "请求过大");
            await _writer.WaitAsync(cancellation).ConfigureAwait(false);
            try { var stream = _process?.Input ?? throw new IOException("Driver stopped"); await stream.WriteAsync(bytes, 0, bytes.Length, cancellation).ConfigureAwait(false); await stream.FlushAsync(cancellation).ConfigureAwait(false); }
            finally { _writer.Release(); }
        }
        internal static IEnumerable<string> Frames(Stream stream)
        {
            using var frame = new MemoryStream(); var buffer = new byte[8192]; int count;
            var utf8 = new UTF8Encoding(false, true);
            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0) {
                for (var i = 0; i < count; i++) {
                    if (buffer[i] == 10) {
                        var bytes = frame.ToArray(); var length = bytes.Length; if (length > 0 && bytes[length - 1] == 13) length--;
                        if (length == 0) throw new InvalidDataException("Empty protocol frame");
                        yield return utf8.GetString(bytes, 0, length); frame.SetLength(0);
                    } else { if (frame.Length >= MaximumFrameBytes) throw new InvalidDataException("Protocol frame too large"); frame.WriteByte(buffer[i]); }
                }
            }
            if (frame.Length != 0) throw new InvalidDataException("Incomplete protocol frame");
        }
        private void Read(Stream stream)
        {
            try {
                foreach (var frame in Frames(stream)) {
                    var message = JObject.Parse(frame);
                    if ((string?)message["jsonrpc"] != "2.0") throw new InvalidDataException("Invalid JSON-RPC");
                    if (message["id"] == null) continue;
                    if (message["id"]!.Type != JTokenType.Integer) throw new InvalidDataException("Invalid response id");
                    var id = message["id"]!.Value<long>();
                    if (!_pending.TryRemove(id, out var pending)) continue;
                    if (message["error"] != null) pending.TrySetException(new ControlException("DriverFailure", "Driver 拒绝了内部协议请求"));
                    else if (message["result"] is JObject result) pending.TrySetResult(result);
                    else pending.TrySetException(new ControlException("ProtocolViolation", "Driver 缺少结果"));
                }
            } catch { } finally { Dispose(); OnExit?.Invoke(); }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
            _process?.Dispose(); _process = null;
            foreach (var pair in _pending) if (_pending.TryRemove(pair.Key, out var pending)) pending.TrySetException(new ControlException("DriverUnavailable", "Driver 已退出，请重新连接并观察。", "Unknown"));
        }
    }
}
