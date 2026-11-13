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
using YTray.Native;

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
            CollectionAssert.AreEquivalent(new[] { "list_apps", "list_windows", "get_window_state", "get_desktop_state", "system_status", "launch_app", "open_folder", "activate_window", "click", "type_text", "press_key", "scroll" }, tools.Select(t => (string)t["name"]!).ToArray());
            foreach (var tool in tools) {
                var properties = (JObject)tool["inputSchema"]!["properties"]!;
                foreach (var name in new[] { "session_id", "runtime_generation", "observation_id", "request_id", "deadline", "target_ref" }) Assert.IsNull(properties[name]);
            }
            foreach (var name in new[] { "click", "type_text", "press_key", "scroll" })
                Assert.AreEqual("string", (string?)tools.Single(tool => (string?)tool["name"] == name)["inputSchema"]?["properties"]?["state_id"]?["type"]);
            var key = tools.Single(t => (string)t["name"]! == "press_key");
            ControlJson.Validate(new JObject { ["window_id"] = 42, ["key"] = "ctrl+l" }, (JObject)key["inputSchema"]!);
            var folder = tools.Single(t => (string)t["name"]! == "open_folder");
            Assert.IsFalse((bool)folder["annotations"]!["readOnlyHint"]!);
            Assert.ThrowsExactly<ControlException>(() => ControlJson.Validate(new JObject(), (JObject)folder["inputSchema"]!));
            ControlJson.Validate(new JObject { ["path"] = @"C:\Code" }, (JObject)folder["inputSchema"]!);
            var read = (JObject)tools.Single(t => (string)t["name"]! == "get_window_state")["inputSchema"]!;
            ControlJson.Validate(new JObject { ["window_id"] = 42, ["region"] = new JObject { ["x"] = 10, ["y"] = 20, ["width"] = 300, ["height"] = 100 } }, read);
            Assert.ThrowsExactly<ControlException>(() => ControlJson.Validate(new JObject { ["window_id"] = 42, ["region"] = new JObject { ["x"] = 0, ["y"] = 0, ["width"] = 0, ["height"] = 100 } }, read));
        }

        [TestMethod, TestCategory("ControlProtocol")]
        public void OpenFolderPreparesOnlyAnExistingAbsoluteDirectoryForShell()
        {
            var root = Path.Combine(Path.GetTempPath(), "ytray-folder-" + Guid.NewGuid().ToString("N"));
            const string name = "测试 文件夹";
            var directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
            try
            {
                var start = WindowsApplicationControl.FolderStartInfo(Path.Combine(root, ".", name));
                Assert.AreEqual(directory, start.FileName);
                Assert.AreEqual("open", start.Verb);
                Assert.IsTrue(start.UseShellExecute);
                Assert.AreEqual("", start.Arguments);

                foreach (var invalid in new[] { "", "relative", @"C:relative", @"\relative", "https://example.test/", @"\\?\C:\" })
                    Assert.AreEqual("InvalidPath", Assert.ThrowsExactly<ControlException>(() => WindowsApplicationControl.FolderStartInfo(invalid)).Code);

                var file = Path.Combine(root, "file.txt");
                File.WriteAllText(file, "fixture");
                foreach (var unavailable in new[] { file, Path.Combine(root, "missing") })
                    Assert.AreEqual("FolderUnavailable", Assert.ThrowsExactly<ControlException>(() => WindowsApplicationControl.FolderStartInfo(unavailable)).Code);
            }
            finally { Directory.Delete(root, true); }
        }

        [TestMethod, TestCategory("ControlAuthorization")]
        public async Task OpenFolderRequiresEnabledControl()
        {
            using var controller = new SystemControlController(Path.Combine(Path.GetTempPath(), "ytray-folder-auth-" + Guid.NewGuid().ToString("N")));
            var error = await Assert.ThrowsExactlyAsync<ControlException>(() =>
                controller.CallAsync("open_folder", new JObject { ["path"] = Path.GetTempPath() }));
            Assert.AreEqual("ControlDisabled", error.Code);
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
                using var gateway = new ControlGateway(controller); gateway.Start(0);
                var endpoint = "http://127.0.0.1:" + gateway.BoundPort + "/system/mcp";
                using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
                http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", (string)JObject.Parse(configuration!)["mcpServers"]!["ytray-system"]!["headers"]!["Authorization"]!);
                var rpcId = 0;
                async Task<JObject> Call(string name, JObject args) {
                    var body = new JObject { ["jsonrpc"] = "2.0", ["id"] = ++rpcId, ["method"] = "tools/call", ["params"] = new JObject { ["name"] = name, ["arguments"] = args } };
                    using var response = await http.PostAsync(endpoint, new StringContent(body.ToString(), Encoding.UTF8, "application/json"));
                    Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
                    var rpc = JObject.Parse(await response.Content.ReadAsStringAsync()); Assert.IsNull(rpc["error"], rpc.ToString());
                    return (JObject)rpc["result"]!;
                }
                var status = await Call("system_status", new JObject()); AssertOkay(status);
                Assert.AreEqual(YTray.Core.YTrayBuildInfo.BuildId, (string?)status["structuredContent"]?["build_id"]);
                Assert.AreEqual((string?)status["structuredContent"]?["build_id"], (string?)status["structuredContent"]?["worker_build_id"], "The MCP host and input worker must run the same build");
                var launched = await Call("launch_app", new JObject { ["app"] = executable });
                AssertOkay(launched);
                var state = (JObject)launched["structuredContent"]!;
                var window = state.Value<long>("window_id"); Assert.AreNotEqual(0L, window);
                Assert.IsFalse(state.Value<bool>("reused_window"));
                var item = ((JArray)state["windows"]!).Single(w => w.Value<long>("window_id") == window);
                fixture = Process.GetProcessById(item.Value<int>("pid"));
                var file = Path.Combine(Path.GetTempPath(), "ytray-launched-fixture-" + fixture.Id + ".json");
                Assert.AreEqual(window, GetForegroundWindow().ToInt64());
                AssertFresh(launched, window);
                ShowWindow(new IntPtr(window), 6);
                var reused = await Call("launch_app", new JObject { ["app"] = executable });
                AssertOkay(reused); AssertFresh(reused, window);
                Assert.IsTrue(reused["structuredContent"]!.Value<bool>("reused_window"));
                Assert.AreEqual(1, ((JArray)reused["structuredContent"]!["windows"]!).Count);
                Assert.AreEqual(window, GetForegroundWindow().ToInt64());
                var tree = Text(reused);
                var messageRow = System.Text.RegularExpressions.Regex.Match(tree, @"\[(\d+)\] Edit.*Message"); Assert.IsTrue(messageRow.Success, tree);
                var worker = controller.WorkerProcessId;
                var typed = await Call("type_text", new JObject { ["window_id"] = window, ["element_index"] = int.Parse(messageRow.Groups[1].Value), ["text"] = "你好 YTray 🚀" });
                AssertOkay(typed); AssertFresh(typed, window);
                Assert.AreEqual("你好 YTray 🚀", JObject.Parse(File.ReadAllText(file)).Value<string>("text"));
                Assert.AreEqual(worker, controller.WorkerProcessId, "Reading the caret after typing must keep the input worker alive");
                var selected = await Call("press_key", new JObject { ["window_id"] = window, ["key"] = "ctrl+a", ["include_screenshot"] = false });
                AssertOkay(selected);
                Assert.AreEqual("focused", (string?)selected["structuredContent"]!["focus"]?["status"], Text(selected));
                Assert.AreEqual("selected", (string?)selected["structuredContent"]!["focus"]?["selection_state"], Text(selected));
                StringAssert.Contains(Text(selected), "selected: \"你好 YTray 🚀\"");
                Assert.AreEqual(worker, controller.WorkerProcessId, "Reading the selection must keep the input worker alive");
                Assert.AreEqual("你好 YTray 🚀".Length, JObject.Parse(File.ReadAllText(file)).Value<int>("selectionLength"));
                AssertOkay(await Call("type_text", new JObject { ["window_id"] = window, ["text"] = "替换成功" }));
                Assert.AreEqual("替换成功", JObject.Parse(File.ReadAllText(file)).Value<string>("text"));
                var keyed = await Call("press_key", new JObject { ["window_id"] = window, ["key"] = "enter", ["include_screenshot"] = true });
                AssertOkay(keyed); AssertFresh(keyed, window, image: true);
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
                var scrolled = await Call("scroll", new JObject { ["window_id"] = window, ["direction"] = "down", ["amount"] = 3, ["include_screenshot"] = false });
                AssertOkay(scrolled);
                Assert.IsTrue(JObject.Parse(File.ReadAllText(file)).Value<double>("scroll") > 0, "Window-centered scroll must work from a text-only observation");
                Assert.IsFalse(((JArray)scrolled["content"]!).Any(c => (string?)c["type"] == "image"));
                var rows = await Call("get_window_state", new JObject { ["window_id"] = window, ["query"] = "Fixture row", ["include_screenshot"] = false });
                AssertOkay(rows); StringAssert.Contains(Text(rows), "Fixture row");
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
                await controller.CallNativeAsync("end_session", new JObject(), CancellationToken.None);
                var healed = await Call("press_key", new JObject { ["window_id"] = window, ["key"] = "enter" });
                AssertOkay(healed); AssertFresh(healed, window);
                Assert.AreEqual(3, JObject.Parse(File.ReadAllText(file)).Value<int>("submissions"), beforeHeal + "\n" + File.ReadAllText(file) + "\nForeground=" + GetForegroundWindow().ToInt64() + " target=" + window);
                Assert.AreEqual(configuration, controller.ConfigurationJSON);
                using (var driver = Process.GetProcessById(controller.WorkerProcessId!.Value)) { driver.Kill(); driver.WaitForExit(3000); }
                var reconnected = await Call("press_key", new JObject { ["window_id"] = window, ["key"] = "enter" });
                AssertOkay(reconnected); AssertFresh(reconnected, window);
                Assert.AreEqual(4, JObject.Parse(File.ReadAllText(file)).Value<int>("submissions"));
                Assert.AreEqual(configuration, controller.ConfigurationJSON);
                var second = await Call("launch_app", new JObject { ["app"] = executable, ["new_window"] = true });
                AssertOkay(second);
                var secondWindow = second["structuredContent"]!.Value<long>("window_id");
                var secondItem = ((JArray)second["structuredContent"]!["windows"]!).Single(w => w.Value<long>("window_id") == secondWindow);
                secondFixture = Process.GetProcessById(secondItem.Value<int>("pid"));
                Assert.AreNotEqual(window, secondWindow, "Launching a second native instance must wait for its new window rather than return the previous one");
                Assert.AreNotEqual(fixture.Id, secondFixture.Id);
                Assert.AreEqual(secondWindow, GetForegroundWindow().ToInt64());
                var button = System.Text.RegularExpressions.Regex.Match(Text(second), @"\[(\d+)\] Button.*Increment");
                Assert.IsTrue(button.Success, Text(second));
                var secondFile = Path.Combine(Path.GetTempPath(), "ytray-launched-fixture-" + secondFixture.Id + ".json");
                WindowsNativeInput.Press(new IntPtr(secondWindow), "f2", Array.Empty<string>(), true);
                for (var attempt = 0; attempt < 20 && JObject.Parse(File.ReadAllText(secondFile)).Value<string>("buttonLabel") != "Renamed"; attempt++)
                    await Task.Delay(25);
                Assert.AreEqual("Renamed", JObject.Parse(File.ReadAllText(secondFile)).Value<string>("buttonLabel"));
                var stale = await Call("click", new JObject { ["window_id"] = secondWindow, ["element_index"] = int.Parse(button.Groups[1].Value), ["include_screenshot"] = false });
                Assert.IsTrue(stale.Value<bool>("isError"), Text(stale));
                Assert.AreEqual("ElementChanged", (string?)stale["structuredContent"]?["error"]?["code"]);
                Assert.AreEqual("NotStarted", (string?)stale["structuredContent"]?["error"]?["execution_state"]);
                Assert.AreEqual(0, JObject.Parse(File.ReadAllText(secondFile)).Value<int>("count"));
                StringAssert.Contains(Text(stale), "Renamed");
            } finally {
                controller.Stop();
                if (secondFixture != null) { if (!secondFixture.HasExited) { secondFixture.Kill(); secondFixture.WaitForExit(3000); } secondFixture.Dispose(); }
                if (fixture != null) { if (!fixture.HasExited) { fixture.Kill(); fixture.WaitForExit(3000); } fixture.Dispose(); }
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            }
        }
        private static void AssertOkay(JObject result) => Assert.IsFalse(result["isError"]?.Value<bool>() == true, result.ToString());
        private static string Text(JObject result) => string.Join("\n", ((JArray)result["content"]!).Where(c => (string?)c["type"] == "text").Select(c => (string?)c["text"]));
        private static void AssertFresh(JObject result, long window, bool image = false)
        {
            var state = (JObject)result["structuredContent"]!;
            Assert.AreEqual(window, state.Value<long>("window_id")); Assert.IsTrue(Text(result).Length > 0); Assert.IsNotNull(state["windows"]);
            Assert.IsTrue(Guid.TryParseExact((string?)state["state_id"], "N", out _));
            StringAssert.Contains(Text(result), "state_id: " + state["state_id"]);
            Assert.AreEqual((string?)((JArray)state["windows"]!).Single(w => w.Value<long>("window_id") == window)["title"], (string?)state["title"]);
            StringAssert.Contains(Text(result), "Count: 0", "Native status text must survive compact formatting");
            Assert.AreEqual(image, ((JArray)result["content"]!).Any(c => (string?)c["type"] == "image"));
            foreach (var name in new[] { "session_id", "runtime_generation", "observation_id", "request_id", "deadline" }) Assert.IsNull(state[name]);
        }
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    }
}
