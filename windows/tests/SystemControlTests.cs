#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using YTray.Core.SystemControl;
using YTray.Models;

namespace YTray.Tests
{
    [TestClass]
    public class SystemControlTests
    {
        [TestMethod, TestCategory("ControlProtocol")]
        public void NdjsonPreservesUtf8SplitFramesAndEscapedNewlines()
        {
            var text = "{\"a\":\"中文🚀\\n文字\"}\r\n{\"id\":2}\n";
            using var input = new ChunkStream(Encoding.UTF8.GetBytes(text));
            var frames = CuaDriverConnection.Frames(input).ToArray();
            Assert.AreEqual("中文🚀\n文字", JObject.Parse(frames[0]).Value<string>("a")); Assert.AreEqual(2, JObject.Parse(frames[1]).Value<int>("id"));
            Assert.ThrowsExactly<InvalidDataException>(() => CuaDriverConnection.Frames(new MemoryStream(Encoding.UTF8.GetBytes("{}"))).ToArray());
            Assert.ThrowsExactly<DecoderFallbackException>(() => CuaDriverConnection.Frames(new MemoryStream(new byte[] { 0xff, 10 })).ToArray());
        }
        [TestMethod, TestCategory("ControlAuthorization")]
        public void ReviewedSchemaDoesNotPublishStartupPathsScriptsOrImplicitForeground()
        {
            var actual = JArray.Parse(File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixtures", "cua-driver-0.38.0-tools.json")));
            var read = CuaDriverSchemaAdapter.Review(new JArray(actual.Select(t => new JObject { ["name"] = t["name"], ["inputSchema"] = t["input_schema"] })), new ControlSessionOptions { ReadOnly = true, AllowDesktop = false, AllowForeground = false });
            Assert.IsFalse(read.ContainsKey("click")); Assert.IsFalse(read.ContainsKey("get_desktop_state"));
            Assert.IsFalse(read.Values.Any(t => t.ToString().Contains("screenshot_out_file")));
            var writable = CuaDriverSchemaAdapter.Review(new JArray(actual.Select(t => new JObject { ["name"] = t["name"], ["inputSchema"] = t["input_schema"] })), new ControlSessionOptions { ReadOnly = false });
            Assert.IsTrue(writable.ContainsKey("click"));
            Assert.ThrowsExactly<ControlException>(() => ControlJson.Validate(new JObject { ["prompt"] = true }, (JObject)read["check_permissions"]["inputSchema"]!));
            foreach (var origin in new[] { "null", "*", "https://example.com/path", "https://user@example.com", "http://example.com" }) Assert.ThrowsExactly<ControlException>(() => ControlJson.Origin(origin));
            Assert.AreEqual("https://example.com", ControlJson.Origin("https://EXAMPLE.com:443/"));
        }
        [TestMethod, TestCategory("ControlProtocol")]
        public void TextOnlyClientsReceiveObservationReferencesAndPublicNextCallArguments()
        {
            var content = new JArray(new JObject { ["type"] = "text", ["text"] = "driver output" }, new JObject { ["type"] = "image", ["data"] = "unchanged" });
            var result = new JObject { ["content"] = content };
            var structured = new JObject { ["observation_id"] = "observation", ["target_ref"] = "window", ["hint"] = "Use run_steps next", ["next_call"] = new JObject { ["tool"] = "get_window_state", ["arguments"] = new JObject { ["pid"] = 7, ["session"] = "private", ["since"] = "latest" } } };
            SystemControlController.DescribePublicContext(result, structured, "window");
            var context = JObject.Parse((string)content.Last!["text"]!);
            Assert.AreEqual("observation", (string?)context["observation_id"]);
            Assert.AreEqual("window", (string?)context["next_call"]!["arguments"]!["target_ref"]);
            Assert.AreEqual(1, ((JObject)context["next_call"]!["arguments"]!).Count);
            Assert.AreEqual("unchanged", (string?)content[1]["data"]); Assert.IsFalse(((string)structured["hint"]!).Contains("run_steps"));
        }
        [TestMethod, TestCategory("ControlProtocol")]
        public async Task HttpParserRejectsDuplicateLengthSmugglingAndOversize()
        {
            foreach (var headers in new[] { "Content-Length: 0\r\nContent-Length: 0", "Content-Length: 0\r\nTransfer-Encoding: chunked", "Content-Length: 1048577", "Content-Length: +0", "Content-Length: 0\r\nX-Forwarded-Host: localhost" }) {
                using var stream = new MemoryStream(Encoding.ASCII.GetBytes("POST /system/mcp HTTP/1.1\r\nHost: localhost:51373\r\n" + headers + "\r\n\r\n"));
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ControlGateway.ReadAsync(stream, CancellationToken.None));
            }
            using var good = new ChunkStream(Encoding.UTF8.GetBytes("OPTIONS /system/mcp HTTP/1.1\r\nHost: 127.0.0.1:51373\r\n\r\n"));
            Assert.AreEqual("OPTIONS", (await ControlGateway.ReadAsync(good, CancellationToken.None)).Method);
        }
        [TestMethod, TestCategory("ControlAuthorization")]
        public async Task StoppedGatewayRejectsCredentialsAndOriginsWithoutStartingRuntime()
        {
            var root = Path.Combine(Path.GetTempPath(), "ytray-control-" + Guid.NewGuid().ToString("N"));
            using var controller = new SystemControlController(root); using var gateway = new ControlGateway(controller);
            var request = new ControlGateway.HttpRequest("POST", "/system/mcp", new Dictionary<string, string> { ["host"] = "127.0.0.1:51373", ["content-type"] = "application/json" }, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}");
            Assert.AreEqual(401, (await gateway.HandleRequestAsync(request)).Status);
            request.Headers["origin"] = "https://evil.example"; Assert.AreEqual(403, (await gateway.HandleRequestAsync(request)).Status);
            Assert.IsFalse(controller.IsActive); Assert.IsNull(controller.ConfigurationJSON);
        }
        [TestMethod, TestCategory("ControlDesktop")]
        public async Task RealDriverControlsOnlyFixtureAndRevokesObservationsAndBearer()
        {
            if (Environment.GetEnvironmentVariable("YTRAY_CONTROL_INTERACTIVE") != "1") Assert.Inconclusive("Set YTRAY_CONTROL_INTERACTIVE=1 for isolated fixture desktop input");
            var executable = Environment.GetEnvironmentVariable("YTRAY_CONTROL_FIXTURE_EXE") ?? "";
            Assert.IsTrue(File.Exists(executable), "Build the independent ControlFixture first");
            var root = Path.Combine(Path.GetTempPath(), "ytray-control-desktop-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var stateFile = Path.Combine(root, "fixture.json");
            using var fixture = Process.Start(new ProcessStartInfo(executable, "\"" + stateFile + "\"") { UseShellExecute = false, CreateNoWindow = true });
            Process? blocker = null;
            using var controller = new SystemControlController(root);
            try {
                await Wait(() => File.Exists(stateFile));
                var blockerFile = Path.Combine(root, "blocker.json");
                blocker = Process.Start(new ProcessStartInfo(executable, "\"" + blockerFile + "\"") { UseShellExecute = false, CreateNoWindow = true });
                await Wait(() => File.Exists(blockerFile));
                await controller.InstallAsync(); Assert.IsTrue(controller.IsInstalled, controller.LastError);
                await controller.StartAsync(new ControlSessionOptions { Executables = new[] { executable }, AllowDesktop = false, AllowForeground = false });
                Assert.IsTrue(controller.IsRunning, controller.LastError);
                var config = JObject.Parse(controller.ConfigurationJSON!); var bearer = (string)config["mcpServers"]!["ytray-system"]!["headers"]!["Authorization"]!;
                Assert.IsTrue(controller.Authorized(bearer, null)); Assert.IsFalse(controller.Authorized(bearer, "https://evil.example"));
                using var gateway = new ControlGateway(controller); gateway.Start();
                using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
                var unauthorized = await http.PostAsync(controller.Endpoint, new StringContent("{}", Encoding.UTF8, "application/json"));
                Assert.AreEqual(System.Net.HttpStatusCode.Unauthorized, unauthorized.StatusCode);
                http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", bearer);
                var rpcId = 0;
                async Task<JObject> Call(string name, JObject arguments) {
                    var rpc = new JObject { ["jsonrpc"] = "2.0", ["id"] = ++rpcId, ["method"] = "tools/call", ["params"] = new JObject { ["name"] = name, ["arguments"] = arguments } };
                    using var response = await http.PostAsync(controller.Endpoint, new StringContent(rpc.ToString(), Encoding.UTF8, "application/json"));
                    Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
                    var result = JObject.Parse(await response.Content.ReadAsStringAsync()); Assert.IsNull(result["error"], result.ToString()); return (JObject)result["result"]!;
                }
                var windows = (JArray)(await Call("list_windows", new JObject()))["structuredContent"]!["windows"]!;
                Assert.IsTrue(windows.All(w => string.Equals((string?)w["executable"], executable, StringComparison.OrdinalIgnoreCase)));
                var target = windows.Single(w => (int)w["pid"]! == fixture!.Id); var reference = (string)target["target_ref"]!;
                async Task<JObject> Observe(int dimension = 640) => await Call("get_window_state", new JObject { ["target_ref"] = reference, ["max_image_dimension"] = dimension });
                var observation = await Observe();
                var structured = (JObject)observation["structuredContent"]!;
                // Protocol evidence contains only the source-controlled fixture's tree, no screenshots or credentials.
                var evidence = Environment.GetEnvironmentVariable("YTRAY_CONTROL_TEST_OUTPUT");
                if (!string.IsNullOrEmpty(evidence)) { Directory.CreateDirectory(evidence); File.WriteAllText(Path.Combine(evidence, "fixture-observation.json"), structured.ToString()); }
                Assert.IsFalse(observation["isError"]?.Value<bool>() == true, observation.ToString());
                Assert.IsTrue(structured.Value<bool>("capture_blocked_by_occlusion"), "A covered window must return its UIA tree without an unproven screenshot");
                Assert.IsFalse(((JArray)observation["content"]!).OfType<JObject>().Any(c => (string?)c["type"] == "image"));
                Assert.AreEqual(0, structured.Value<int>("image_width"));
                var elements = (JArray)structured["elements"]!;
                var button = elements.OfType<JObject>().First(e => e.ToString().Contains("Increment"));
                var requestId = Guid.NewGuid().ToString();
                var action = new JObject { ["target_ref"] = reference, ["observation_id"] = structured["observation_id"], ["element_token"] = button["element_token"], ["request_id"] = requestId,
                    ["deadline"] = DateTimeOffset.UtcNow.AddSeconds(25).ToString("O"), ["delivery_mode"] = "background" };
                var foreground = GetForegroundWindow();
                fixture!.Refresh(); Assert.AreNotEqual(fixture.MainWindowHandle, foreground, "The background test must run with another window in front");
                var clicked = await Call("click", action);
                Assert.IsFalse(clicked["isError"]?.Value<bool>() == true, clicked.ToString());
                await Wait(() => JObject.Parse(File.ReadAllText(stateFile)).Value<int>("count") == 1);
                Assert.AreEqual(foreground, GetForegroundWindow(), "Background Invoke stole focus");
                var duplicate = await Call("click", (JObject)action.DeepClone());
                Assert.IsFalse(duplicate["isError"]?.Value<bool>() == true); Assert.AreEqual(1, JObject.Parse(File.ReadAllText(stateFile)).Value<int>("count"));
                action["request_id"] = Guid.NewGuid().ToString();
                Assert.IsTrue((bool)(await Call("click", action))["isError"]!);
                observation = await Observe(); structured = (JObject)observation["structuredContent"]!;
                var text = ((JArray)structured["elements"]!).OfType<JObject>().First(e => (string?)e["role"] == "edit" || (string?)e["control_type"] == "Edit" || e.ToString().Contains("Message"));
                var typed = await Call("type_text", new JObject { ["target_ref"] = reference, ["observation_id"] = structured["observation_id"], ["element_token"] = text["element_token"],
                    ["text"] = "你好 Windows 🚀", ["request_id"] = Guid.NewGuid().ToString(), ["deadline"] = DateTimeOffset.UtcNow.AddSeconds(25).ToString("O") });
                Assert.IsFalse(typed["isError"]?.Value<bool>() == true, typed.ToString());
                await Wait(() => JObject.Parse(File.ReadAllText(stateFile)).Value<string>("text") == "你好 Windows 🚀");
                observation = await Observe(); structured = (JObject)observation["structuredContent"]!;
                var scroll = ((JArray)structured["elements"]!).OfType<JObject>().First(e => (string?)e["label"] == "FixtureScroll");
                var scrolled = await Call("scroll", new JObject { ["target_ref"] = reference, ["observation_id"] = structured["observation_id"], ["element_token"] = scroll["element_token"],
                    ["direction"] = "down", ["by"] = "line", ["amount"] = 3, ["request_id"] = Guid.NewGuid().ToString(), ["deadline"] = DateTimeOffset.UtcNow.AddSeconds(25).ToString("O") });
                Assert.IsFalse(scrolled["isError"]?.Value<bool>() == true, scrolled.ToString());
                await Wait(() => JObject.Parse(File.ReadAllText(stateFile)).Value<double>("scroll") > 0);
                Assert.AreEqual(foreground, GetForegroundWindow(), "Covered background input stole focus");
                Assert.AreEqual(0, JObject.Parse(File.ReadAllText(blockerFile)).Value<int>("count"), "The same-title application window received an unintended click");
                if (blocker != null && !blocker.HasExited) { blocker.Kill(); blocker.WaitForExit(3000); }
                await controller.StartAsync(new ControlSessionOptions());
                Assert.IsTrue(controller.IsRunning, controller.LastError);
                bearer = (string)JObject.Parse(controller.ConfigurationJSON!)["mcpServers"]!["ytray-system"]!["headers"]!["Authorization"]!;
                http.DefaultRequestHeaders.Remove("Authorization"); http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", bearer);
                windows = (JArray)(await Call("list_windows", new JObject()))["structuredContent"]!["windows"]!; reference = (string)windows.Single(w => (int)w["pid"]! == fixture!.Id)["target_ref"]!;
                observation = await Observe(); structured = (JObject)observation["structuredContent"]!;
                text = ((JArray)structured["elements"]!).OfType<JObject>().First(e => (string?)e["label"] == "Message");
                var focused = await Call("click", new JObject { ["target_ref"] = reference, ["observation_id"] = structured["observation_id"], ["element_token"] = text["element_token"],
                    ["delivery_mode"] = "foreground", ["request_id"] = Guid.NewGuid().ToString(), ["deadline"] = DateTimeOffset.UtcNow.AddSeconds(25).ToString("O") });
                Assert.IsFalse(focused["isError"]?.Value<bool>() == true, focused.ToString());
                observation = await Observe(320); structured = (JObject)observation["structuredContent"]!;
                Assert.IsFalse(observation["isError"]?.Value<bool>() == true, observation.ToString());
                var occluder = YTray.Native.WindowsControlTarget.Resolve(fixture!.MainWindowHandle).FindOccluder(IntPtr.Zero);
                GetWindowThreadProcessId(occluder, out var occludingPid);
                var occludingClass = new StringBuilder(256); GetClassName(occluder, occludingClass, occludingClass.Capacity);
                Assert.AreEqual(320, (int)structured["image_width"]!, "Occluder pid=" + occludingPid + " class=" + occludingClass + " window=" + occluder.ToInt64() + " foreground=" + GetForegroundWindow().ToInt64());
                var picture = ((JArray)observation["content"]!).OfType<JObject>().First(c => (string?)c["type"] == "image");
                using (var png = new MemoryStream(Convert.FromBase64String((string)picture["data"]!))) using (var bitmap = new System.Drawing.Bitmap(png)) {
                    var color = bitmap.GetPixel(16, bitmap.Height - 15);
                    Assert.IsTrue(Math.Abs(color.R - 240) < 5 && Math.Abs(color.G - 248) < 5 && Math.Abs(color.B - 255) < 5, "Capture did not contain the authorized fixture: " + color);
                }
                var pixelButton = ((JArray)structured["elements"]!).OfType<JObject>().First(e => (string?)e["label"] == "Increment");
                var frame = (JObject)pixelButton["screenshot_frame"]!;
                var pixel = await Call("click", new JObject { ["target_ref"] = reference, ["observation_id"] = structured["observation_id"], ["capture_id"] = structured["capture_id"],
                    ["x"] = (double)frame["x"]! + (double)frame["w"]! / 2, ["y"] = (double)frame["y"]! + (double)frame["h"]! / 2,
                    ["request_id"] = Guid.NewGuid().ToString(), ["deadline"] = DateTimeOffset.UtcNow.AddSeconds(25).ToString("O") });
                Assert.IsFalse(pixel["isError"]?.Value<bool>() == true, pixel.ToString());
                await Wait(() => JObject.Parse(File.ReadAllText(stateFile)).Value<int>("count") == 2);
                observation = await Observe(); structured = (JObject)observation["structuredContent"]!;
                text = ((JArray)structured["elements"]!).OfType<JObject>().First(e => (string?)e["label"] == "Message");
                var keyed = await Call("press_key", new JObject { ["target_ref"] = reference, ["observation_id"] = structured["observation_id"], ["element_token"] = text["element_token"],
                    ["delivery_mode"] = "foreground", ["key"] = "Return", ["request_id"] = Guid.NewGuid().ToString(), ["deadline"] = DateTimeOffset.UtcNow.AddSeconds(25).ToString("O") });
                Assert.IsFalse(keyed["isError"]?.Value<bool>() == true, keyed.ToString());
                await Wait(() => JObject.Parse(File.ReadAllText(stateFile)).Value<int>("submissions") == 1);
                controller.Stop(); Assert.IsFalse(controller.Authorized(bearer, null)); Assert.IsNull(controller.ConfigurationJSON);
                await controller.StartAsync(new ControlSessionOptions { Executables = new[] { executable }, ReadOnly = true, AllowDesktop = false, AllowForeground = false });
                Assert.IsTrue(controller.IsRunning, controller.LastError); Assert.IsFalse(controller.Authorized(bearer, null)); Assert.IsFalse(controller.Tools().Any(t => (string?)t["name"] == "click"));
            } finally { controller.Stop(); if (fixture != null && !fixture.HasExited) { fixture.Kill(); fixture.WaitForExit(3000); } if (blocker != null) { if (!blocker.HasExited) { blocker.Kill(); blocker.WaitForExit(3000); } blocker.Dispose(); } }
        }
        private static async Task Wait(Func<bool> condition) { var clock = Stopwatch.StartNew(); while (clock.Elapsed < TimeSpan.FromSeconds(15)) { try { if (condition()) return; } catch (IOException) { } await Task.Delay(100); } Assert.Fail("Fixture state did not reach expected value"); }
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int length);
        private sealed class ChunkStream : MemoryStream { internal ChunkStream(byte[] bytes) : base(bytes) { } public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(1, count)); public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => base.ReadAsync(buffer, offset, Math.Min(1, count), cancellationToken); }
    }
}
