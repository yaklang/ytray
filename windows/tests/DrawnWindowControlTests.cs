#nullable enable
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YTray.Core.SystemControl;
using YTray.Models;
using YTray.Native;

namespace YTray.Tests
{
    [TestClass]
    public class DrawnWindowControlTests
    {
        [TestMethod, TestCategory("ControlDesktop")]
        public async Task McpCanClickAndTypeIntoDrawnWindowUsingScreenshotPixels()
        {
            if (Environment.GetEnvironmentVariable("YTRAY_CONTROL_INTERACTIVE") != "1") Assert.Inconclusive("Run the independent desktop fixture with -Interactive");
            var executable = Environment.GetEnvironmentVariable("YTRAY_CONTROL_FIXTURE_EXE") ?? "";
            Assert.IsTrue(File.Exists(executable));
            var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ytray-drawn-" + Guid.NewGuid().ToString("N"))).FullName;
            var fixtureState = Path.Combine(root, "fixture.json");
            var blockerState = Path.Combine(root, "blocker.json");
            using var controller = new SystemControlController(root);
            using var fixture = Process.Start(new ProcessStartInfo(executable, "\"" + fixtureState + "\" --drawn-window") { UseShellExecute = false })!;
            Process? blocker = null;
            try {
                await WaitForState(fixtureState, _ => true);
                blocker = Process.Start(new ProcessStartInfo(executable, "\"" + blockerState + "\"") { UseShellExecute = false })!;
                await WaitForState(blockerState, _ => true);
                blocker.Refresh();
                var coveringWindow = blocker.MainWindowHandle;
                await controller.StartAsync(new ControlSessionOptions { Executables = new[] { executable }, AllowDesktop = false, AllowForeground = true });
                Assert.IsTrue(controller.IsRunning, controller.LastError);
                using var gateway = new ControlGateway(controller);
                gateway.Start(0);
                using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
                var configuration = JObject.Parse(controller.ConfigurationJSON!);
                client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", (string)configuration["mcpServers"]!["ytray-system"]!["headers"]!["Authorization"]!);
                var rpcId = 0;
                async Task<JObject> Call(string name, JObject arguments) {
                    var rpc = new JObject { ["jsonrpc"] = "2.0", ["id"] = ++rpcId, ["method"] = "tools/call",
                        ["params"] = new JObject { ["name"] = name, ["arguments"] = arguments } };
                    using var request = new StringContent(rpc.ToString(Formatting.None), Encoding.UTF8, "application/json");
                    using var response = await client.PostAsync("http://127.0.0.1:" + gateway.BoundPort + "/system/mcp", request);
                    response.EnsureSuccessStatusCode();
                    var message = JObject.Parse(await response.Content.ReadAsStringAsync());
                    Assert.IsNull(message["error"], message.ToString());
                    return (JObject)message["result"]!;
                }

                var blockerActive = await Call("activate_window", new JObject { ["window_id"] = coveringWindow.ToInt64() });
                Assert.IsFalse(blockerActive.Value<bool>("isError"), blockerActive.ToString());
                Assert.AreEqual(coveringWindow, GetForegroundWindow());
                var discovered = await Call("list_windows", new JObject { ["app"] = "YTray Drawn Fixture" });
                var window = ((JArray)discovered["structuredContent"]!["windows"]!).Single().Value<long>("window_id");
                var native = await Call("get_window_state", new JObject { ["window_id"] = window, ["include_screenshot"] = false });
                Assert.IsFalse(native.Value<bool>("isError"), native.ToString());
                Assert.AreEqual("window_only", (string?)native["structuredContent"]!["layout_status"], native.ToString());
                Assert.IsFalse(HasImage(native));
                Assert.AreEqual(coveringWindow, GetForegroundWindow());
                StringAssert.Contains((string)native["content"]![0]!["text"]!, "include_screenshot:true");

                var rejected = await Call("click", new JObject { ["window_id"] = window, ["x"] = 100, ["y"] = 100,
                    ["state_id"] = native["structuredContent"]!["state_id"] });
                Assert.AreEqual("ScreenshotRequired", (string?)rejected["structuredContent"]?["error"]?["code"]);

                var observed = await Call("get_window_state", new JObject { ["window_id"] = window, ["max_image_dimension"] = 320 });
                Assert.IsFalse(observed.Value<bool>("isError"), observed.ToString());
                var state = observed["structuredContent"]!;
                Assert.AreEqual("window_capture_pixels", (string?)state["coordinate_space"]);
                Assert.AreEqual("window_render", (string?)state["screenshot_source"]);
                Assert.AreEqual(coveringWindow, GetForegroundWindow(), "Capturing the covered window must leave the foreground alone");
                using var image = ReadImage(observed);
                Assert.AreEqual(320, Math.Max(image.Width, image.Height));
                var point = FindColor(image, Color.FromArgb(44, 109, 174));
                await Task.Delay(TimeSpan.FromSeconds(11));
                var clicked = await Call("click", new JObject { ["window_id"] = window, ["state_id"] = state["state_id"],
                    ["x"] = point.X, ["y"] = point.Y, ["include_screenshot"] = true });
                Assert.IsFalse(clicked.Value<bool>("isError"), (string?)clicked["content"]?[0]?["text"]);
                Assert.AreEqual("matched", (string?)clicked["structuredContent"]?["visual_verification"]);
                Assert.IsTrue(((JArray)clicked["structuredContent"]!["operation"]!["steps"]!).Any(step => (string?)step["step"] == "verify_capture"));
                Assert.AreEqual(window, GetForegroundWindow().ToInt64(), "Coordinate input must activate the target");
                using var updated = ReadImage(clicked);
                _ = FindColor(updated, Color.FromArgb(32, 133, 92));
                Assert.AreEqual(1, (await WaitForState(fixtureState, row => row.Value<int>("count") == 1)).Value<int>("count"));

                const string text = "你好 YTray";
                var typed = await Call("type_text", new JObject { ["window_id"] = window,
                    ["state_id"] = clicked["structuredContent"]!["state_id"], ["text"] = text });
                Assert.IsFalse(typed.Value<bool>("isError"), typed.ToString());
                await WaitForState(fixtureState, row => (string?)row["text"] == text);

                var controls = await Call("get_window_state", new JObject { ["window_id"] = coveringWindow.ToInt64(), ["detail"] = "full" });
                Assert.IsFalse(controls.Value<bool>("isError"), controls.ToString());
                Assert.IsFalse(HasImage(controls));
                Assert.AreEqual(window, GetForegroundWindow().ToInt64(), "Reading available controls needs no foreground access");
                var layout = (string)controls["structuredContent"]!["tree_markdown"]!;
                var button = System.Text.RegularExpressions.Regex.Match(layout, @"\[(\d+)\].*\bIncrement\b");
                Assert.IsTrue(button.Success, layout);
                var nativeClick = await Call("click", new JObject { ["window_id"] = coveringWindow.ToInt64(),
                    ["state_id"] = controls["structuredContent"]!["state_id"], ["element_index"] = int.Parse(button.Groups[1].Value) });
                Assert.IsFalse(nativeClick.Value<bool>("isError"), nativeClick.ToString());
                Assert.IsTrue((nativeClick["structuredContent"]!["operation"]!["steps"] as JArray)!.Any(step => (string?)step["step"] == "invoke_control"));
                Assert.AreEqual(window, GetForegroundWindow().ToInt64(), "A native button invocation must stay in the background");
                await WaitForState(blockerState, row => row.Value<int>("count") == 1);

                var beforeChange = await Call("get_window_state", new JObject { ["window_id"] = window, ["max_image_dimension"] = 320 });
                using var oldFrame = ReadImage(beforeChange);
                var oldPoint = FindColor(oldFrame, Color.FromArgb(32, 133, 92));
                File.WriteAllText(fixtureState + ".change-target", "");
                await WaitForState(fixtureState, row => row.Value<bool>("targetChanged"));
                await Task.Delay(TimeSpan.FromSeconds(11));
                var changed = await Call("click", new JObject { ["window_id"] = window, ["state_id"] = beforeChange["structuredContent"]!["state_id"],
                    ["x"] = oldPoint.X, ["y"] = oldPoint.Y });
                Assert.IsTrue(changed.Value<bool>("isError"));
                var error = changed["structuredContent"]?["error"];
                Assert.AreEqual("ViewChanged", (string?)error?["code"], (string?)changed["content"]?[0]?["text"]);
                Assert.AreEqual("NotStarted", (string?)error?["execution_state"]);
                Assert.AreEqual("verify_capture", (string?)error?["step"]);
                var difference = error!["verification"]!;
                Assert.AreEqual("click_neighborhood", (string?)difference["region"]);
                Assert.IsTrue(difference.Value<int>("changed_pixels") > 0);
                var changedBounds = difference["changed_bounds"]!;
                Assert.IsTrue(new Rectangle(changedBounds.Value<int>("x"), changedBounds.Value<int>("y"),
                    changedBounds.Value<int>("width"), changedBounds.Value<int>("height")).Contains(oldPoint));
                Assert.AreNotEqual((string?)beforeChange["structuredContent"]?["state_id"], (string?)changed["structuredContent"]?["state_id"]);
                using var newFrame = ReadImage(changed);
                Assert.AreEqual(oldFrame.Size, newFrame.Size);
                var newPoint = FindColor(newFrame, Color.FromArgb(163, 59, 52));
                Assert.AreEqual(1, (await WaitForState(fixtureState, _ => true)).Value<int>("count"));

                File.WriteAllText(fixtureState + ".change-decoration", "");
                await WaitForState(fixtureState, row => row.Value<bool>("decorationChanged"));
                var selectedAgain = await Call("click", new JObject { ["window_id"] = window, ["state_id"] = changed["structuredContent"]!["state_id"],
                    ["x"] = newPoint.X, ["y"] = newPoint.Y,
                    ["target_region"] = new JObject { ["x"] = newPoint.X - 14, ["y"] = newPoint.Y - 12, ["width"] = 29, ["height"] = 25 },
                    ["verify_regions"] = new JArray(new JObject { ["x"] = 45, ["y"] = 46, ["width"] = 70, ["height"] = 12 }) });
                Assert.IsFalse(selectedAgain.Value<bool>("isError"), (string?)selectedAgain["content"]?[0]?["text"]);
                Assert.AreEqual("matched", (string?)selectedAgain["structuredContent"]?["visual_verification"]);
                await WaitForState(fixtureState, row => row.Value<int>("count") == 2);

                async Task MinimizeBehindFixtureAsync() {
                    var front = await Call("activate_window", new JObject { ["window_id"] = coveringWindow.ToInt64() });
                    Assert.IsFalse(front.Value<bool>("isError"), front.ToString());
                    const int showMinimizedNoActivate = 7;
                    ShowWindow(new IntPtr(window), showMinimizedNoActivate);
                    Assert.IsTrue(WindowsApplicationControl.IsMinimized(new IntPtr(window)));
                    Assert.AreEqual(coveringWindow, GetForegroundWindow());
                }

                await MinimizeBehindFixtureAsync();
                var textOnly = await Call("get_window_state", new JObject { ["window_id"] = window, ["include_screenshot"] = false });
                Assert.IsFalse(HasImage(textOnly));
                Assert.AreEqual(coveringWindow, GetForegroundWindow());
                Assert.IsTrue(WindowsApplicationControl.IsMinimized(new IntPtr(window)));
                var fallback = await Call("get_window_state", new JObject { ["window_id"] = window, ["max_image_dimension"] = 320 });
                Assert.IsFalse(fallback.Value<bool>("isError"), fallback.ToString());
                Assert.IsTrue(fallback["structuredContent"]!.Value<bool>("foreground_for_capture"), (string?)fallback["content"]?[0]?["text"]);
                Assert.AreEqual("screen", (string?)fallback["structuredContent"]!["screenshot_source"]);
                Assert.AreEqual(window, GetForegroundWindow().ToInt64());
                using var restored = ReadImage(fallback);
                _ = FindColor(restored, Color.FromArgb(163, 59, 52));

                await MinimizeBehindFixtureAsync();
                await controller.StartAsync(new ControlSessionOptions { Executables = new[] { executable }, AllowDesktop = false, AllowForeground = false });
                client.DefaultRequestHeaders.Remove("Authorization");
                configuration = JObject.Parse(controller.ConfigurationJSON!);
                client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", (string)configuration["mcpServers"]!["ytray-system"]!["headers"]!["Authorization"]!);
                var restricted = await Call("get_window_state", new JObject { ["window_id"] = window });
                Assert.IsFalse(HasImage(restricted));
                Assert.IsNotNull(restricted["structuredContent"]?["screenshot_error"]);
                Assert.AreEqual(coveringWindow, GetForegroundWindow());
                Assert.IsTrue(WindowsApplicationControl.IsMinimized(new IntPtr(window)));
            } finally {
                controller.Stop();
                if (!fixture.HasExited) { fixture.Kill(); fixture.WaitForExit(3000); }
                if (blocker != null && !blocker.HasExited) { blocker.Kill(); blocker.WaitForExit(3000); }
                blocker?.Dispose();
                await controller.Trace.FlushAsync();
                Directory.Delete(root, true);
            }
        }

