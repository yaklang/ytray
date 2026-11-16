#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using YTray.Models;

namespace YTray.Core
{
    // The browser connection stays open for the lifetime of the launched instance so
    // new tabs receive their environment before they execute or send their first request.
    internal sealed class BrowserEnvironmentSession : IDisposable
    {
        private readonly BrowserDevTools _browser = new BrowserDevTools();
        private readonly BrowserEnvironment _environment;
        private readonly CancellationTokenSource _lifetime;
        private readonly CancellationToken _token;
        private readonly SemaphoreSlim _send = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JObject>> _pending = new ConcurrentDictionary<int, TaskCompletionSource<JObject>>();
        private readonly TaskCompletionSource<bool> _initialPage = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _nextId;
        private int _disposed;

        internal BrowserEnvironmentSession(BrowserEnvironment environment, CancellationToken cancellation)
        {
            _environment = environment;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            _token = _lifetime.Token;
        }

        internal bool IsConnected => !_token.IsCancellationRequested && _initialPage.Task.Status == TaskStatus.RanToCompletion;

        internal async Task StartAsync(int port)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await _browser.ConnectAsync(port, deadline.Token).ConfigureAwait(false);
            _ = ReadMessagesAsync();
            await CommandAsync("Target.setAutoAttach", new JObject
            {
                ["autoAttach"] = true,
                ["waitForDebuggerOnStart"] = true,
                ["flatten"] = true,
                ["filter"] = new JArray(new JObject { ["type"] = "page" }, new JObject { ["exclude"] = true })
            }, deadline.Token).ConfigureAwait(false);
            using var cancel = deadline.Token.Register(() => _initialPage.TrySetCanceled());
            await _initialPage.Task.ConfigureAwait(false);
        }

        private async Task ReadMessagesAsync()
        {
            try
            {
                while (!_token.IsCancellationRequested)
                {
                    var message = await _browser.ReceiveMessageAsync(_token).ConfigureAwait(false);
                    if ((int?)message["id"] is int id && _pending.TryGetValue(id, out var reply))
                    {
                        if (message["error"] is JObject error)
                            reply.TrySetException(new DevToolsCommandException((int?)error["code"] ?? 0, (string?)error["message"] ?? "浏览器环境设置失败"));
                        else reply.TrySetResult(message["result"] as JObject ?? new JObject());
                    }
                    else if ((string?)message["method"] == "Target.attachedToTarget" && message["params"] is JObject attached)
                    {
                        _ = PreparePageAsync(attached);
                    }
                }
            }
            catch (Exception error)
            {
                if (!_token.IsCancellationRequested) DiagnosticLog.Error("browser.environment.connection", error);
                _initialPage.TrySetException(error);
                Dispose();
            }
        }

        private async Task PreparePageAsync(JObject attached)
        {
            var sessionId = (string?)attached["sessionId"];
            var targetId = (string?)attached["targetInfo"]?["targetId"];
            if (sessionId == null || targetId == null) return;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_token);
            deadline.CancelAfter(TimeSpan.FromSeconds(6));
            var token = deadline.Token;
            try
            {
                // A link-created tab can keep renderer commands pending until it is
                // resumed. Queue the settings before resume and then await all replies.
                var setup = new List<Task<JObject>>(3);
                if (_environment.TimeZone.Length != 0)
                    setup.Add(CommandAsync("Emulation.setTimezoneOverride", new JObject { ["timezoneId"] = _environment.TimeZone }, token, sessionId));
                if (_environment.ViewportWidth is int width && _environment.ViewportHeight is int height)
                    setup.Add(CommandAsync("Emulation.setDeviceMetricsOverride", new JObject {
                        ["width"] = width, ["height"] = height, ["deviceScaleFactor"] = 0, ["mobile"] = false
                    }, token, sessionId));
                if ((bool?)attached["waitingForDebugger"] == true)
                    setup.Add(CommandAsync("Runtime.runIfWaitingForDebugger", new JObject(), token, sessionId));
                await Task.WhenAll(setup).ConfigureAwait(false);
                _initialPage.TrySetResult(true);
            }
            catch (Exception error)
            {
                if (_token.IsCancellationRequested) return;
                if (error is DevToolsCommandException command && command.Code == -32001) return; // The tab was closed.
                _initialPage.TrySetException(error);
                DiagnosticLog.Error("browser.environment.page", error);
                // Fail this navigation instead of sending the request with default environment values.
                using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(_token);
                cleanup.CancelAfter(TimeSpan.FromSeconds(2));
                try { await CommandAsync("Target.closeTarget", new JObject { ["targetId"] = targetId }, cleanup.Token).ConfigureAwait(false); }
                catch (Exception closeError) when (!_token.IsCancellationRequested) { DiagnosticLog.Error("browser.environment.close-page", closeError); }
                catch (Exception) when (_token.IsCancellationRequested) { }
            }
        }

        private async Task<JObject> CommandAsync(string method, JObject parameters, CancellationToken token, string? sessionId = null)
        {
            var id = Interlocked.Increment(ref _nextId);
            var request = new JObject { ["id"] = id, ["method"] = method, ["params"] = parameters };
            if (sessionId != null) request["sessionId"] = sessionId;
            var reply = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = reply;
            using var registration = token.Register(() => reply.TrySetCanceled());
            try
            {
                await _send.WaitAsync(token).ConfigureAwait(false);
                try { await _browser.SendMessageAsync(request, token).ConfigureAwait(false); }
                finally { _send.Release(); }
                return await reply.Task.ConfigureAwait(false);
            }
            finally { _pending.TryRemove(id, out _); }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _lifetime.Cancel();
            _browser.Dispose();
            _lifetime.Dispose();
        }
    }
}
