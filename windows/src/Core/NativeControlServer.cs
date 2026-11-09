#nullable enable
using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YTray.Models;

namespace YTray.Core
{
    /// <summary>Current-user IPC used by first-party desktop clients. Independent from MCP.</summary>
    internal sealed class NativeControlServer : IDisposable
    {
        internal const string PipeName = "ytray-control-v1";
        private readonly InstanceStore _store;
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private Task? _listener;

        internal NativeControlServer(InstanceStore store) => _store = store;

        internal void Start() => _listener = Task.Run(ListenAsync);

        private async Task ListenAsync()
        {
            while (!_shutdown.IsCancellationRequested)
            {
                try
                {
                    using var pipe = CreatePipe();
                    await pipe.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);
                    using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
                    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    var response = Application.Current.Dispatcher.Invoke(() => Handle(line));
                    await writer.WriteLineAsync(response).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
                catch (Exception error)
                {
                    DiagnosticLog.Error("native-control", error);
                }
            }
        }

        private static NamedPipeServerStream CreatePipe()
        {
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            var user = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("无法识别当前 Windows 用户");
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, AccessControlType.Allow));
            return new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 4096, 4096, security);
        }

        private string Handle(string? line)
        {
            try
            {
                var requestLine = line ?? throw new InvalidOperationException("原生控制请求无效");
                if (string.IsNullOrWhiteSpace(requestLine) || Encoding.UTF8.GetByteCount(requestLine) > 64 * 1024)
                    throw new InvalidOperationException("原生控制请求无效");
                var request = JObject.Parse(requestLine);
                switch (request.Value<string>("action"))
                {
                    case "list_history":
                        return JsonConvert.SerializeObject(new
                        {
                            ok = true,
                            instances = _store.HistoryInstances
                                .OrderByDescending(instance => instance.StartedAt)
                                .Select(instance => new
                                {
                                    id = instance.Id,
                                    name = instance.Name,
                                    runtime = string.Join(" ", new[] { instance.RuntimeName, instance.RuntimeVersion }
                                        .Where(value => !string.IsNullOrWhiteSpace(value))),
                                    status = instance.Status == InstanceStatus.Failed ? "failed" : "stopped",
                                    startUrl = instance.StartURL,
                                    pageTitle = instance.LastPageTitle ?? "",
                                    pageUrl = instance.LastPageURL ?? "",
                                    badge = instance.DockBadge ?? "",
                                    startedAt = new DateTimeOffset(instance.StartedAt).ToUnixTimeMilliseconds(),
                                }),
                        });
                    case "claim_launch_approval":
                        if (!Guid.TryParse(request.Value<string>("id"), out var approvalID))
                            throw new InvalidOperationException("浏览器实例 ID 无效");
                        var requestID = request.Value<string>("requestId")?.Trim() ?? "";
                        if (requestID.Length < 1 || requestID.Length > 160
                            || requestID.Any(char.IsControl))
                            throw new InvalidOperationException("配对请求 ID 无效");
                        var approved = _store.TryClaimYakitLaunch(approvalID, requestID, out var reason);
                        return JsonConvert.SerializeObject(new { ok = true, approved, reason });
                    case "restore_history":
                        if (_store.IsLaunching) throw new InvalidOperationException("YTray 正在启动另一个浏览器实例");
                        if (!Guid.TryParse(request.Value<string>("id"), out var id))
                            throw new InvalidOperationException("浏览器实例 ID 无效");
                        var history = _store.HistoryInstances.FirstOrDefault(instance => instance.Id == id)
                            ?? throw new InvalidOperationException("没有找到可恢复的浏览器实例");
                        _store.RestoreHistory(history);
                        return JsonConvert.SerializeObject(new { ok = true });
                    default:
                        throw new InvalidOperationException("不支持的原生控制命令");
                }
            }
            catch (Exception error)
            {
                return JsonConvert.SerializeObject(new { ok = false, error = error.Message });
            }
        }

        public void Dispose()
        {
            _shutdown.Cancel();
            try
            {
                using var wake = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                wake.Connect(100);
            }
            catch { }
            try { _listener?.Wait(500); } catch { }
            _shutdown.Dispose();
        }
    }
}