        [TestMethod, TestCategory("ControlDesktop")]
        public async Task McpReturnsAnOwnedPopupAndRefusesItsCoordinatesAfterItCloses()
        {
            if (Environment.GetEnvironmentVariable("YTRAY_CONTROL_INTERACTIVE") != "1") Assert.Inconclusive("Run the independent desktop fixture with -Interactive");
            var executable = Environment.GetEnvironmentVariable("YTRAY_CONTROL_FIXTURE_EXE") ?? "";
            Assert.IsTrue(File.Exists(executable));
            var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ytray-popup-" + Guid.NewGuid().ToString("N"))).FullName;
            var fixtureState = Path.Combine(root, "fixture.json");
            using var controller = new SystemControlController(root);
            using var fixture = Process.Start(new ProcessStartInfo(executable, "\"" + fixtureState + "\" --drawn-popup") { UseShellExecute = false })!;
            try {
                await WaitForState(fixtureState, _ => true);
                await controller.StartAsync(new ControlSessionOptions { Executables = new[] { executable }, AllowDesktop = false, AllowForeground = true });
                Assert.IsTrue(controller.IsRunning, controller.LastError);
                using var gateway = new ControlGateway(controller);
                gateway.Start(0);
                using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
                var configuration = JObject.Parse(controller.ConfigurationJSON!);
                client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", (string)configuration["mcpServers"]!["ytray-system"]!["headers"]!["Authorization"]!);
                var id = 0;
                async Task<JObject> Call(string name, JObject arguments) {
                    var rpc = new JObject { ["jsonrpc"] = "2.0", ["id"] = ++id, ["method"] = "tools/call",
                        ["params"] = new JObject { ["name"] = name, ["arguments"] = arguments } };
                    using var request = new StringContent(rpc.ToString(Formatting.None), Encoding.UTF8, "application/json");
                    using var response = await client.PostAsync("http://127.0.0.1:" + gateway.BoundPort + "/system/mcp", request);
                    response.EnsureSuccessStatusCode();
                    var message = JObject.Parse(await response.Content.ReadAsStringAsync());
                    Assert.IsNull(message["error"], message.ToString());
                    return (JObject)message["result"]!;
                }

                var listed = await Call("list_windows", new JObject { ["app"] = "YTray Drawn Fixture" });
                var owner = listed["structuredContent"]!["windows"]!.Single().Value<long>("window_id");
                var activated = await Call("activate_window", new JObject { ["window_id"] = owner, ["include_screenshot"] = false });
                Assert.IsFalse(activated.Value<bool>("isError"), (string?)activated["content"]?[0]?["text"]);
                Assert.IsFalse(HasImage(activated));
                var nativePopup = activated["structuredContent"]!["related_windows"]!.Single();
                Assert.AreEqual("window_only", (string?)nativePopup["layout_status"]);
                Assert.IsNull(nativePopup["image_content_index"]);
                Assert.IsTrue(nativePopup.Value<bool>("no_activate"));
                Assert.AreEqual(owner, GetForegroundWindow().ToInt64());

                var key = await Call("press_key", new JObject { ["window_id"] = nativePopup["window_id"],
                    ["state_id"] = nativePopup["state_id"], ["key"] = "enter", ["include_screenshot"] = false });
                var rejected = key["structuredContent"]!;
                Assert.AreEqual(0, (await WaitForState(fixtureState, _ => true)).Value<int>("popupActivations"),
                    "A keyboard action must leave a WS_EX_NOACTIVATE popup visible and keep the owner's focus.");
                Assert.IsTrue(key.Value<bool>("isError"));
                Assert.AreEqual("KeyboardTargetUnavailable", (string?)rejected["error"]?["code"], (string?)key["content"]?[0]?["text"]);
                Assert.AreEqual("NotStarted", (string?)rejected["error"]?["execution_state"]);
                Assert.AreEqual(owner, rejected.Value<long>("window_id"));
                Assert.AreEqual(nativePopup.Value<long>("window_id"), rejected["related_windows"]!.Single().Value<long>("window_id"));
                Assert.IsFalse(rejected["operation"]!["steps"]!.Any(step => (string?)step["step"] == "activate"));
                Assert.AreEqual(owner, GetForegroundWindow().ToInt64());

                var observed = await Call("activate_window", new JObject { ["window_id"] = owner });
                var popup = observed["structuredContent"]!["related_windows"]!.Single();
                Assert.AreEqual(owner, popup.Value<long>("owner_window_id"));
                Assert.AreEqual("window_render", (string?)popup["screenshot_source"]);
                var popupId = popup.Value<long>("window_id");
                using var image = ReadImage(observed, popup.Value<int>("image_content_index"));
                Assert.AreEqual(popup.Value<int>("image_width"), image.Width);
                Assert.AreEqual(popup.Value<int>("image_height"), image.Height);
                var point = FindColor(image, Color.FromArgb(44, 109, 174));
                var selection = new JObject { ["window_id"] = popupId, ["state_id"] = popup["state_id"], ["x"] = point.X, ["y"] = point.Y };
                var clicked = await Call("click", selection);
                Assert.IsFalse(clicked.Value<bool>("isError"), (string?)clicked["content"]?[0]?["text"]);
                Assert.AreEqual("matched", (string?)clicked["structuredContent"]?["visual_verification"]);
                Assert.IsFalse(clicked["structuredContent"]!["operation"]!["steps"]!.Any(step => (string?)step["step"] == "activate"));
                Assert.AreEqual(1, (await WaitForState(fixtureState, row => row.Value<int>("popupCount") == 1)).Value<int>("popupCount"));

                var stale = await Call("click", selection);
                var state = stale["structuredContent"]!;
                Assert.IsTrue(stale.Value<bool>("isError"));
                Assert.AreEqual("PopupChanged", (string?)state["error"]?["code"]);
                Assert.AreEqual("NotStarted", (string?)state["error"]?["execution_state"]);
                Assert.AreEqual("verify_capture", (string?)state["error"]?["step"]);
                Assert.AreEqual(owner, state.Value<long>("window_id"));
                Assert.AreEqual(popupId, state.Value<long>("closed_window_id"));
                Assert.IsNotNull(state["state_id"]);
                Assert.IsFalse(state["related_windows"]!.Any());
                Assert.AreEqual(0, (await WaitForState(fixtureState, _ => true)).Value<int>("count"));
            } finally {
                controller.Stop();
                if (!fixture.HasExited) { fixture.Kill(); fixture.WaitForExit(3000); }
                await controller.Trace.FlushAsync();
                Directory.Delete(root, true);
            }
        }

