using System;
using System.Collections.Generic;
using System.IO;
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

        public struct PageState
        {
            public string Title;
            public string URL;
        }

        #pragma warning disable 0649
        private class CdpTarget
        {
            public string type;
            public string title;
            public string url;
            [JsonProperty("webSocketDebuggerUrl")]
            public string WebSocketDebuggerUrl;
        }
        #pragma warning restore 0649

        public static async Task<string> CurrentPageTitleAsync(int debugPort, int attempts = 1)
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
                    var visible = await VisiblePageStateAsync(targets);
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
            var target = await VisiblePageAsync(targets) ?? targets.Find(t => t.type == "page" && t.WebSocketDebuggerUrl != null);
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
            await CaptureCoreAsync(debugPort, path, "png", null, 20);
            return path;
        }

        public static async Task<string> CaptureThumbnailAsync(int debugPort, Guid instanceId, string outputURL)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputURL));
            await CaptureCoreAsync(debugPort, outputURL, "jpeg", 68, 12);
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
                    var target = await VisiblePageAsync(targets) ?? targets.Find(t => t.type == "page" && t.WebSocketDebuggerUrl != null);
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
                        File.WriteAllBytes(outputPath, Convert.FromBase64String(encoded));
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

        private static async Task<List<CdpTarget>> PageTargetsAsync(int debugPort)
        {
            if (debugPort < 1 || debugPort > 65535) return null;
            try
            {
                var resp = await Http.GetAsync($"http://127.0.0.1:{debugPort}/json/list");
                if (!resp.IsSuccessStatusCode) return null;
                var json = await resp.Content.ReadAsStringAsync();
                return JsonConvert.DeserializeObject<List<CdpTarget>>(json);
            }
            catch { return null; }
        }

        private static async Task<PageState?> VisiblePageStateAsync(List<CdpTarget> targets)
        {
            foreach (var t in targets)
            {
                if (t.type != "page" || t.WebSocketDebuggerUrl == null) continue;
                try
                {
                    var uri = new Uri(t.WebSocketDebuggerUrl);
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
                    var resp = await WebSocketExchangeAsync(uri, JObject.FromObject(cmd));
                    foreach (var msg in resp)
                    {
                        if (msg["id"]?.Value<int?>() != 1) continue;
                        var value = msg["result"]?["result"]?["value"]?.ToString();
                        // A background tab reports an empty value. Keep checking the remaining
                        // targets instead of abandoning the whole lookup at the first hidden tab.
                        if (string.IsNullOrEmpty(value)) continue;
                        var obj = JObject.Parse(value);
                        return new PageState
                        {
                            Title = (obj["title"]?.ToString() ?? "").Trim(),
                            URL = (obj["url"]?.ToString() ?? "").Trim(),
                        };
                    }
                }
                catch { }
            }
            return null;
        }

        private static async Task<CdpTarget> VisiblePageAsync(List<CdpTarget> targets)
        {
            foreach (var t in targets)
            {
                if (t.type != "page" || t.WebSocketDebuggerUrl == null) continue;
                var state = await VisiblePageStateAsync(new List<CdpTarget> { t });
                if (state != null) return t;
            }
            return null;
        }

        private static async Task<List<JObject>> WebSocketExchangeAsync(Uri wsUrl, JObject command)
        {
            using (var ws = new ClientWebSocket())
            {
                await ws.ConnectAsync(wsUrl, CancellationToken.None);
                var cmdBytes = Encoding.UTF8.GetBytes(command.ToString(Formatting.None));
                await ws.SendAsync(new ArraySegment<byte>(cmdBytes), WebSocketMessageType.Text, true, CancellationToken.None);

                var results = new List<JObject>();
                var buffer = new byte[64 * 1024];
                for (int i = 0; i < 20; i++)
                {
                    var recv = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (recv.MessageType == WebSocketMessageType.Close) break;
                    var text = Encoding.UTF8.GetString(buffer, 0, recv.Count);
                    try
                    {
                        var msg = JObject.Parse(text);
                        results.Add(msg);
                        if (msg["id"]?.Value<int?>() == command["id"]?.Value<int?>()) break;
                    }
                    catch { }
                }
                return results;
            }
        }
    }
}
