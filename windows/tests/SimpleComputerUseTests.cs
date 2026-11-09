#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using YTray.Core.SystemControl;
using YTray.Models;

namespace YTray.Tests
{
    [TestClass]
    public class SimpleComputerUseTests
    {
        [TestMethod, TestCategory("ControlProtocol")]
        public void DiscoveryHasCompactActionsAndNoSessionWorkflow()
        {
            using var controller = new SystemControlController(Path.Combine(Path.GetTempPath(), "ytray-simple-schema-" + Guid.NewGuid().ToString("N")));
            var tools = controller.Tools().OfType<JObject>().ToArray();
            CollectionAssert.AreEquivalent(new[] { "list_apps", "list_windows", "get_window_state", "get_desktop_state", "system_status", "launch_app", "activate_window", "click", "type_text", "press_key", "scroll" }, tools.Select(t => (string)t["name"]!).ToArray());
            foreach (var tool in tools) {
                var properties = (JObject)tool["inputSchema"]!["properties"]!;
                foreach (var name in new[] { "session_id", "runtime_generation", "observation_id", "request_id", "deadline", "target_ref" }) Assert.IsNull(properties[name]);
            }
            var key = tools.Single(t => (string)t["name"]! == "press_key");
            ControlJson.Validate(new JObject { ["window_id"] = 42, ["key"] = "ctrl+l" }, (JObject)key["inputSchema"]!);
            var read = (JObject)tools.Single(t => (string)t["name"]! == "get_window_state")["inputSchema"]!;
            ControlJson.Validate(new JObject { ["window_id"] = 42, ["region"] = new JObject { ["x"] = 10, ["y"] = 20, ["width"] = 300, ["height"] = 100 } }, read);
            Assert.ThrowsExactly<ControlException>(() => ControlJson.Validate(new JObject { ["window_id"] = 42, ["region"] = new JObject { ["x"] = 0, ["y"] = 0, ["width"] = 0, ["height"] = 100 } }, read));
        }

