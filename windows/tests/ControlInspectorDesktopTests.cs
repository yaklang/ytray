#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YTray.Core;
using YTray.Core.SystemControl;
using YTray.Models;
using YTray.Native;
using YTray.Views;

namespace YTray.Tests
{
    [TestClass]
    public class ControlInspectorDesktopTests
    {
        [TestMethod, TestCategory("ControlDesktop")]
        public async Task PipelineOpensRecordedCallsAcrossSessionsWithoutReplacingAgentState()
        {
            if (Environment.GetEnvironmentVariable("YTRAY_CONTROL_INTERACTIVE") != "1")
                Assert.Inconclusive("Run the independent desktop fixture with -Interactive");
            var executable = Environment.GetEnvironmentVariable("YTRAY_CONTROL_FIXTURE_EXE") ?? "";
            Assert.IsTrue(File.Exists(executable));
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() => {
                var dispatcher = Dispatcher.CurrentDispatcher;
                dispatcher.BeginInvoke(new Action(async () => {
                    try { await VerifyAsync(executable); completion.SetResult(true); }
                    catch (Exception error) { completion.SetException(error); }
                    finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
                }));
                Dispatcher.Run();
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            Assert.AreSame(completion.Task, await Task.WhenAny(completion.Task, Task.Delay(45000)), "Inspector desktop test timed out");
            await completion.Task;
            Assert.IsTrue(thread.Join(3000));
        }

        private static async Task VerifyAsync(string executable)
        {
            var app = new App(initializeServices: false) { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
            ThemeManager.Initialize(AppThemePreference.Dark);
            var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ytray-inspector-" + Guid.NewGuid().ToString("N"))).FullName;
            var statePath = Path.Combine(root, "fixture.json");
            using var controller = new SystemControlController(root);
            using var fixture = Process.Start(new ProcessStartInfo(executable, "\"" + statePath + "\"") { UseShellExecute = false })!;
            try {
                await WaitAsync(() => { fixture.Refresh(); return File.Exists(statePath) && fixture.MainWindowHandle != IntPtr.Zero; });
                var target = WindowsControlTarget.Resolve(fixture.MainWindowHandle);
                var presentation = WindowPresentation.Read(target, CancellationToken.None);
                Assert.AreEqual((0, "WPF"), (presentation.Group, presentation.Framework));
                Assert.IsNotNull(presentation.Icon);
                Assert.IsTrue(presentation.Icon.IsFrozen);
                await controller.StartAsync(new ControlSessionOptions { Executables = new[] { executable }, AllowDesktop = false, AllowForeground = true });
                Assert.IsTrue(controller.IsRunning, controller.LastError);
                using var gateway = new ControlGateway(controller);
                gateway.Start(0);
                using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
                var config = JObject.Parse(controller.ConfigurationJSON!);
                http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", (string)config["mcpServers"]!["ytray-system"]!["headers"]!["Authorization"]!);
                var endpoint = "http://127.0.0.1:" + gateway.BoundPort + "/system/mcp";
                async Task ReconnectAsync() {
                    http.DefaultRequestHeaders.Remove("Mcp-Session-Id");
                    using var init = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"Trace fixture agent\"}}}", Encoding.UTF8, "application/json");
                    using var initialized = await http.PostAsync(endpoint, init);
                    initialized.EnsureSuccessStatusCode();
                    http.DefaultRequestHeaders.TryAddWithoutValidation("Mcp-Session-Id", initialized.Headers.GetValues("Mcp-Session-Id").Single());
                }
                await ReconnectAsync();
                var rpcId = 0;
                async Task<JObject> Call(string tool, JObject args, bool task = true) {
                    if (task) args["task_id"] = "test-inspector-pipeline";
                    var rpc = new JObject { ["jsonrpc"] = "2.0", ["id"] = ++rpcId, ["method"] = "tools/call",
                        ["params"] = new JObject { ["name"] = tool, ["arguments"] = args } };
                    using var content = new StringContent(rpc.ToString(Formatting.None), Encoding.UTF8, "application/json");
                    using var response = await http.PostAsync(endpoint, content);
                    response.EnsureSuccessStatusCode();
                    var message = JObject.Parse(await response.Content.ReadAsStringAsync());
                    Assert.IsNull(message["error"], message.ToString());
                    return (JObject)message["result"]!;
                }
                Assert.IsFalse((await Call("system_status", new JObject(), task: false)).Value<bool>("isError"));
                var window = target.Handle.ToInt64();
                var activated = await Call("activate_window", new JObject { ["window_id"] = window });
                Assert.IsFalse(activated.Value<bool>("isError"), activated.ToString());
                var state = (JObject)activated["structuredContent"]!;
                var inspected = await controller.InspectAsync(target, false, CancellationToken.None);
                Assert.IsNull(inspected.Image);
                var button = ((JArray)inspected.Layout["elements"]!).OfType<JObject>().Single(row => (string?)row["automation_id"] == "Increment");
                CollectionAssert.Contains(button["patterns"]!.Values<string>().ToArray(), "Invoke");
                Assert.IsNotNull(button["frame"]);
                Assert.IsNotNull((await controller.InspectAsync(target, true, CancellationToken.None)).Image);
                var clicked = await Call("click", new JObject { ["window_id"] = window,
                    ["element_index"] = button.Value<int>("element_index"), ["state_id"] = state["state_id"] });
                Assert.IsFalse(clicked.Value<bool>("isError"), clicked.ToString());
                Assert.AreEqual(1, JObject.Parse(File.ReadAllText(statePath)).Value<int>("count"));
                await ReconnectAsync();
                for (var i = 0; i < 6; i++)
                    Assert.IsFalse((await Call("list_windows", new JObject())).Value<bool>("isError"));
                var failed = await Call("press_key", new JObject { ["window_id"] = window, ["key"] = "f99",
                    ["state_id"] = clicked["structuredContent"]!["state_id"] });
                Assert.IsTrue(failed.Value<bool>("isError"));
                var operation = failed["structuredContent"]!["operation"]!;
                var steps = (JArray)operation["steps"]!;
                CollectionAssert.AreEqual(new[] { "target", "activate", "locate", "press_key" }, steps.Select(step => (string?)step["step"]).ToArray());
                Assert.AreEqual("failed", (string?)steps.Last!["status"]);
                Assert.AreEqual("InvalidKey", (string?)operation["error"]!["code"]);
                Assert.AreEqual("press_key", (string?)failed["structuredContent"]!["error"]!["step"]);
                Assert.IsNotNull(failed["structuredContent"]!["state_id"]);
                var checkpoint = (long)operation["id"]!;
                Assert.AreEqual(checkpoint, (long?)failed["structuredContent"]!["task"]!["checkpoint"]!["id"]);
                Assert.AreEqual(9L, (long?)failed["structuredContent"]!["task"]!["call_count"]);
                var blocked = await Call("click", new JObject { ["window_id"] = window, ["element_index"] = button.Value<int>("element_index") });
                Assert.AreEqual("TaskInterrupted", (string?)blocked["structuredContent"]?["error"]?["code"]);
                Assert.AreEqual(1, JObject.Parse(File.ReadAllText(statePath)).Value<int>("count"));
                await ReconnectAsync();
                var observed = await Call("get_window_state", new JObject { ["window_id"] = window });
                Assert.IsFalse(observed.Value<bool>("isError"), observed.ToString());
                Assert.AreEqual(checkpoint, (long?)observed["structuredContent"]!["task"]!["checkpoint"]!["id"]);

                controller.TraceDetailsRequested += id => ControlTraceWindow.ShowFor(controller, callId: id);
                var failedTrace = (string)operation["trace_id"]!;
                var observedTrace = (string)observed["structuredContent"]!["operation"]!["trace_id"]!;
                var worker = controller.WorkerProcessId!.Value;
                var banner = IntPtr.Zero;
                await WaitAsync(() => (banner = FindSurface(worker, 2)) != IntPtr.Zero
                    && FindSurfaces(worker, 7).Count == 4 && FindSurface(worker, 6) != IntPtr.Zero);
                Assert.IsTrue(GetWindowRect(banner, out var panel));
                var center = (panel.Left + panel.Right) / 2;
                var older = FindSurface(worker, 6, bounds => bounds.Left < center);
                var nodes = FindSurfaces(worker, 7);
                Assert.AreEqual(1, nodes.Select(node => node.Bounds.Top).Distinct().Count(), "Pipeline nodes must share one row");
                Assert.AreNotEqual(IntPtr.Zero, older);
                ClickSurface(older);
                var newer = IntPtr.Zero;
                await WaitAsync(() => (newer = FindSurface(worker, 6, bounds => bounds.Left > center)) != IntPtr.Zero);
                var failedNode = FindSurfaces(worker, 7).Last().Handle;
                Assert.AreEqual(0, GetWindowLong(failedNode, -20) & 0x20);
                ClickSurface(failedNode);
                await WaitAsync(() => app.Windows.OfType<ControlTraceWindow>().Any(view => view.SelectedCallId == failedTrace));
                var traceView = app.Windows.OfType<ControlTraceWindow>().Single();
                Assert.AreEqual(1, traceView.RunList.Items.Count, "The task groups across sessions and the standalone status check does not create a row");
                Assert.AreEqual(11, traceView.CallList.Items.Count);
                await WaitAsync(() => FindVisual<ScrollViewer>(traceView.CallList)?.HorizontalOffset > 0 && traceView.EarlierStepsButton.IsEnabled);
                var timeline = FindVisual<ScrollViewer>(traceView.CallList)!;
                var selectedOffset = timeline.HorizontalOffset;
                traceView.EarlierStepsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitAsync(() => timeline.HorizontalOffset < selectedOffset && traceView.LaterStepsButton.IsEnabled);
                Assert.AreEqual(failedTrace, traceView.SelectedCallId);
                Assert.AreEqual("press_key", traceView.ToolNameText.Text);
                StringAssert.Contains(traceView.CallErrorText.Text, "f99");
                Assert.IsFalse(app.Windows.OfType<ControlInspectorWindow>().Any(), "A failed trace node must not open or analyze the target window");
                ClickSurface(newer);
                await WaitAsync(() => FindSurface(worker, 6, bounds => bounds.Left > center) == IntPtr.Zero);
                Assert.IsTrue(GetWindowRect(banner, out var afterScroll));
                Assert.AreEqual(panel, afterScroll, "Scrolling must keep the card the same size and position");
                Assert.IsTrue(GetWindowLong(banner, -20) != 0 && (GetWindowLong(banner, -20) & 0x20) != 0);
                var output = Environment.GetEnvironmentVariable("YTRAY_CONTROL_TEST_OUTPUT");
                ClickSurface(FindSurfaces(worker, 7).Last().Handle);
                await WaitAsync(() => traceView.SelectedCallId == observedTrace);
                Assert.AreEqual("get_window_state", traceView.ToolNameText.Text);
                Assert.AreEqual(Visibility.Visible, traceView.ReadableDetails.Visibility);
                Assert.AreEqual(Visibility.Collapsed, traceView.PayloadText.Visibility);
                Assert.IsTrue(traceView.ResultFields.Items.OfType<ControlTraceDetails.Field>().Any(field => field.Label == "原生布局"));
                Assert.IsTrue(traceView.StageList.Items.OfType<ControlTraceDetails.Stage>().Any(stage => stage.Name == "读取 Windows 原生控件"));
                traceView.RequestButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                StringAssert.Contains(traceView.PayloadText.Text, "test-inspector-pipeline");
                StringAssert.Contains(traceView.CallMetadataText.Text, "Trace fixture agent");
                traceView.ResponseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                StringAssert.Contains(traceView.PayloadText.Text, "state_id");
                traceView.TimeModeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitAsync(() => traceView.TimeTrack.Children.Count == traceView.CallList.Items.Count);
                ((Button)traceView.TimeTrack.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual("activate_window", traceView.ToolNameText.Text);
                traceView.StepModeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual(Visibility.Visible, traceView.CallList.Visibility);
                traceView.Close();
                var latestFailure = controller.LatestFailure!;
                Assert.AreEqual(checkpoint, latestFailure.Id);
                Assert.AreEqual(target.Handle, latestFailure.Target?.Handle);
                ControlInspectorWindow.ShowFor(controller, failure: latestFailure);
                ControlInspectorWindow? inspector = null;
                await WaitAsync(() => {
                    inspector = app.Windows.OfType<ControlInspectorWindow>().SingleOrDefault();
                    return inspector != null && inspector.IsVisible && inspector.AnalysisButton.IsEnabled && inspector.ObservationText.Text.Contains("UI Automation");
                });
                StringAssert.Contains(inspector!.FailureTitle.Text, "按键");
                StringAssert.Contains(inspector.FailureReason.Text, "f99");
                Assert.IsTrue(inspector.ControlTree.Items.Count > 0);
                Assert.IsNull(inspector.CaptureImage.Source, "Opening failure details starts with native layout");
                Assert.IsTrue(inspector.LayoutCanvas.Children.Count > 1, "The inspector must draw the native bounds");
                Assert.AreEqual(10, inspector.TaskCalls.Items.Count);
                Assert.AreEqual(Visibility.Collapsed, inspector.LayoutPlaceholder.Visibility);
                Assert.AreEqual(new WindowInteropHelper(inspector).Handle, GetForegroundWindow());
                inspector.ViewTraceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitAsync(() => app.Windows.OfType<ControlTraceWindow>().Any(view => view.SelectedCallId == failedTrace));
                traceView = app.Windows.OfType<ControlTraceWindow>().Single();
                Assert.IsTrue(traceView.InputFields.Items.OfType<ControlTraceDetails.Field>().Any(field => field.Label == "按键" && field.Value == "F99"));
                Assert.IsTrue(traceView.StageList.Items.OfType<ControlTraceDetails.Stage>().Any(stage => stage.Name == "按下 F99" && stage.Failed));
                StringAssert.Contains(traceView.CallErrorText.Text, "f99");
                traceView.ResponseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                StringAssert.Contains(traceView.PayloadText.Text, "InvalidKey");
                traceView.DetailsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (!string.IsNullOrEmpty(output)) {
                    await WaitAsync(() => FindVisual<ScrollViewer>(traceView.CallList)?.HorizontalOffset > 0);
                    traceView.UpdateLayout();
                    ControlReviewCapture.Save(traceView, Path.Combine(output, "control-trace-dark.png"));
                    ThemeManager.SetPreference(AppThemePreference.Light);
                    traceView.UpdateLayout();
                    ControlReviewCapture.Save(traceView, Path.Combine(output, "control-trace-light.png"));
                    traceView.TimeModeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    traceView.UpdateLayout();
                    ControlReviewCapture.Save(traceView, Path.Combine(output, "control-trace-timing.png"));
                    ThemeManager.SetPreference(AppThemePreference.Dark);
                }
                traceView.Close();
                inspector.Activate();
                inspector.TaskCalls.SelectedIndex = 1;
                await WaitAsync(() => inspector.AnalysisButton.IsEnabled && inspector.ObservationText.Text.Contains("UI Automation"));
                StringAssert.Contains(inspector.FailureOutcome.Text, "动作已投递");
                inspector.TaskCalls.SelectedIndex = 8;
                await WaitAsync(() => inspector.AnalysisButton.IsEnabled && inspector.ObservationText.Text.Contains("UI Automation"));
                StringAssert.Contains(inspector.FailureReason.Text, "f99");
                var fit = ((MatrixTransform)inspector.LayoutWorld.RenderTransform).Matrix;
                inspector.ZoomPercentButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var camera = ((MatrixTransform)inspector.LayoutWorld.RenderTransform).Matrix;
                Assert.AreEqual(1d, camera.M11);
                Assert.AreNotEqual(fit, camera);
                var scene = inspector.LayoutCanvas.Children.Count;
                var viewport = inspector.LayoutViewport.ActualWidth;
                inspector.Width = Math.Max(inspector.MinWidth, inspector.Width - 80);
                inspector.UpdateLayout();
                Assert.IsTrue(inspector.LayoutViewport.ActualWidth < viewport);
                Assert.AreEqual(camera, ((MatrixTransform)inspector.LayoutWorld.RenderTransform).Matrix);
                Assert.AreEqual(scene, inspector.LayoutCanvas.Children.Count);
                inspector.Width += 80;
                inspector.UpdateLayout();
                await DragLayoutAsync(inspector);
                var panned = ((MatrixTransform)inspector.LayoutWorld.RenderTransform).Matrix;
                Assert.AreNotEqual(camera, panned, "Dragging with the left mouse button must pan the viewport");
                var regularWidth = inspector.LayoutViewport.ActualWidth;
                var modal = inspector.Dispatcher.InvokeAsync(() => inspector.ExpandLayoutButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
                await WaitAsync(() => Window.GetWindow(inspector.LayoutViewport) is Window view && view.Owner == inspector
                    && view.IsVisible && inspector.LayoutViewport.ActualWidth > regularWidth);
                var expanded = Window.GetWindow(inspector.LayoutViewport)!;
                var fittedScale = Math.Min(6, Math.Min((inspector.LayoutViewport.ActualWidth - 24) / inspector.LayoutWorld.Width,
                    (inspector.LayoutViewport.ActualHeight - 24) / inspector.LayoutWorld.Height));
                await WaitAsync(() => Math.Abs(((MatrixTransform)inspector.LayoutWorld.RenderTransform).Matrix.M11 - fittedScale) < .01);
                if (!string.IsNullOrEmpty(output)) ControlReviewCapture.Save(expanded, Path.Combine(output, "control-layout-expanded.png"));
                expanded.Close();
                await modal;
                Assert.AreSame(inspector, Window.GetWindow(inspector.LayoutViewport));
                Assert.AreEqual(panned, ((MatrixTransform)inspector.LayoutWorld.RenderTransform).Matrix);
                inspector.FitLayoutButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var fitted = ((MatrixTransform)inspector.LayoutWorld.RenderTransform).Matrix;
                var worldCenter = fitted.Transform(new System.Windows.Point(inspector.LayoutWorld.Width / 2, inspector.LayoutWorld.Height / 2));
                Assert.IsTrue(new Rect(0, 0, inspector.LayoutViewport.ActualWidth, inspector.LayoutViewport.ActualHeight).Contains(worldCenter),
                    "The small viewport must fit the restored layout using its own size");
                var element = inspector.LayoutCanvas.Children.OfType<Border>().Single(block => (block.ToolTip as string)?.Contains("Button Increment") == true);
                element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
                StringAssert.Contains(inspector.SelectedMetadata.Text, "Invoke");
                if (!string.IsNullOrEmpty(output)) {
                    inspector.UpdateLayout();
                    ControlReviewCapture.Save(inspector, Path.Combine(output, "control-inspector-failure.png"));
                }
                Assert.IsTrue(inspector.HighlightButton.IsEnabled);
                inspector.HighlightButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitAsync(() => FindSurface(worker, 5) != IntPtr.Zero);

                var resumed = await Call("click", new JObject { ["window_id"] = window,
                    ["element_index"] = button.Value<int>("element_index"), ["state_id"] = observed["structuredContent"]!["state_id"], ["resume_from"] = checkpoint });
                Assert.IsFalse(resumed.Value<bool>("isError"), resumed.ToString());
                Assert.AreEqual(checkpoint, (long?)resumed["structuredContent"]!["operation"]!["resumed_from"]);
                Assert.IsNull(resumed["structuredContent"]!["task"]!["checkpoint"]);
                Assert.AreEqual(2, JObject.Parse(File.ReadAllText(statePath)).Value<int>("count"));

                await controller.Trace.FlushAsync();
                using var reopened = new ControlTrace(root);
                var saved = await reopened.ReadSavedAsync();
                Assert.AreEqual(13, saved.Length);
                Assert.AreEqual("", (string?)saved.Single(call => (string?)call["tool"] == "system_status")["task_id"]);
                var taskCalls = saved.Where(call => (string?)call["task_id"] == "test-inspector-pipeline").ToArray();
                Assert.AreEqual(12, taskCalls.Length);
                Assert.AreEqual("f99", (string?)saved.Single(call => (string?)call["id"] == failedTrace)["request"]?["arguments"]?["key"]);
                Assert.IsTrue(saved.All(call => ((string?)call["session"])?.Length == 32));
                Assert.AreEqual(1, taskCalls.Select(call => (string?)call["run_id"]).Distinct().Count());
                Assert.AreEqual(3, taskCalls.Select(call => (string?)call["session"]).Distinct().Count());

                VerifyScrollViewports(inspector);

                controller.Stop();
                controller.ToggleOperationPreview();
                var host = Process.GetCurrentProcess().Id;
                await WaitAsync(() => FindSurface(host, 2) != IntPtr.Zero && FindSurface(host, 6) != IntPtr.Zero);
                Assert.IsFalse(controller.IsEnabled);
                Assert.AreEqual(IntPtr.Zero, FindSurface(host, 3), "Preview only draws the card");
                if (!string.IsNullOrEmpty(output)) await SavePreviewAsync(FindSurface(host, 2), Path.Combine(output, "control-pipeline-preview.png"));
                controller.ToggleOperationPreview();
                await WaitAsync(() => FindSurface(host, 2) == IntPtr.Zero && FindSurface(host, 6) == IntPtr.Zero);
                Assert.IsFalse(controller.IsPipelinePreviewVisible);
            } finally {
                foreach (var trace in app.Windows.OfType<ControlTraceWindow>().ToArray()) trace.Close();
                foreach (var inspector in app.Windows.OfType<ControlInspectorWindow>().ToArray()) inspector.Close();
                controller.Stop();
                if (!fixture.HasExited) { fixture.Kill(); fixture.WaitForExit(3000); }
                await controller.Trace.FlushAsync();
                ThemeManager.Shutdown(); app.Shutdown();
                Directory.Delete(root, true);
            }
        }

        private static async Task SavePreviewAsync(IntPtr banner, string path)
        {
            Assert.IsTrue(GetWindowRect(banner, out var bounds));
            var rectangle = Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
            var backdrop = new Window {
                Width = 40, Height = 40, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false,
                Background = new LinearGradientBrush(System.Windows.Media.Color.FromRgb(218, 230, 245), System.Windows.Media.Color.FromRgb(245, 222, 211), 20)
            };
            backdrop.Show();
            try {
                var handle = new WindowInteropHelper(backdrop).Handle;
                Assert.IsTrue(SetWindowPos(handle, banner, rectangle.Left, rectangle.Top, rectangle.Width, rectangle.Height, 0x0010));
                await Task.Delay(100);
                Assert.IsTrue(GetWindowRect(handle, out var visible));
                Assert.AreEqual(bounds, visible);
                using var image = WindowsActionOverlayTests.Capture(rectangle);
                image.Save(path, ImageFormat.Png);

                var hit = FindSurface(Process.GetCurrentProcess().Id, 6);
                Assert.IsTrue(GetWindowRect(hit, out var hitBounds));
                var hitArea = Rectangle.FromLTRB(hitBounds.Left, hitBounds.Top, hitBounds.Right, hitBounds.Bottom);
                using (WindowsActionOverlay.HideForCapture(new Rectangle(bounds.Right + 1, bounds.Top, 4, 4))) {
                    using var before = WindowsActionOverlayTests.Capture(hitArea);
                    ShowWindow(hit, 0);
                    await Task.Delay(65);
                    Assert.IsFalse(IsWindowVisible(hit));
                    using var after = WindowsActionOverlayTests.Capture(hitArea);
                    Assert.AreEqual(0, WindowsActionOverlayTests.DifferentPixels(before, after),
                        "Withdrawing a click target must leave the arrow painted in place");
                }
            } finally { backdrop.Close(); }
        }

        private static async Task DragLayoutAsync(ControlInspectorWindow inspector)
        {
            Assert.IsTrue(GetCursorPos(out var previous));
            var layout = inspector.LayoutViewport;
            var point = layout.PointToScreen(new System.Windows.Point(layout.ActualWidth / 2, layout.ActualHeight / 2));
            var before = ((MatrixTransform)inspector.LayoutWorld.RenderTransform).Matrix;
            Assert.IsTrue(SetCursorPos((int)point.X, (int)point.Y));
            mouse_event(2, 0, 0, 0, UIntPtr.Zero);
            try {
                await WaitAsync(() => Mouse.Captured == layout);
                Assert.IsTrue(SetCursorPos((int)point.X + 55, (int)point.Y + 31));
                await WaitAsync(() => ((MatrixTransform)inspector.LayoutWorld.RenderTransform).Matrix != before);
            } finally {
                mouse_event(4, 0, 0, 0, UIntPtr.Zero);
                SetCursorPos(previous.X, previous.Y);
            }
            await WaitAsync(() => Mouse.Captured != layout);
        }

        private static void VerifyScrollViewports(ControlInspectorWindow inspector)
        {
            var windows = new ListCollectionView(Enumerable.Range(0, 40).Select(index => new {
                Title = "Fixture " + index, Subtitle = "WPF", Group = "Group " + index / 4, Icon = (object?)null,
                Details = "", Name = "Fixture " + index, FallbackIconVisibility = Visibility.Visible
            }).ToArray());
            windows.GroupDescriptions.Add(new PropertyGroupDescription("Group"));
            inspector.WindowList.ItemsSource = windows;
            var item = new TreeViewItem { Header = new TextBlock { Text = new string('W', 105) }, IsExpanded = true };
            for (var depth = 0; depth < 12; depth++) {
                var parent = new TreeViewItem { Header = "Layer " + depth, IsExpanded = true };
                parent.Items.Add(item);
                item = parent;
            }
            inspector.ControlTree.ItemsSource = new[] { item };
            inspector.UpdateLayout();
            var vertical = FindVisual<ScrollViewer>(inspector.WindowList)!;
            var horizontal = FindVisual<ScrollViewer>(inspector.ControlTree)!;
            Assert.IsTrue(vertical.ScrollableHeight > 27);
            vertical.ScrollToVerticalOffset(27);
            Assert.IsTrue(horizontal.ScrollableWidth > 30, "Deep tree labels must contribute their full width");
            horizontal.ScrollToHorizontalOffset(30);
            inspector.UpdateLayout();
            Assert.AreEqual(27d, vertical.VerticalOffset, "Grouped windows must scroll by pixel offset");
            Assert.AreEqual(30d, horizontal.HorizontalOffset);
        }

        private static T? FindVisual<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent is T match) return match;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                if (FindVisual<T>(VisualTreeHelper.GetChild(parent, i)) is T child) return child;
            return null;
        }

        private static async Task WaitAsync(Func<bool> ready)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 6000) { if (ready()) return; await Task.Delay(30); }
            Assert.Fail("Timed out waiting for fixture, failed node or inspector layout");
        }

