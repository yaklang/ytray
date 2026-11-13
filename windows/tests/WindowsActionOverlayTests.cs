#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using YTray.Native;

namespace YTray.Tests
{
    [TestClass]
    public class WindowsActionOverlayTests
    {
        [TestMethod, TestCategory("ControlProtocol")]
        [DataRow(-1800, -1100, 1800, 1052, 1.5f)]
        [DataRow(2560, 48, 720, 1232, 2.5f)]
        [DataRow(-300, 120, 300, 140, 2f)]
        public void BannerFitsTheTargetMonitorWorkingArea(int x, int y, int width, int height, float scale)
        {
            var work = new Rectangle(x, y, width, height);
            var layout = WindowsActionOverlay.BannerLayout(work, scale);
            Assert.IsTrue(work.Contains(layout.Bounds));
            Assert.AreEqual(work.Left + (work.Width - layout.Bounds.Width) / 2, layout.Bounds.Left);
            Assert.IsTrue(layout.Bounds.Top > work.Top && layout.Bounds.Bottom < work.Bottom);
        }

        [TestMethod, TestCategory("ControlDesktop")]
        public async Task IndicatorIsVisibleInNormalCaptureAndHiddenOnlyForOwnScreenshots()
        {
            if (Environment.GetEnvironmentVariable("YTRAY_CONTROL_INTERACTIVE") != "1") Assert.Inconclusive("Run the independent desktop fixture with -Interactive");
            var executable = Environment.GetEnvironmentVariable("YTRAY_CONTROL_FIXTURE_EXE") ?? "";
            var stateFile = Path.Combine(Path.GetTempPath(), "ytray-indicator-" + Guid.NewGuid().ToString("N") + ".json");
            using var fixture = Process.Start(new ProcessStartInfo(executable, "\"" + stateFile + "\"") { UseShellExecute = false, CreateNoWindow = true });
            using var engine = new WindowsControlEngine();
            using var indicator = new WindowsActionOverlay();
            try {
                await Wait(() => {
                    fixture!.Refresh();
                    return File.Exists(stateFile) && fixture.MainWindowHandle != IntPtr.Zero;
                });
                var target = WindowsControlTarget.Resolve(fixture!.MainWindowHandle);
                await WindowsApplicationControl.ActivateAsync(target);
                var state = engine.Call("get_window_state", new JObject { ["window_id"] = target.Handle.ToInt64(), ["include_screenshot"] = false });
                var button = ((JArray)state["structuredContent"]!["elements"]!).OfType<JObject>().Single(row => (string?)row["automation_id"] == "Increment");
                var frame = button["frame"]!;
                var point = new Point((int)(frame.Value<double>("x") + frame.Value<double>("w") / 2), (int)(frame.Value<double>("y") + frame.Value<double>("h") / 2));
                Assert.IsTrue(indicator.Operation(41, "running", target.Handle, "点击「Increment」", "YTray 桌面操作反馈测试"));
                var expectedEdges = System.Windows.Forms.Screen.AllScreens.Length * 4;
                await Wait(() => FindSurfaces().Count(window => WindowsActionOverlay.WindowKind(window) == 3) == expectedEdges
                    && FindSurfaces().Any(window => WindowsActionOverlay.WindowKind(window) == 2 && IsWindowVisible(window)));
                var surfaces = FindSurfaces();
                Assert.IsFalse(surfaces.Contains(GetForegroundWindow()), "Showing operation feedback activated a feedback surface");
                var banner = surfaces.Single(window => WindowsActionOverlay.WindowKind(window) == 2);
                var screen = System.Windows.Forms.Screen.FromHandle(target.Handle);
                await Wait(() => BannerWithin(banner, screen.WorkingArea));
                Assert.IsTrue(GetWindowRect(banner, out var card));
                using (WindowsActionOverlay.HideForCapture(new Rectangle(card.Right + 1, card.Top, 4, 4))) {
                    await Task.Delay(65);
                    Assert.IsTrue(IsWindowVisible(banner), "Copying pixels outside the card must leave it visible");
                }
                var second = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(item => item.DeviceName != screen.DeviceName);
                if (second != null) {
                    Assert.IsTrue(SetWindowPos(target.Handle, IntPtr.Zero, second.WorkingArea.Left + 32, second.WorkingArea.Top + 32, 0, 0, 0x0015));
                    await Wait(() => BannerWithin(banner, second.WorkingArea));
                    Assert.IsTrue(SetWindowPos(target.Handle, IntPtr.Zero, target.Bounds.Left, target.Bounds.Top, 0, 0, 0x0015));
                    await Wait(() => BannerWithin(banner, screen.WorkingArea));
                    Assert.IsFalse(surfaces.Contains(GetForegroundWindow()), "Moving the banner activated a feedback surface");
                    await WindowsApplicationControl.ActivateAsync(target);
                }
                Assert.AreEqual(expectedEdges, surfaces.Count(window => WindowsActionOverlay.WindowKind(window) == 3), "Every monitor needs four narrow edge surfaces");
                Assert.AreEqual(1, surfaces.Count(window => WindowsActionOverlay.WindowKind(window) == 2), "Only one operation banner should be shown");
                foreach (var surface in surfaces) Assert.AreEqual(0x080800a8, GetWindowLong(surface, -20) & 0x080800a8);
                foreach (var surface in surfaces) {
                    Assert.IsTrue(GetWindowDisplayAffinity(surface, out var affinity));
                    Assert.AreEqual(0u, affinity, "UU/remote viewers must be able to capture feedback without changing its flags");
                }
                Assert.IsTrue(indicator.Operation(41, "running", target.Handle, "", ""));
                using (var feedback = indicator.Begin(target.Handle, point, "点击")) {
                    await Wait(() => IsWindowVisible(FindOverlay()));
                    var overlay = FindOverlay();
                    Assert.AreEqual(0x080800a8, GetWindowLong(overlay, -20) & 0x080800a8);
                    Assert.AreNotEqual(overlay, GetForegroundWindow(), "Showing the cursor activated it");
                    Assert.IsFalse(target.IsObscured(IntPtr.Zero), "Feedback was treated as an application covering the fixture");
                    Assert.IsFalse(WindowsControlTarget.Enumerate().Any(window => window.Handle == overlay), "Feedback leaked into window discovery");
                    await WindowsApplicationControl.ActivateAsync(target);
                    Assert.IsTrue(GetCursorPos(out var mouse));
                    WindowsNativeInput.Click(target.Handle, point.X, point.Y, "left", 1, true);
                    Assert.IsTrue(GetCursorPos(out var restored));
                    Assert.IsTrue(Math.Abs(mouse.X - restored.X) <= 1 && Math.Abs(mouse.Y - restored.Y) <= 1,
                        "The click displaced the user's mouse from its original point");
                    await Wait(() => JObject.Parse(File.ReadAllText(stateFile)).Value<int>("count") == 1);
                    feedback.Complete();
                }
                Assert.IsTrue(indicator.Operation(41, "completed", target.Handle, "", ""));
                var handle = FindOverlay();
                // Verify ordinary capture with production defaults. Altering the
                // window's capture flags here previously masked the remote bug.
                using (var preview = indicator.Begin(target.Handle, point, "点击「Increment」")) { preview.Complete(); }
                var rectangle = new Rectangle(point.X - 28, point.Y - 28, 64, 64);
                var output = Environment.GetEnvironmentVariable("YTRAY_CONTROL_TEST_OUTPUT");
                await Wait(() => { using var frameImage = Capture(rectangle); return TerracottaPixels(frameImage) > 60; });
                using var shown = Capture(rectangle);
                if (!string.IsNullOrEmpty(output)) shown.Save(Path.Combine(output, "indicator-visible.png"), ImageFormat.Png);
                Assert.IsTrue(TerracottaPixels(shown) > 60, "The translucent dart was not painted at the input coordinates");
                using (WindowsActionOverlay.HideForCapture()) {
                    var enqueue = Stopwatch.StartNew();
                    using (var hiddenFeedback = indicator.Begin(target.Handle, point, "点击")) hiddenFeedback.Complete();
                    Assert.IsTrue(enqueue.ElapsedMilliseconds < 1000, "Input feedback waited for a frame while capture suppressed rendering");
                    await Task.Delay(70); // Cross several render ticks: it must remain hidden.
                    using var hidden = Capture(rectangle);
                    if (!string.IsNullOrEmpty(output)) hidden.Save(Path.Combine(output, "indicator-hidden.png"), ImageFormat.Png);
                    Assert.IsFalse(IsWindowVisible(handle), "The renderer reappeared during capture");
                    Assert.IsTrue(surfaces.All(window => !IsWindowVisible(window)), "Global feedback contaminated the AI capture");
                    Assert.IsTrue(DifferentPixels(shown, hidden) > 60, "Hiding feedback did not remove the painted cursor from the image");
                }
                await Wait(() => IsWindowVisible(handle));
                var monitor = System.Windows.Forms.Screen.FromHandle(target.Handle).Bounds;
                var captured = WindowsNativeCapture.Read(null, monitor, monitor.Width, false, null, _ => { });
                using (var data = new MemoryStream(Convert.FromBase64String((string)captured["image"]!["data"]!)))
                using (var ownScreenshot = new Bitmap(data))
                using (var cursorArea = ownScreenshot.Clone(new Rectangle(rectangle.X - monitor.X, rectangle.Y - monitor.Y, rectangle.Width, rectangle.Height), PixelFormat.Format32bppArgb))
                    Assert.IsTrue(DifferentPixels(shown, cursorArea) > 60, "The actual YTray screenshot path captured its own feedback");
                if (!string.IsNullOrEmpty(output)) {
                    using var screenshot = Capture(Rectangle.FromLTRB(target.Bounds.Left, target.Bounds.Top, target.Bounds.Right, target.Bounds.Bottom));
                    screenshot.Save(Path.Combine(output, "native-action-indicator.png"), ImageFormat.Png);
                    using var desktop = Capture(System.Windows.Forms.Screen.FromHandle(target.Handle).Bounds);
                    desktop.Save(Path.Combine(output, "native-operation-feedback.png"), ImageFormat.Png);
                }
                await Task.Delay(1700);
                Assert.IsTrue(IsWindowVisible(handle), "The cursor disappeared between consecutive AI operations");
                var resources = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
                var timer = Stopwatch.StartNew();
                for (var i = 0; i < 12; i++) {
                    using var feedback = indicator.Begin(target.Handle, new Point(point.X + (i % 2) * 140, point.Y + 30), i % 2 == 0 ? "输入" : "滚动 ↓");
                    Assert.IsTrue(IsWindowVisible(handle), "The cursor blinked off while moving to the next action");
                    feedback.Complete();
                }
                Assert.IsTrue(timer.ElapsedMilliseconds < 1000, "Queuing input feedback waited for the cursor animation");
                await Wait(() => !IsWindowVisible(handle), 14000);
                Assert.IsTrue(GetGuiResources(Process.GetCurrentProcess().Handle, 0) <= resources + 8, "Repeated frames leaked GDI resources");
                Assert.AreEqual(1, JObject.Parse(File.ReadAllText(stateFile)).Value<int>("count"), "Feedback generated duplicate input");
                indicator.Dispose();
                await Wait(() => !IsWindow(handle));
            } finally {
                if (fixture != null && !fixture.HasExited) { fixture.Kill(); fixture.WaitForExit(3000); }
                if (File.Exists(stateFile)) File.Delete(stateFile);
            }
        }
        private static bool BannerWithin(IntPtr banner, Rectangle work)
        {
            if (!GetWindowRect(banner, out var rect)) return false;
            var bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            return bounds.Width > 0 && bounds.Height > 0 && work.Contains(bounds);
        }
        internal static Bitmap Capture(Rectangle rectangle)
        {
            var image = new Bitmap(rectangle.Width, rectangle.Height, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(image);
            var destination = graphics.GetHdc(); var screen = GetDC(IntPtr.Zero);
            try { Assert.IsTrue(BitBlt(destination, 0, 0, rectangle.Width, rectangle.Height, screen, rectangle.X, rectangle.Y, 0x40cc0020)); }
            finally { ReleaseDC(IntPtr.Zero, screen); graphics.ReleaseHdc(destination); }
            return image;
        }
        private static int TerracottaPixels(Bitmap image)
        {
            var count = 0;
            for (var y = 0; y < image.Height; y++) for (var x = 0; x < image.Width; x++) { var color = image.GetPixel(x, y); if (color.R > 100 && color.R - color.G > 20 && color.R - color.B > 20) count++; }
            return count;
        }
        internal static int DifferentPixels(Bitmap first, Bitmap second)
        {
            var count = 0;
            for (var y = 0; y < first.Height; y++) for (var x = 0; x < first.Width; x++) {
                var a = first.GetPixel(x, y); var b = second.GetPixel(x, y);
                if (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) > 45) count++;
            }
            return count;
        }
        private static IntPtr FindOverlay()
        {
            var found = IntPtr.Zero;
            EnumWindows((window, _) => { GetWindowThreadProcessId(window, out var pid); if (pid == Process.GetCurrentProcess().Id && WindowsActionOverlay.WindowKind(window) == 1) found = window; return true; }, IntPtr.Zero);
            return found;
        }
        private static List<IntPtr> FindSurfaces()
        {
            var windows = new List<IntPtr>();
            EnumWindows((window, _) => { GetWindowThreadProcessId(window, out var pid); if (pid == Process.GetCurrentProcess().Id && WindowsActionOverlay.IsOverlayWindow(window)) windows.Add(window); return true; }, IntPtr.Zero);
            return windows;
        }
        private static async Task Wait(Func<bool> predicate, int timeoutMilliseconds = 6000)
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.ElapsedMilliseconds < timeoutMilliseconds) { try { if (predicate()) return; } catch (IOException) { } await Task.Delay(25); }
            Assert.Fail("Timed out waiting for native action indicator or fixture state");
        }
        private delegate bool EnumCallback(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out WindowsControlTarget.Rect bounds);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out WindowsAutomation.NativePoint point);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(IntPtr window, out uint affinity);
        [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    }
}
