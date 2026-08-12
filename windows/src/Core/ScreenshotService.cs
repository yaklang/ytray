#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YTray.Models;

namespace YTray.Core
{
    /// <summary>
    /// CDP screenshot / navigation / page-state via HttpClient (/json/list) + ClientWebSocket.
    /// Mirrors macOS ScreenshotService.
    /// </summary>
    public static class ScreenshotService
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        private static readonly ConcurrentDictionary<int, string> LastVisibleTargetByPort =
            new ConcurrentDictionary<int, string>();
        private static readonly TimeSpan VisibilityProbeTimeout = TimeSpan.FromMilliseconds(850);

        public struct PageState
        {
            public string Title;
            public string URL;
        }

        #pragma warning disable 0649
        private class CdpTarget
        {
            public string? id;
            public string? type;
            public string? title;
            public string? url;
            [JsonProperty("webSocketDebuggerUrl")]
            public string? WebSocketDebuggerUrl;
        }
        #pragma warning restore 0649

        public static async Task<string?> CurrentPageTitleAsync(int debugPort, int attempts = 1)
        {
            var s = await CurrentPageStateAsync(debugPort, attempts);
            return s?.Title;
        }

        public static async Task<PageState?> CurrentPageStateAsync(int debugPort, int attempts = 1)
        {
            for (int a = 0; a < Math.Max(attempts, 1); a++)
            {
                var targets = await PageTargetsAsync(debugPort);
                if (targets != null)
                {
                    var visible = await VisiblePageStateAsync(debugPort, targets);
                    if (visible != null) return visible;
                    var first = targets.Find(t => t.type == "page");
                    if (first != null)
                    {
                        var title = (first.title ?? "").Trim();
                        var url = (first.url ?? "").Trim();
                        if (!string.IsNullOrEmpty(title) || !string.IsNullOrEmpty(url))
                            return new PageState { Title = title, URL = url };
                    }
                }
                if (a + 1 < attempts) await Task.Delay(150);
            }
            return null;
        }

        public static async Task<bool> WaitUntilReadyAsync(int debugPort, int attempts = 60)
        {
            for (int a = 0; a < Math.Max(attempts, 1); a++)
            {
                if (await PageTargetsAsync(debugPort) != null) return true;
                if (a + 1 < attempts) await Task.Delay(250);
            }
            return false;
        }

        public static async Task NavigateAsync(int debugPort, string url)
        {
            if (!Uri.IsWellFormedUriString(url, UriKind.Absolute)) throw new YTrayException(YTrayError.InvalidURL, url);
            var targets = await PageTargetsAsync(debugPort) ?? throw new YTrayException(YTrayError.LaunchFailed, "找不到可恢复的浏览器标签页");
            var target = await VisiblePageAsync(debugPort, targets) ?? targets.Find(t => t.type == "page" && t.WebSocketDebuggerUrl != null);
            if (target?.WebSocketDebuggerUrl == null) throw new YTrayException(YTrayError.LaunchFailed, "找不到可恢复的浏览器标签页");
            var uri = new Uri(target.WebSocketDebuggerUrl);
            var cmd = new { id = 1, method = "Page.navigate", @params = new { url } };
            var resp = await WebSocketExchangeAsync(uri, JObject.FromObject(cmd));
            foreach (var msg in resp)
            {
                if (msg["id"]?.Value<int?>() == 1)
                {
                    if (msg["error"] is JObject err) throw new YTrayException(YTrayError.LaunchFailed, err["message"]?.ToString() ?? "恢复页面失败");
                    return;
                }
            }
            throw new YTrayException(YTrayError.LaunchFailed, "浏览器没有确认恢复页面");
        }

        public static async Task<string> CaptureAsync(int debugPort, Guid instanceId, string outputDirectory)
        {
            Directory.CreateDirectory(outputDirectory);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var path = Path.Combine(outputDirectory, $"{stamp}-{instanceId.ToString().Substring(0, 8)}.png");
            await CaptureCoreAsync(debugPort, path, "png", null, 4);
            return path;
        }

        public static async Task<string> CaptureThumbnailAsync(int debugPort, Guid instanceId, string outputURL)
        {
            var outputDirectory = Path.GetDirectoryName(outputURL);
            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new ArgumentException("Thumbnail output must include a directory.", nameof(outputURL));
            Directory.CreateDirectory(outputDirectory);
            await CaptureCoreAsync(debugPort, outputURL, "jpeg", 68, 3);
            return outputURL;
        }

