#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YTray.Models;

namespace YTray.Core
{
    internal sealed class ExtensionLoadingException : Exception
    {
        internal ExtensionLoadingException(string message, Exception? inner = null) : base(message, inner) { }
    }

    internal sealed class DevToolsCommandException : Exception
    {
        internal int Code { get; }
        internal DevToolsCommandException(int code, string message) : base(message) { Code = code; }
        internal bool IsUnavailable => Code == -32601 || Message.StartsWith("Method not available", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Browser-target CDP connection; page targets cannot install extensions.</summary>
    internal sealed class BrowserDevTools : IDisposable
    {
        private readonly ClientWebSocket _socket = new ClientWebSocket();
        private int _id;

        internal async Task ConnectAsync(int port, CancellationToken token)
        {
            using var handler = new HttpClientHandler { UseProxy = false };
            using var http = new HttpClient(handler);
            using var response = await http.GetAsync($"http://127.0.0.1:{port}/json/version", token);
            response.EnsureSuccessStatusCode();
            var json = JObject.Parse(await response.Content.ReadAsStringAsync());
            var address = new Uri(json["webSocketDebuggerUrl"]?.Value<string>() ?? "");
            if (address.Scheme != "ws" || !address.IsLoopback || address.Port != port || !address.AbsolutePath.StartsWith("/devtools/browser/", StringComparison.Ordinal))
                throw new IOException("浏览器返回了无效的本地调试地址");
            await _socket.ConnectAsync(address, token);
        }

        internal async Task<JObject> SendAsync(string method, JObject parameters, CancellationToken token)
        {
            var id = ++_id;
            var bytes = Encoding.UTF8.GetBytes(new JObject { ["id"] = id, ["method"] = method, ["params"] = parameters }.ToString(Formatting.None));
            await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
            var buffer = new byte[16384];
            while (true)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult received;
                do
                {
                    received = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (received.MessageType == WebSocketMessageType.Close) throw new IOException("浏览器调试连接已关闭");
                    message.Write(buffer, 0, received.Count);
                    if (message.Length > 4 * 1024 * 1024) throw new IOException("浏览器扩展响应过大");
                } while (!received.EndOfMessage);
                var reply = JObject.Parse(Encoding.UTF8.GetString(message.ToArray()));
                if (reply["id"]?.Value<int>() != id) continue;
                if (reply["error"] is JObject error)
                    throw new DevToolsCommandException(error["code"]?.Value<int>() ?? 0, error["message"]?.Value<string>() ?? "扩展接口返回错误");
                return reply["result"] as JObject ?? throw new IOException("浏览器没有返回扩展操作结果");
            }
        }

        public void Dispose() => _socket.Dispose();
    }

    internal static class BrowserExtensionService
    {
        internal static bool LegacyLoadingExpected(BrowserRuntime runtime)
        {
            if (BrowserLauncher.SupportsCommandLineExtensions(runtime.Kind)) return true;
            return int.TryParse((runtime.Version ?? "").Split('.')[0], out var major) && major > 0 && major < 137;
        }

        internal static async Task EnsureLoadedAsync(int port, IEnumerable<string> paths, bool legacyLoadingExpected, CancellationToken token)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(25));
            try
            {
                using var connection = new BrowserDevTools();
                await connection.ConnectAsync(port, deadline.Token);
                await EnsureLoadedAsync(paths, legacyLoadingExpected,
                    (method, parameters) => connection.SendAsync(method, parameters, deadline.Token));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (ExtensionLoadingException) { throw; }
            catch (Exception ex) { throw new ExtensionLoadingException("无法完成插件加载验证：" + ex.Message, ex); }
        }

        // The command seam exercises protocol failures and legacy fallback without a real browser.
        internal static async Task EnsureLoadedAsync(IEnumerable<string> paths, bool legacyLoadingExpected,
            Func<string, JObject, Task<JObject>> send)
        {
            var selected = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            JArray? installed = null;
            try { installed = (await send("Extensions.getExtensions", new JObject()))["extensions"] as JArray; }
            catch (DevToolsCommandException ex) when (ex.IsUnavailable) { }
            foreach (var path in selected)
            {
                if (installed?.OfType<JObject>().Any(item => Matches(item, path) && item["enabled"]?.Value<bool>() == true) == true)
                    continue; // The old command-line method (or the existing profile) already loaded it.
                try
                {
                    var result = await send("Extensions.loadUnpacked", new JObject { ["path"] = path });
                    var id = result["id"]?.Value<string>();
                    if (id == null || id.Length != 32 || id.Any(ch => ch < 'a' || ch > 'p'))
                        throw new IOException("加载接口没有返回有效插件 ID");
                    if (installed != null)
                    {
                        JArray? verified = null;
                        var enabled = false;
                        for (var attempt = 0; attempt < 5; attempt++)
                        {
                            verified = (await send("Extensions.getExtensions", new JObject()))["extensions"] as JArray;
                            enabled = verified?.OfType<JObject>().Any(item => item["id"]?.Value<string>() == id
                                && Matches(item, path) && item["enabled"]?.Value<bool>() == true) == true;
                            if (enabled) break;
                            await Task.Delay(100);
                        }
                        if (!enabled)
                        {
                            DiagnosticLog.Info("extension.verify", "id=" + id + "; observed=" + verified?.ToString(Formatting.None));
                            throw new IOException("浏览器未确认插件已启用");
                        }
                    }
                    DiagnosticLog.Info("extension.load", $"CDP loaded extension={id}");
                }
                catch (DevToolsCommandException ex) when (ex.IsUnavailable && installed == null && legacyLoadingExpected)
                {
                    // Older browsers lack this experimental domain; retain their existing CLI support.
                    DiagnosticLog.Info("extension.load", "CDP unavailable; retaining supported legacy command-line loading");
                    return;
                }
                catch (Exception ex)
                {
                    throw new ExtensionLoadingException("插件加载失败（" + Path.GetFileName(path) + "）：" + ex.Message, ex);
                }
            }
        }

        private static bool Matches(JObject item, string path)
        {
            var actual = item["path"]?.Value<string>();
            return !string.IsNullOrWhiteSpace(actual) && string.Equals(Path.GetFullPath(actual), path, StringComparison.OrdinalIgnoreCase);
        }
    }
}
