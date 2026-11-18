#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace YTray.Core
{
    internal static class BrowserEngineControl
    {
        internal static async Task<JObject> CallAsync(int port, string extensionId, Guid instanceId, BrowserEngineTarget? target)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var token = timeout.Token;
            using var browser = new BrowserDevTools();
            await browser.ConnectAsync(port, token);
            var action = target == null ? "bridge.managed-connection.get" : "bridge.managed-connection.set";
            var message = new JObject { ["action"] = action, ["payload"] = new JObject { ["instanceId"] = instanceId.ToString("D") } };
            if (target != null) message["payload"]!["launchTarget"] = target.ToWire();
            var url = "chrome-extension://" + extensionId + "/ytray-bootstrap.html?connectionOnly=1";
            var created = await browser.SendAsync("Target.createTarget", new JObject
            {
                ["url"] = "about:blank", ["hidden"] = true, ["background"] = true,
            }, token);
            var targetId = (string?)created["targetId"] ?? throw new IOException("浏览器未返回插件控制页。");
            try
            {
                var attached = await browser.SendAsync("Target.attachToTarget", new JObject
                {
                    ["targetId"] = targetId, ["flatten"] = true,
                }, token);
                var session = (string?)attached["sessionId"] ?? throw new IOException("无法连接插件控制页。");
                var nav = await browser.SendAsync("Page.navigate", new JObject { ["url"] = url }, token, session);
                if ((string?)nav["errorText"] is string error) throw new IOException(error);
                var encodedUrl = JsonConvert.SerializeObject(url);
                var expression = "location.href === " + encodedUrl + " && !!globalThis.chrome?.runtime?.id";
                while (true)
                {
                    var ready = await browser.SendAsync("Runtime.evaluate", new JObject { ["expression"] = expression, ["returnByValue"] = true }, token, session);
                    if ((bool?)ready["result"]?["value"] == true) break;
                    await Task.Delay(40, token);
                }
                var call = await browser.SendAsync("Runtime.evaluate", new JObject
                {
                    ["expression"] = "chrome.runtime.sendMessage(" + message.ToString(Formatting.None) + ")",
                    ["returnByValue"] = true, ["awaitPromise"] = true,
                }, token, session);
                if (call["result"]?["value"] is not JObject response)
                    throw new IOException("插件未返回接入状态，请确认浏览器使用内置插件的当前版本。");
                if ((bool?)response["ok"] != true)
                    throw new IOException((string?)response["error"] ?? "插件无法处理接入请求。");
                return response["data"] as JObject ?? throw new IOException("插件接入状态不完整。");
            }
            finally
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try { await browser.SendAsync("Target.closeTarget", new JObject { ["targetId"] = targetId }, cleanup.Token); }
                catch (Exception error) { DiagnosticLog.Error("browser.engine.control.close", error); }
            }
        }
    }
}