        private static async Task CaptureCoreAsync(int debugPort, string outputPath, string format, int? quality, int attempts)
        {
            var lastError = "调试端口尚未就绪";
            for (int i = 0; i < attempts; i++)
            {
                try
                {
                    var targets = await PageTargetsAsync(debugPort) ?? throw new YTrayException(YTrayError.ScreenshotFailed, "没有可截图的页面");
                    var target = await VisiblePageAsync(debugPort, targets) ?? targets.Find(t => t.type == "page" && t.WebSocketDebuggerUrl != null);
                    if (target?.WebSocketDebuggerUrl == null) throw new YTrayException(YTrayError.ScreenshotFailed, "没有可截图的页面");
                    var uri = new Uri(target.WebSocketDebuggerUrl);
                    var p = new Dictionary<string, object> { ["format"] = format, ["captureBeyondViewport"] = false, ["fromSurface"] = true };
                    if (quality.HasValue) p["quality"] = quality.Value;
                    var cmd = new { id = 1, method = "Page.captureScreenshot", @params = p };
                    var resp = await WebSocketExchangeAsync(uri, JObject.FromObject(cmd));
                    foreach (var msg in resp)
                    {
                        if (msg["id"]?.Value<int?>() != 1) continue;
                        if (msg["error"] is JObject err) throw new YTrayException(YTrayError.ScreenshotFailed, err["message"]?.ToString() ?? "CDP 返回错误");
                        var encoded = msg["result"]?["data"]?.ToString();
                        if (string.IsNullOrEmpty(encoded)) throw new YTrayException(YTrayError.ScreenshotFailed, "CDP 没有返回图片");
                        WriteAllBytesAtomically(outputPath, Convert.FromBase64String(encoded));
                        return;
                    }
                    throw new YTrayException(YTrayError.ScreenshotFailed, "等待 CDP 截图响应超时");
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    await Task.Delay(250);
                }
            }
            throw new YTrayException(YTrayError.ScreenshotFailed, lastError);
        }

        private static async Task<List<CdpTarget>?> PageTargetsAsync(int debugPort)
        {
            if (debugPort < 1 || debugPort > 65535) return null;
            try
            {
                using (var resp = await Http.GetAsync($"http://127.0.0.1:{debugPort}/json/list"))
                {
                    if (!resp.IsSuccessStatusCode) return null;
                    var json = await resp.Content.ReadAsStringAsync();
                    return JsonConvert.DeserializeObject<List<CdpTarget>>(json);
                }
            }
            catch { return null; }
        }

        private sealed class VisiblePageResult
        {
            public CdpTarget Target { get; }
            public PageState State { get; }

            public VisiblePageResult(CdpTarget target, PageState state)
            {
                Target = target ?? throw new ArgumentNullException(nameof(target));
                State = state;
            }
        }

        private static async Task<PageState?> VisiblePageStateAsync(int debugPort, List<CdpTarget> targets)
        {
            var result = await FindVisiblePageAsync(debugPort, targets);
            return result?.State;
        }

        private static async Task<CdpTarget?> VisiblePageAsync(int debugPort, List<CdpTarget> targets)
        {
            var result = await FindVisiblePageAsync(debugPort, targets);
            return result?.Target;
        }

        private static async Task<VisiblePageResult?> FindVisiblePageAsync(int debugPort, List<CdpTarget> targets)
        {
            var pages = targets?
                .Where(target => target != null && target.type == "page"
                    && !string.IsNullOrWhiteSpace(target.WebSocketDebuggerUrl))
                .ToList() ?? new List<CdpTarget>();
            if (pages.Count == 0) return null;

            if (LastVisibleTargetByPort.TryGetValue(debugPort, out var preferredId))
            {
                var preferred = pages.FirstOrDefault(target => target.id == preferredId);
                if (preferred != null)
                {
                    var cached = await ProbeVisibilityAsync(preferred, CancellationToken.None);
                    if (cached != null) return cached;
                    pages.Remove(preferred);
                }
            }

            using (var cancellation = new CancellationTokenSource())
            {
                // Probe concurrently so one hidden or stale tab cannot serialize an 850ms timeout
                // ahead of the actual foreground page. Once found, cancel all losing probes.
                var pending = pages.Select(target => ProbeVisibilityAsync(target, cancellation.Token)).ToList();
                while (pending.Count > 0)
                {
                    var completed = await Task.WhenAny(pending);
                    pending.Remove(completed);
                    var result = await completed;
                    if (result == null) continue;
                    var targetId = result.Target.id;
                    if (!string.IsNullOrWhiteSpace(targetId))
                        LastVisibleTargetByPort[debugPort] = targetId!;
                    cancellation.Cancel();
                    return result;
                }
            }
            return null;
        }

