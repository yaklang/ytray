#nullable enable
using System;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YTray.Core.BrowserUse;

namespace YTray.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class BrowserUseTests
    {
        [TestMethod]
        public void BrowserUseConfigurationSurvivesRestartAndRotatesWhenDisabled()
        {
            var directory = Path.Combine(Path.GetTempPath(), "ytray-browser-access-" + Guid.NewGuid().ToString("N"));
            var access = new YTray.Core.SystemControl.ControlAccessStore(directory, "BrowserUse");
            try
            {
                var original = access.Open(restore: false);
                Assert.AreEqual(original, new YTray.Core.SystemControl.ControlAccessStore(directory, "BrowserUse").Open(restore: true));
                access.Clear();
                Assert.AreNotEqual(original, access.Open(restore: true));
            }
            finally { Directory.Delete(directory, true); }
        }

        [TestMethod]
        public async Task BrowserUseMcpRequiresBearerAndRoutesOnlyToExplicitShare()
        {
            var directory = Path.Combine(Path.GetTempPath(), "ytray-browser-use-tests-" + Guid.NewGuid().ToString("N"));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                var enrollment = new BrowserUseEnrollment(directory);
                var instance = Guid.NewGuid();
                var ticket = enrollment.Issue(instance);
                Assert.IsTrue(new BrowserUseEnrollment(directory).Accepts(instance, ticket));
                Assert.IsFalse(enrollment.Accepts(Guid.NewGuid(), ticket));
                using var server = new BrowserUseServer(directory, "", 0, enrollment: enrollment);
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var endpoint = server.Endpoint;
                using var unauthorized = await client.PostAsync(endpoint, Json(new JObject()));
                Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
                var entry = JObject.Parse(server.ConfigurationJSON)["mcpServers"]!["ytray-browser"]!;
                client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", (string)entry["headers"]!["Authorization"]!);
                using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = Json(new JObject()) })
                {
                    request.Headers.TryAddWithoutValidation("Origin", "https://example.test");
                    using var forbidden = await client.SendAsync(request);
                    Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);
                }
                async Task<JObject> Call(string name, JObject arguments)
                {
                    using var response = await client.PostAsync(endpoint, Json(new JObject
                    {
                        ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                        ["params"] = new JObject { ["name"] = name, ["arguments"] = arguments },
                    }));
                    response.EnsureSuccessStatusCode();
                    return (JObject)JObject.Parse(await response.Content.ReadAsStringAsync())["result"]!;
                }
                async Task<JArray> Pages() => (JArray)(await Call("browser_list_pages", new JObject()))["structuredContent"]!["pages"]!;
                Assert.AreEqual(0, (await Pages()).Count);
                var share = new JObject
                {
                    ["type"] = "share", ["version"] = 1, ["instance_id"] = instance.ToString("D"),
                    ["instance_label"] = "A", ["url"] = "http://example.test/", ["title"] = "Fixture",
                };
                NamedPipeClientStream NewPipe() => new NamedPipeClientStream(".", BrowserUseNativeHost.PipeName(server.BoundPort),
                    PipeDirection.InOut, PipeOptions.Asynchronous);
                using (var host = NewPipe())
                {
                    await host.ConnectAsync(5_000, deadline.Token);
                    await BrowserUseWire.WriteAsync(host, share, deadline.Token);
                    var hello = await BrowserUseWire.ReadAsync(host, deadline.Token);
                    Assert.AreEqual("ready", (string?)hello["type"]);
                    var pageId = (string?)(await Pages()).First?["page_id"];
                    StringAssert.Matches(pageId, new System.Text.RegularExpressions.Regex(@"^p[0-9a-f]{8}-1$"));
                    var denied = await Call("browser_observe", new JObject { ["page_id"] = "p999" });
                    Assert.AreEqual(true, (bool?)denied["isError"]);
                    Assert.AreEqual("page_not_shared", (string?)denied["structuredContent"]?["code"]);
                    var pending = Call("browser_observe", new JObject { ["page_id"] = pageId });
                    var request = await BrowserUseWire.ReadAsync(host, deadline.Token);
                    Assert.AreEqual("observe", (string?)request["method"]);
                    await BrowserUseWire.WriteAsync(host, new JObject
                    {
                        ["type"] = "response", ["id"] = request["id"]!.DeepClone(), ["result"] = new JObject { ["title"] = "Fixture" },
                    }, deadline.Token);
                    var observed = await pending;
                    Assert.AreEqual(false, (bool?)observed["isError"]);
                    Assert.AreEqual("Fixture", (string?)observed["structuredContent"]?["title"]);
                    Assert.AreEqual(false, (bool?)(await Pages()).First?["managed"]);
                }
                JArray remaining;
                do { remaining = await Pages(); if (remaining.Count != 0) await Task.Delay(30, deadline.Token); }
                while (remaining.Count != 0);
                using (var host = NewPipe())
                {
                    await host.ConnectAsync(5_000, deadline.Token);
                    share["managed_ticket"] = enrollment.Issue(Guid.NewGuid());
                    await BrowserUseWire.WriteAsync(host, share, deadline.Token);
                    Assert.AreEqual("rejected", (string?)(await BrowserUseWire.ReadAsync(host, deadline.Token))["type"]);
                    Assert.AreEqual(0, (await Pages()).Count);
                }
            }
            finally { Directory.Delete(directory, true); }
        }

        [TestMethod]
        public void BrowserOpenRequiresExactlyOneExplicitTargetAndAnHttpUrl()
        {
            var target = Guid.NewGuid().ToString("D");
            const string url = "https://example.test/";
            (string Method, JObject Arguments) Parse(JObject args) => BrowserUseTools.Parse(new JObject
            {
                ["name"] = "browser_open", ["arguments"] = args,
            });
            Assert.AreEqual("open", Parse(new JObject { ["url"] = url, ["instance_id"] = target }).Method);
            Assert.AreEqual("open", Parse(new JObject { ["url"] = url, ["runtime_id"] = target, ["use_proxy"] = true }).Method);
            foreach (var invalid in new[]
            {
                new JObject { ["url"] = url },
                new JObject { ["url"] = url, ["instance_id"] = target, ["runtime_id"] = target },
                new JObject { ["url"] = url, ["instance_id"] = target, ["use_proxy"] = true },
                new JObject { ["url"] = url, ["instance_id"] = "A" },
                new JObject { ["url"] = "file:///C:/test.txt", ["instance_id"] = target },
                new JObject { ["url"] = "https://user:secret@example.test/", ["instance_id"] = target },
            })
                Assert.AreEqual("invalid_arguments", Assert.ThrowsExactly<BrowserUseException>(() => Parse(invalid)).Code);
        }

        [TestMethod]
        public async Task InstanceOpenClaimsOnePageForTheRequestedOrigin()
        {
            var directory = Path.Combine(Path.GetTempPath(), "ytray-browser-open-" + Guid.NewGuid().ToString("N"));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                var enrollment = new BrowserUseEnrollment(directory);
                var instance = Guid.NewGuid();
                var pipeName = "ytray-browser-open-" + instance.ToString("N");
                using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                using var peer = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                var accepting = pipe.WaitForConnectionAsync(deadline.Token);
                await peer.ConnectAsync(5_000, deadline.Token);
                await accepting;
                var hello = new JObject { ["version"] = 1, ["instance_id"] = instance.ToString("D"), ["managed_ticket"] = enrollment.Issue(Guid.NewGuid()) };
                Assert.ThrowsExactly<InvalidDataException>(() => new BrowserUseInstanceConnection(pipe, hello, enrollment));
                hello["managed_ticket"] = enrollment.Issue(instance);
                using var connection = new BrowserUseInstanceConnection(pipe, hello, enrollment);
                var helloReceived = BrowserUseWire.ReadAsync(peer, deadline.Token);
                await connection.SendReadyAsync(deadline.Token);
                Assert.AreEqual("ready", (string?)(await helloReceived)["type"]);
                var receive = connection.ReceiveAsync(deadline.Token);
                var url = new Uri("https://example.test/search");
                var open = connection.OpenAsync(url, deadline.Token);
                var request = await BrowserUseWire.ReadAsync(peer, deadline.Token);
                var id = (string)request["id"]!;
                Assert.AreEqual(url.AbsoluteUri, (string?)request["params"]?["url"]);
                Assert.AreEqual("instance_busy", (await Assert.ThrowsExactlyAsync<BrowserUseException>(() => connection.OpenAsync(url, deadline.Token))).Code);
                Assert.IsFalse(connection.TryClaimPage(Guid.NewGuid().ToString("N"), "https://example.test"));
                Assert.IsFalse(connection.TryClaimPage(id, "https://other.test"));
                Assert.IsTrue(connection.TryClaimPage(id, "https://example.test"));
                Assert.IsFalse(connection.TryClaimPage(id, "https://example.test"));
                await BrowserUseWire.WriteAsync(peer, new JObject
                {
                    ["type"] = "response", ["id"] = id, ["result"] = new JObject { ["page_id"] = "p123" },
                }, deadline.Token);
                Assert.AreEqual((id, "p123"), await open);
                Assert.IsFalse(connection.TryClaimPage(id, "https://example.test"));
                // A bad reply terminates the reader deterministically. Windows can keep the read
                // pending when the async pipe peer is disposed from another thread.
                await BrowserUseWire.WriteAsync(peer, new JObject { ["type"] = "unexpected" }, deadline.Token);
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => receive);
            }
            finally { Directory.Delete(directory, true); }
        }

        private static StringContent Json(JObject value) => new StringContent(value.ToString(Formatting.None), Encoding.UTF8, "application/json");
    }
}