        private static void ClickSurface(IntPtr window)
        {
            Assert.IsTrue(GetWindowRect(window, out var bounds));
            var point = new WindowsAutomation.NativePoint { X = (bounds.Left + bounds.Right) / 2, Y = (bounds.Top + bounds.Bottom) / 2 };
            Assert.AreEqual(window, WindowFromPoint(point), "The painted control must be hit-testable");
            WindowsNativeInput.Click(IntPtr.Zero, point.X, point.Y, "left", 1, true);
        }

        private static IntPtr FindSurface(int pid, int kind, Func<WindowsControlTarget.Rect, bool>? matches = null)
        {
            return FindSurfaces(pid, kind).FirstOrDefault(surface => matches == null || matches(surface.Bounds)).Handle;
        }

        private static List<(IntPtr Handle, WindowsControlTarget.Rect Bounds)> FindSurfaces(int pid, int kind)
        {
            var surfaces = new List<(IntPtr Handle, WindowsControlTarget.Rect Bounds)>();
            EnumWindows((window, _) => {
                GetWindowThreadProcessId(window, out var owner);
                if (owner == pid && IsWindowVisible(window) && WindowsActionOverlay.WindowKind(window) == kind && GetWindowRect(window, out var bounds))
                    surfaces.Add((window, bounds));
                return true;
            }, IntPtr.Zero);
            return surfaces.OrderBy(surface => surface.Bounds.Left).ToList();
        }

        private delegate bool EnumCallback(IntPtr window, IntPtr state);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, IntPtr state);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out WindowsControlTarget.Rect bounds);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(WindowsAutomation.NativePoint point);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out WindowsAutomation.NativePoint point);
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    }
}