        [TestMethod, TestCategory("ControlProtocol")]
        public void NativeActionFailuresKeepTheirConcreteCauseWithinABoundedMessage()
        {
            var result = new JObject { ["structuredContent"] = new JObject { ["code"] = "tool_invocation_failed" },
                ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = "Cannot establish foreground for target window" }) };
            ((JArray)result["content"]!).Add(new JObject { ["type"] = "text", ["text"] = "{\"session_id\":\"private\",\"runtime_generation\":\"private\"}" });
            Assert.AreEqual("Cannot establish foreground for target window", SystemControlController.ActionErrorMessage(result));
            result["structuredContent"]!["error"] = new JObject { ["message"] = new string('x', 1200) };
            Assert.AreEqual(801, SystemControlController.ActionErrorMessage(result).Length);
        }

        [TestMethod, TestCategory("ControlDesktop")]
        public async Task LaunchActivateInputAndRecoveryReturnFreshStateWithoutClientSessions()
        {
            if (Environment.GetEnvironmentVariable("YTRAY_CONTROL_INTERACTIVE") != "1") Assert.Inconclusive("Run the independent desktop fixture with -Interactive");
            var executable = Environment.GetEnvironmentVariable("YTRAY_CONTROL_FIXTURE_EXE") ?? "";
            var root = Path.Combine(Path.GetTempPath(), "ytray-simple-desktop-" + Guid.NewGuid().ToString("N"));
            Process? fixture = null; Process? secondFixture = null;
            using var controller = new SystemControlController(root);
            try {
                await controller.EnableAsync(); Assert.IsTrue(controller.IsRunning, controller.LastError);
                var configuration = controller.ConfigurationJSON;
                using var gateway = new ControlGateway(controller); gateway.Start();
                using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
                http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", (string)JObject.Parse(configuration!)["mcpServers"]!["ytray-system"]!["headers"]!["Authorization"]!);
                var rpcId = 0;
                async Task<JObject> Call(string name, JObject args) {
                    var body = new JObject { ["jsonrpc"] = "2.0", ["id"] = ++rpcId, ["method"] = "tools/call", ["params"] = new JObject { ["name"] = name, ["arguments"] = args } };
                    using var response = await http.PostAsync(controller.Endpoint, new StringContent(body.ToString(), Encoding.UTF8, "application/json"));
                    Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
                    var rpc = JObject.Parse(await response.Content.ReadAsStringAsync()); Assert.IsNull(rpc["error"], rpc.ToString());
                    return (JObject)rpc["result"]!;
                }
                var launched = await Call("launch_app", new JObject { ["app"] = executable });
                AssertOkay(launched);
                var state = (JObject)launched["structuredContent"]!;
                var window = state.Value<long>("window_id"); Assert.AreNotEqual(0L, window);
                var item = ((JArray)state["windows"]!).Single(w => w.Value<long>("window_id") == window);
                fixture = Process.GetProcessById(item.Value<int>("pid"));
                var file = Path.Combine(Path.GetTempPath(), "ytray-launched-fixture-" + fixture.Id + ".json");
                Assert.AreEqual(window, GetForegroundWindow().ToInt64());
                var tree = Text(launched);
                var messageRow = System.Text.RegularExpressions.Regex.Match(tree, @"\[(\d+)\] Edit.*Message"); Assert.IsTrue(messageRow.Success, tree);
                var typed = await Call("type_text", new JObject { ["window_id"] = window, ["element_index"] = int.Parse(messageRow.Groups[1].Value), ["text"] = "你好 YTray 🚀" });
                AssertOkay(typed); AssertFresh(typed, window);
                Assert.AreEqual("你好 YTray 🚀", JObject.Parse(File.ReadAllText(file)).Value<string>("text"));
                var selected = await Call("press_key", new JObject { ["window_id"] = window, ["key"] = "ctrl+a", ["include_screenshot"] = false });
                AssertOkay(selected);
                Assert.AreEqual("focused", (string?)selected["structuredContent"]!["focus"]?["status"], Text(selected));
                Assert.AreEqual("selected", (string?)selected["structuredContent"]!["focus"]?["selection_state"], Text(selected));
                StringAssert.Contains(Text(selected), "你好 YTray 🚀");
                Assert.AreEqual("你好 YTray 🚀".Length, JObject.Parse(File.ReadAllText(file)).Value<int>("selectionLength"));
                AssertOkay(await Call("type_text", new JObject { ["window_id"] = window, ["text"] = "替换成功" }));
                Assert.AreEqual("替换成功", JObject.Parse(File.ReadAllText(file)).Value<string>("text"));
                var keyed = await Call("press_key", new JObject { ["window_id"] = window, ["key"] = "enter" });
                AssertOkay(keyed); AssertFresh(keyed, window);
                Assert.AreEqual(1, JObject.Parse(File.ReadAllText(file)).Value<int>("submissions"));
                Assert.AreEqual(window, GetForegroundWindow().ToInt64());
                JObject Region(string boundsName) {
                    var physical = (JObject)JObject.Parse(File.ReadAllText(file))[boundsName]!;
                    var windowBounds = keyed["structuredContent"]!["window_bounds"]!;
                    var scaleX = keyed["structuredContent"]!.Value<double>("image_width") / windowBounds.Value<double>("width");
                    var scaleY = keyed["structuredContent"]!.Value<double>("image_height") / windowBounds.Value<double>("height");
                    return new JObject { ["x"] = (physical.Value<double>("x") - windowBounds.Value<double>("x")) * scaleX,
                        ["y"] = (physical.Value<double>("y") - windowBounds.Value<double>("y")) * scaleY,
                        ["width"] = physical.Value<double>("width") * scaleX, ["height"] = physical.Value<double>("height") * scaleY };
                }
                var scopedField = await Call("get_window_state", new JObject { ["window_id"] = window, ["region"] = Region("messageBounds") });
                AssertOkay(scopedField); Assert.IsFalse(((JArray)scopedField["content"]!).Any(c => (string?)c["type"] == "image"), "Region reads should not encode a screenshot by default");
                Assert.IsFalse(Text(scopedField).Contains("Increment"), Text(scopedField));
                var field = System.Text.RegularExpressions.Regex.Match(Text(scopedField), @"\[(\d+)\] Edit.*Message"); Assert.IsTrue(field.Success, Text(scopedField));
                var filled = await Call("type_text", new JObject { ["window_id"] = window, ["element_index"] = int.Parse(field.Groups[1].Value),
                    ["text"] = "组合替换 🚀", ["replace"] = true, ["submit"] = true, ["include_screenshot"] = false });
                AssertOkay(filled); AssertFresh(filled, window, image: false);
                Assert.AreEqual("组合替换 🚀", JObject.Parse(File.ReadAllText(file)).Value<string>("text"));
                Assert.AreEqual(2, JObject.Parse(File.ReadAllText(file)).Value<int>("submissions"));
                Assert.AreEqual("YTray Control Fixture · Submitted 2", (string?)filled["structuredContent"]!["title"], "A changed caption must not retain the driver's previous title");
                Assert.IsFalse(((JArray)filled["content"]!).Any(c => (string?)c["type"] == "image"));
                Assert.IsNull(filled["structuredContent"]!["tree_markdown"], "The default context must not duplicate its tree");
                var scopedStatus = await Call("get_window_state", new JObject { ["window_id"] = window, ["region"] = Region("statusBounds") });
                AssertOkay(scopedStatus); StringAssert.Contains(Text(scopedStatus), "Count: 0");
                Assert.IsFalse(Text(scopedStatus).Contains("组合替换"), "Out-of-region focus/selection must not bring unrelated text into the response");
                var full = await Call("get_window_state", new JObject { ["window_id"] = window, ["detail"] = "full", ["include_screenshot"] = false });
                AssertOkay(full); StringAssert.Contains((string)full["structuredContent"]!["tree_markdown"]!, "Message");
                ShowWindow(new IntPtr(window), 6);
                var activated = await Call("activate_window", new JObject { ["window_id"] = window });
                AssertOkay(activated); AssertFresh(activated, window); Assert.AreEqual(window, GetForegroundWindow().ToInt64());
                // End only the underlying driver context, just as a long-idle native
                // session does. The public API must repair it without a setup call.
                var beforeHeal = File.ReadAllText(file);
                await controller.DriverCall("end_session", new JObject(), CancellationToken.None);
                var healed = await Call("press_key", new JObject { ["window_id"] = window, ["key"] = "enter" });
                AssertOkay(healed); AssertFresh(healed, window);
                Assert.AreEqual(3, JObject.Parse(File.ReadAllText(file)).Value<int>("submissions"), beforeHeal + "\n" + File.ReadAllText(file) + "\nForeground=" + GetForegroundWindow().ToInt64() + " target=" + window);
                Assert.AreEqual(configuration, controller.ConfigurationJSON);
                using (var driver = Process.GetProcessById(controller.DriverProcessId!.Value)) { driver.Kill(); driver.WaitForExit(3000); }
                var reconnected = await Call("press_key", new JObject { ["window_id"] = window, ["key"] = "enter" });
                AssertOkay(reconnected); AssertFresh(reconnected, window);
                Assert.AreEqual(4, JObject.Parse(File.ReadAllText(file)).Value<int>("submissions"));
                Assert.AreEqual(configuration, controller.ConfigurationJSON);
                var second = await Call("launch_app", new JObject { ["app"] = executable });
                AssertOkay(second);
                var secondWindow = second["structuredContent"]!.Value<long>("window_id");
                var secondItem = ((JArray)second["structuredContent"]!["windows"]!).Single(w => w.Value<long>("window_id") == secondWindow);
                secondFixture = Process.GetProcessById(secondItem.Value<int>("pid"));
                Assert.AreNotEqual(window, secondWindow, "Launching a second native instance must wait for its new window rather than return the previous one");
                Assert.AreNotEqual(fixture.Id, secondFixture.Id);
                Assert.AreEqual(secondWindow, GetForegroundWindow().ToInt64());
            } finally {
                controller.Stop();
                if (secondFixture != null) { if (!secondFixture.HasExited) { secondFixture.Kill(); secondFixture.WaitForExit(3000); } secondFixture.Dispose(); }
                if (fixture != null) { if (!fixture.HasExited) { fixture.Kill(); fixture.WaitForExit(3000); } fixture.Dispose(); }
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            }
        }
        private static void AssertOkay(JObject result) => Assert.IsFalse(result["isError"]?.Value<bool>() == true, result.ToString());
        private static string Text(JObject result) => string.Join("\n", ((JArray)result["content"]!).Where(c => (string?)c["type"] == "text").Select(c => (string?)c["text"]));
        private static void AssertFresh(JObject result, long window, bool image = true)
        {
            var state = (JObject)result["structuredContent"]!;
            Assert.AreEqual(window, state.Value<long>("window_id")); Assert.IsTrue(Text(result).Length > 0); Assert.IsNotNull(state["windows"]);
            Assert.AreEqual((string?)((JArray)state["windows"]!).Single(w => w.Value<long>("window_id") == window)["title"], (string?)state["title"]);
            StringAssert.Contains(Text(result), "Count: 0", "Native status text must survive compact formatting");
            if (image) Assert.IsTrue(((JArray)result["content"]!).Any(c => (string?)c["type"] == "image"));
            foreach (var name in new[] { "session_id", "runtime_generation", "observation_id", "request_id", "deadline" }) Assert.IsNull(state[name]);
        }
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    }
}