        [TestMethod, TestCategory("ControlDesktop")]
        public async Task McpRechecksAPaintedCaretButRefusesChangedText()
        {
            if (Environment.GetEnvironmentVariable("YTRAY_CONTROL_INTERACTIVE") != "1") Assert.Inconclusive("Run the independent desktop fixture with -Interactive");
            var executable = Environment.GetEnvironmentVariable("YTRAY_CONTROL_FIXTURE_EXE") ?? "";
            Assert.IsTrue(File.Exists(executable));
            var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ytray-caret-" + Guid.NewGuid().ToString("N"))).FullName;
            var stateFile = Path.Combine(root, "fixture.json");
            using var controller = new SystemControlController(root);
            using var fixture = Process.Start(new ProcessStartInfo(executable, "\"" + stateFile + "\" --drawn-window") { UseShellExecute = false })!;
            try {
                await WaitForState(stateFile, _ => true);
                await controller.StartAsync(new ControlSessionOptions { Executables = new[] { executable }, AllowDesktop = false, AllowForeground = true });
                Assert.IsTrue(controller.IsRunning, controller.LastError);
                using var gateway = new ControlGateway(controller);
                gateway.Start(0);
                using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
                var configuration = JObject.Parse(controller.ConfigurationJSON!);
                client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", (string)configuration["mcpServers"]!["ytray-system"]!["headers"]!["Authorization"]!);
                var id = 0;
                async Task<JObject> Call(string name, JObject arguments) {
                    var rpc = new JObject { ["jsonrpc"] = "2.0", ["id"] = ++id, ["method"] = "tools/call",
                        ["params"] = new JObject { ["name"] = name, ["arguments"] = arguments } };
                    using var request = new StringContent(rpc.ToString(Formatting.None), Encoding.UTF8, "application/json");
                    using var response = await client.PostAsync("http://127.0.0.1:" + gateway.BoundPort + "/system/mcp", request);
                    response.EnsureSuccessStatusCode();
                    var message = JObject.Parse(await response.Content.ReadAsStringAsync());
                    Assert.IsNull(message["error"], message.ToString());
                    return (JObject)message["result"]!;
                }
                fixture.Refresh();
                var window = fixture.MainWindowHandle.ToInt64();
                var initial = await Call("activate_window", new JObject { ["window_id"] = window });
                using var image = ReadImage(initial);
                var point = FindColor(image, Color.FromArgb(44, 109, 174));
                Task<JObject> Click(JObject observed) => Call("click", new JObject { ["window_id"] = window,
                    ["state_id"] = observed["structuredContent"]!["state_id"], ["x"] = point.X, ["y"] = point.Y, ["include_screenshot"] = true });
                var focused = await Click(initial);
                Assert.IsFalse(focused.Value<bool>("isError"), (string?)focused["content"]?[0]?["text"]);
                var draft = await Call("type_text", new JObject { ["window_id"] = window,
                    ["state_id"] = focused["structuredContent"]!["state_id"], ["text"] = "Draft", ["include_screenshot"] = true });
                await WaitForState(stateFile, row => (string?)row["text"] == "Draft");

                File.WriteAllText(stateFile + ".blink-caret", "");
                await WaitForState(stateFile, row => row.Value<bool>("caretVisible"));
                var settled = await Click(draft);
                Assert.IsFalse(settled.Value<bool>("isError"), (string?)settled["content"]?[0]?["text"]);
                Assert.AreEqual("painted", (string?)settled["structuredContent"]?["visual_verification_caret"]);
                await WaitForState(stateFile, row => row.Value<int>("count") == 2);

                File.WriteAllText(stateFile + ".change-text", "");
                await WaitForState(stateFile, row => (string?)row["text"] == "Draftl");
                var changed = await Click(settled);
                Assert.IsTrue(changed.Value<bool>("isError"));
                Assert.AreEqual("ViewChanged", (string?)changed["structuredContent"]?["error"]?["code"]);
                Assert.AreEqual("NotStarted", (string?)changed["structuredContent"]?["error"]?["execution_state"]);
                Assert.AreEqual("client", (string?)changed["structuredContent"]?["error"]?["verification"]?["region"]);
                Assert.AreEqual(2, (await WaitForState(stateFile, _ => true)).Value<int>("count"));
            } finally {
                controller.Stop();
                if (!fixture.HasExited) { fixture.Kill(); fixture.WaitForExit(3000); }
                await controller.Trace.FlushAsync();
                Directory.Delete(root, true);
            }
        }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);