        private static async Task<VisiblePageResult?> ProbeVisibilityAsync(CdpTarget target,
            CancellationToken cancellationToken)
        {
            try
            {
                if (!Uri.TryCreate(target.WebSocketDebuggerUrl, UriKind.Absolute, out var uri))
                    return null;
                var cmd = new
                {
                    id = 1,
                    method = "Runtime.evaluate",
                    @params = new
                    {
                        expression = "document.visibilityState === 'visible' ? JSON.stringify({title: document.title, url: location.href}) : ''",
                        returnByValue = true,
                    },
                };
                var resp = await WebSocketExchangeAsync(uri, JObject.FromObject(cmd),
                    VisibilityProbeTimeout, cancellationToken);
                foreach (var msg in resp)
                {
                    if (msg["id"]?.Value<int?>() != 1) continue;
                    var value = msg["result"]?["result"]?["value"]?.ToString();
                    if (value == null || value.Length == 0) continue;
                    var obj = JObject.Parse(value);
                    return new VisiblePageResult(
                        target,
                        new PageState
                        {
                            Title = (obj["title"]?.ToString() ?? "").Trim(),
                            URL = (obj["url"]?.ToString() ?? "").Trim(),
                        });
                }
            }
            catch { }
            return null;
        }

        private static async Task<List<JObject>> WebSocketExchangeAsync(Uri wsUrl, JObject command,
            TimeSpan? operationTimeout = null, CancellationToken cancellationToken = default)
        {
            using (var ws = new ClientWebSocket())
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(operationTimeout ?? TimeSpan.FromSeconds(4));
                await ws.ConnectAsync(wsUrl, timeout.Token);
                var cmdBytes = Encoding.UTF8.GetBytes(command.ToString(Formatting.None));
                await ws.SendAsync(new ArraySegment<byte>(cmdBytes), WebSocketMessageType.Text, true, timeout.Token);

                var results = new List<JObject>();
                var buffer = new byte[16 * 1024];
                for (int messageIndex = 0; messageIndex < 20; messageIndex++)
                {
                    // CDP screenshot responses are routinely hundreds of kilobytes and therefore
                    // arrive as multiple WebSocket fragments. Reassemble the entire message before
                    // parsing it; parsing every fragment independently silently discarded images.
                    using (var message = new MemoryStream())
                    {
                        WebSocketReceiveResult recv;
                        do
                        {
                            recv = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                            if (recv.MessageType == WebSocketMessageType.Close) break;
                            message.Write(buffer, 0, recv.Count);
                            if (message.Length > 64L * 1024 * 1024)
                                throw new YTrayException(YTrayError.ScreenshotFailed, "CDP 返回的图片数据异常过大");
                        }
                        while (!recv.EndOfMessage);

                        if (recv.MessageType == WebSocketMessageType.Close) break;
                        var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
                        try
                        {
                            var msg = JObject.Parse(text);
                            results.Add(msg);
                            if (msg["id"]?.Value<int?>() == command["id"]?.Value<int?>()) break;
                        }
                        catch (JsonException) { }
                    }
                }
                return results;
            }
        }

        private static void WriteAllBytesAtomically(string outputPath, byte[] bytes)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("Screenshot output path is required.", nameof(outputPath));
            if (bytes == null || bytes.Length == 0)
                throw new YTrayException(YTrayError.ScreenshotFailed, "CDP 返回了空图片");
            var directory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("Screenshot output must include a directory.", nameof(outputPath));
            Directory.CreateDirectory(directory);
            var temporary = outputPath + ".new-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllBytes(temporary, bytes);
                if (File.Exists(outputPath)) File.Replace(temporary, outputPath, null);
                else File.Move(temporary, outputPath);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }
    }
}
