#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YTray.Core.SystemControl;

namespace YTray.Tests
{
    [TestClass]
    public class ControlCancellationAndStateTests
    {
        [TestMethod, TestCategory("ControlDesktop")]
        public async Task TwoClientsRejectStaleStateAndCancelOnlyTheMatchingRequest()
        {
            if (Environment.GetEnvironmentVariable("YTRAY_CONTROL_INTERACTIVE") != "1")
                Assert.Inconclusive("Set YTRAY_CONTROL_INTERACTIVE=1 for isolated fixture desktop input");
            var executable = Environment.GetEnvironmentVariable("YTRAY_CONTROL_FIXTURE_EXE") ?? "";
            Assert.IsTrue(File.Exists(executable), "Build the independent ControlFixture first");
            var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ytray-state-" + Guid.NewGuid().ToString("N"))).FullName;
            var file = Path.Combine(root, "fixture.json");
            using var controller = new SystemControlController(root);
            using var fixture = Process.Start(new ProcessStartInfo(executable, "\"" + file + "\"") { UseShellExecute = false })!;
            try {
                await FixtureStateAsync(file);
                fixture.Refresh();
                var window = fixture.MainWindowHandle.ToInt64();
                Assert.AreNotEqual(0L, window);
                await controller.EnableAsync();
                Assert.IsTrue(controller.IsRunning, controller.LastError);
                var worker = controller.WorkerProcessId;
                var bearer = (string)JObject.Parse(controller.ConfigurationJSON!)["mcpServers"]!["ytray-system"]!["headers"]!["Authorization"]!;
                using var gateway = new ControlGateway(controller);
                gateway.Start(0);
                var host = "127.0.0.1:" + gateway.BoundPort;
                var endpoint = "http://" + host + "/system/mcp";
                using var first = CreateClient(bearer);
                using var second = CreateClient(bearer);
                var firstSession = await InitializeAsync(first, endpoint);
                var secondSession = await InitializeAsync(second, endpoint);
                Assert.AreNotEqual(firstSession, secondSession);

                var rpcId = 1;
                async Task<JObject> Call(HttpClient client, string tool, JObject args)
                {
                    using var response = await PostAsync(client, endpoint, Request(++rpcId, tool, args));
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                    return Result(await response.Content.ReadAsStringAsync());
                }
                Task<ControlGateway.HttpResponse> InProcess(string session, JObject message, CancellationToken deadline = default)
                {
                    var headers = new Dictionary<string, string> { ["host"] = host, ["authorization"] = bearer,
                        ["content-type"] = "application/json", ["mcp-session-id"] = session };
                    return gateway.HandleRequestAsync(new ControlGateway.HttpRequest("POST", "/system/mcp", headers, message.ToString(Formatting.None)), deadline);
                }
                JObject Window(bool screenshot = false) => new JObject { ["window_id"] = window, ["include_screenshot"] = screenshot };
                JObject Click(JObject state) => new JObject { ["window_id"] = window, ["element_index"] = Index(state, "Button", "Increment"),
                    ["state_id"] = StateId(state), ["include_screenshot"] = false };

                var original = await Call(first, "activate_window", Window());
                Okay(original);
                var secondRead = await Call(second, "get_window_state", new JObject { ["window_id"] = window, ["include_screenshot"] = false, ["query"] = "Message" });
                Okay(secondRead);
                Assert.AreNotEqual(StateId(original), StateId(secondRead));
                var staleIndex = await Call(first, "click", Click(original));
                Stale(staleIndex, original);
                Assert.AreEqual(0, (await FixtureStateAsync(file)).Value<int>("count"));
                Okay(await Call(first, "click", Click(staleIndex)));
                Assert.AreEqual(1, (await FixtureStateAsync(file)).Value<int>("count"));

                var imageArgs = Window(screenshot: true);
                imageArgs["max_image_dimension"] = 240;
                var small = await Call(first, "get_window_state", imageArgs);
                Okay(small);
                imageArgs["max_image_dimension"] = 512;
                var large = await Call(second, "get_window_state", imageArgs);
                Okay(large);
                Assert.AreNotEqual(small["structuredContent"]!.Value<int>("image_width"), large["structuredContent"]!.Value<int>("image_width"));
                var beforeMove = await FixtureStateAsync(file);
                var olderPoint = Coordinates(small, beforeMove, window);
                var staleImage = await Call(first, "click", olderPoint);
                Stale(staleImage, small);
                Assert.AreEqual(1, (await FixtureStateAsync(file)).Value<int>("count"));

                var pointBeforeMove = Coordinates(staleImage, beforeMove, window);
                var bounds = staleImage["structuredContent"]!["window_bounds"]!;
                Assert.IsTrue(SetWindowPos(new IntPtr(window), IntPtr.Zero, bounds.Value<int>("x") + 32, bounds.Value<int>("y") + 24, 0, 0, 0x15));
                var moved = await Call(first, "click", pointBeforeMove);
                Stale(moved, staleImage);
                Assert.AreEqual(1, (await FixtureStateAsync(file)).Value<int>("count"));
                var afterMove = await FixtureStateAsync(file, state => state["incrementBounds"]!.Value<double>("x")
                    != beforeMove["incrementBounds"]!.Value<double>("x"));
                var clicked = await Call(first, "click", Coordinates(moved, afterMove, window));
                Okay(clicked);
                Assert.AreEqual(2, (await FixtureStateAsync(file)).Value<int>("count"));

                var gate = (SemaphoreSlim)typeof(SystemControlController).GetField("_interaction", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(controller)!;
                Task<ControlGateway.HttpResponse> remaining;
                await gate.WaitAsync();
                try {
                    var action = Request(200, "click", Click(clicked));
                    var cancelled = InProcess(firstSession, action);
                    remaining = InProcess(secondSession, action);
                    Assert.IsFalse(cancelled.IsCompleted);
                    Assert.IsFalse(remaining.IsCompleted);
                    using (var wrong = await PostAsync(first, endpoint, Cancel("200")))
                        Assert.AreEqual(HttpStatusCode.Accepted, wrong.StatusCode);
                    Assert.IsFalse(cancelled.IsCompleted, "A string RPC ID must not match a numeric ID");
                    using (var stop = await PostAsync(first, endpoint, Cancel(200)))
                        Assert.AreEqual(HttpStatusCode.Accepted, stop.StatusCode);
                    Assert.AreSame(cancelled, await Task.WhenAny(cancelled, Task.Delay(2000)), "Queued cancellation must finish while the action gate remains held");
                    Assert.AreEqual(202, (await cancelled).Status);
                    Assert.IsFalse(remaining.IsCompleted, "The same RPC ID in the second MCP session must stay queued");
                    using var deadline = new CancellationTokenSource();
                    var expired = InProcess(firstSession, Request(201, "click", Click(clicked)), deadline.Token);
                    Assert.IsFalse(expired.IsCompleted);
                    deadline.Cancel();
                    Assert.AreSame(expired, await Task.WhenAny(expired, Task.Delay(2000)));
                    var error = Result((await expired).Body)["structuredContent"]!["error"]!;
                    Assert.AreEqual("Cancelled", (string?)error["code"]);
                    Assert.AreEqual("NotStarted", (string?)error["execution_state"]);
                    Assert.AreEqual(2, (await FixtureStateAsync(file)).Value<int>("count"));
                } finally { gate.Release(); }
                Okay(Result((await remaining).Body));
                Assert.AreEqual(3, (await FixtureStateAsync(file)).Value<int>("count"));

                var field = await Call(second, "get_window_state", Window());
                const string originalText = "Original fixture text";
                var typed = await Call(second, "type_text", new JObject { ["window_id"] = window, ["element_index"] = Index(field, "Edit", "Message"),
                    ["state_id"] = StateId(field), ["text"] = originalText, ["include_screenshot"] = false });
                Okay(typed);
                Assert.AreEqual(0, (await FixtureStateAsync(file)).Value<int>("selectionLength"));

                const string taskId = "test-cancel-after-selection";
                using var hook = new SelectionKeyHook(new IntPtr(window), () => InProcess(secondSession, Cancel(202)).GetAwaiter().GetResult().Status);
                await hook.Ready.Task;
                using (var stopped = await PostAsync(second, endpoint, Request(202, "type_text", new JObject {
                    ["window_id"] = window, ["element_index"] = Index(typed, "Edit", "Message"), ["state_id"] = StateId(typed),
                    ["text"] = "Must never be typed", ["replace"] = true, ["submit"] = true, ["include_screenshot"] = false, ["task_id"] = taskId
                }))) {
                    Assert.AreEqual(HttpStatusCode.Accepted, stopped.StatusCode);
                    Assert.AreEqual("", await stopped.Content.ReadAsStringAsync());
                }
                Assert.AreSame(hook.Triggered.Task, await Task.WhenAny(hook.Triggered.Task, Task.Delay(2000)), "The fixture did not receive Ctrl+A");
                Assert.AreEqual(202, await hook.Triggered.Task);
                var selected = await FixtureStateAsync(file, state => state.Value<int>("selectionLength") == originalText.Length);
                Assert.AreEqual(originalText, selected.Value<string>("text"));
                Assert.AreEqual(0, selected.Value<int>("submissions"));
                var checkpoint = controller.LatestFailure!;
                var cancelledStage = checkpoint.Snapshot()["error"]!;
                Assert.AreEqual("type_text", (string?)cancelledStage["step"]);
                Assert.IsTrue(cancelledStage.Value<bool>("selection_sent"));
                Assert.AreNotEqual(true, cancelledStage.Value<bool?>("text_sent"));
                const string otherTask = "test-unrelated-task";
                JObject Resume(string task = taskId) => new JObject { ["window_id"] = window, ["task_id"] = task, ["resume_from"] = checkpoint.Id };
                var unrelated = await Call(first, "activate_window", Resume(otherTask));
                Assert.AreEqual("TaskCheckpointChanged", (string?)unrelated["structuredContent"]?["error"]?["code"]);
                var readback = await Call(first, "get_window_state", new JObject { ["window_id"] = window, ["task_id"] = otherTask });
                Okay(readback);
                StringAssert.Contains(Text(readback), originalText);
                var uncheckedResume = await Call(first, "activate_window", Resume());
                Assert.AreEqual("TaskObservationRequired", (string?)uncheckedResume["structuredContent"]?["error"]?["code"]);
                Assert.AreEqual(true, (bool?)uncheckedResume["structuredContent"]?["task"]?["checkpoint"]?["needs_observation"]);
                var observed = await Call(first, "get_window_state", new JObject { ["window_id"] = window, ["task_id"] = taskId });
                Okay(observed);
                Assert.AreEqual(false, (bool?)observed["structuredContent"]?["task"]?["checkpoint"]?["needs_observation"]);
                var resumed = await Call(second, "activate_window", Resume());
                Okay(resumed);
                Assert.AreEqual(checkpoint.Id, (long?)resumed["structuredContent"]?["operation"]?["resumed_from"]);
                var current = await FixtureStateAsync(file);
                Assert.AreEqual(originalText, current.Value<string>("text"));
                Assert.AreEqual(0, current.Value<int>("submissions"));
                Assert.AreEqual(worker, controller.WorkerProcessId, "Cancellation must keep the native worker connected");
            } finally {
                controller.Stop();
                if (!fixture.HasExited) { fixture.Kill(); fixture.WaitForExit(3000); }
                await controller.Trace.FlushAsync();
                Directory.Delete(root, true);
            }
        }

        private static HttpClient CreateClient(string bearer)
        {
            var client = new HttpClient(new HttpClientHandler { UseProxy = false });
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", bearer);
            return client;
        }
        private static async Task<string> InitializeAsync(HttpClient client, string endpoint)
        {
            using var response = await PostAsync(client, endpoint, new JObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize" });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var session = response.Headers.GetValues("Mcp-Session-Id").Single();
            client.DefaultRequestHeaders.TryAddWithoutValidation("Mcp-Session-Id", session);
            return session;
        }
        private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string endpoint, JObject message)
        {
            using var content = new StringContent(message.ToString(Formatting.None), Encoding.UTF8, "application/json");
            return await client.PostAsync(endpoint, content);
        }
        private static JObject Request(int id, string tool, JObject args) => new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = "tools/call",
            ["params"] = new JObject { ["name"] = tool, ["arguments"] = args } };
        private static JObject Cancel(JToken id) => new JObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/cancelled",
            ["params"] = new JObject { ["requestId"] = id } };
        private static JObject Result(string body)
        {
            var rpc = JObject.Parse(body);
            Assert.IsNull(rpc["error"], body);
            return (JObject)rpc["result"]!;
        }
        private static string Text(JObject result) => string.Join("\n", ((JArray)result["content"]!).Where(c => (string?)c["type"] == "text").Select(c => (string?)c["text"]));
        private static string StateId(JObject result) => (string)result["structuredContent"]!["state_id"]!;
        private static void Okay(JObject result) => Assert.IsFalse(result.Value<bool>("isError"), Text(result));
        private static void Stale(JObject result, JObject previous)
        {
            Assert.IsTrue(result.Value<bool>("isError"));
            Assert.AreEqual("StaleObservation", (string?)result["structuredContent"]?["error"]?["code"], Text(result));
            Assert.AreEqual("NotStarted", (string?)result["structuredContent"]?["error"]?["execution_state"]);
            Assert.IsTrue(Guid.TryParseExact(StateId(result), "N", out _));
            Assert.AreNotEqual(StateId(previous), StateId(result));
        }
        private static int Index(JObject state, string role, string label)
        {
            var match = Regex.Match(Text(state), @"\[(\d+)\] " + role + ".*" + label);
            Assert.IsTrue(match.Success, Text(state));
            return int.Parse(match.Groups[1].Value);
        }
        private static JObject Coordinates(JObject result, JObject fixture, long window)
        {
            var state = result["structuredContent"]!;
            var bounds = state["window_bounds"]!;
            var button = fixture["incrementBounds"]!;
            var width = state.Value<int>("image_width"); var height = state.Value<int>("image_height");
            Assert.IsTrue(width > 0 && height > 0, "Coordinate input requires a real image");
            return new JObject { ["window_id"] = window, ["state_id"] = StateId(result),
                ["include_screenshot"] = true,
                ["x"] = (button.Value<double>("x") + button.Value<double>("width") / 2 - bounds.Value<int>("x")) * width / bounds.Value<int>("width"),
                ["y"] = (button.Value<double>("y") + button.Value<double>("height") / 2 - bounds.Value<int>("y")) * height / bounds.Value<int>("height") };
        }
        private static async Task<JObject> FixtureStateAsync(string file, Func<JObject, bool>? ready = null)
        {
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 4000) {
                try {
                    var value = JObject.Parse(File.ReadAllText(file));
                    if (ready == null || ready(value)) return value;
                } catch (IOException) { } catch (JsonException) { }
                await Task.Delay(20);
            }
            throw new AssertFailedException("Fixture did not report the expected state");
        }

        private sealed class SelectionKeyHook : IDisposable
        {
            internal readonly TaskCompletionSource<Dispatcher> Ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource<int> Triggered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly Thread _thread;

            internal SelectionKeyHook(IntPtr window, Func<int> cancel)
            {
                _thread = new Thread(() => {
                    var triggered = false;
                    HookProc callback = (code, message, data) => {
                        // Observe injected A in the fixture during the Ctrl+A batch and let it through.
                        if (!triggered && code == 0 && message == new IntPtr(0x100) && Marshal.ReadInt32(data) == 0x41
                            && (Marshal.ReadInt32(data, 8) & 0x10) != 0 && GetForegroundWindow() == window) {
                            triggered = true;
                            try { Triggered.TrySetResult(cancel()); }
                            catch (Exception error) { Triggered.TrySetException(error); }
                        }
                        return CallNextHookEx(IntPtr.Zero, code, message, data);
                    };
                    var hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
                    if (hook == IntPtr.Zero) { Ready.TrySetException(new Win32Exception(Marshal.GetLastWin32Error())); return; }
                    try {
                        Ready.TrySetResult(Dispatcher.CurrentDispatcher);
                        Dispatcher.Run();
                    } finally { UnhookWindowsHookEx(hook); GC.KeepAlive(callback); }
                }) { IsBackground = true };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }
            public void Dispose()
            {
                if (Ready.Task.Status == TaskStatus.RanToCompletion) Ready.Task.Result.BeginInvokeShutdown(DispatcherPriority.Send);
                _thread.Join(2000);
            }
        }
        private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int type, HookProc callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    }
}