        private static bool HasImage(JObject result) => ((JArray)result["content"]!).Any(block => (string?)block["type"] == "image");

        private static Bitmap ReadImage(JObject result, int? index = null)
        {
            Assert.IsTrue(HasImage(result), ((JArray)result["content"]!).First?.ToString());
            var content = (JArray)result["content"]!;
            var image = index.HasValue ? content[index.Value] : content.Single(block => (string?)block["type"] == "image");
            Assert.AreEqual("image", (string?)image["type"]);
            using var data = new MemoryStream(Convert.FromBase64String((string)image["data"]!));
            using var bitmap = new Bitmap(data);
            return new Bitmap(bitmap);
        }

        private static Point FindColor(Bitmap image, Color color)
        {
            var bounds = Rectangle.Empty;
            for (var y = 0; y < image.Height; y += 2)
                for (var x = 0; x < image.Width; x += 2)
                    if (image.GetPixel(x, y).ToArgb() == color.ToArgb()) {
                        var sample = new Rectangle(x, y, 1, 1);
                        bounds = bounds.IsEmpty ? sample : Rectangle.Union(bounds, sample);
                    }
            Assert.IsTrue(bounds.Width > 20 && bounds.Height > 20, "MCP screenshot must show the painted target");
            return new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        }

        private static async Task<JObject> WaitForState(string path, Func<JObject, bool> condition)
        {
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 5000) {
                try {
                    using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    var state = JObject.Parse(reader.ReadToEnd());
                    if (condition(state)) return state;
                } catch (IOException) { }
                catch (JsonException) { }
                await Task.Delay(35);
            }
            Assert.Fail("The drawn fixture did not report the expected state");
            return new JObject();
        }
    }
}
