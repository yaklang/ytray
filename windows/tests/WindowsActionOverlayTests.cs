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
                await Wait(() => File.Exists(stateFile));
                fixture!.Refresh(); var target = WindowsControlTarget.Resolve(fixture.MainWindowHandle);
                await WindowsApplicationControl.ActivateAsync(target);
                var state = engine.Call("get_window_state", new JObject { ["window_id"] = target.Handle.ToInt64(), ["include_screenshot"] = false });
                var button = ((JArray)state["structuredContent"]!["elements"]!).OfType<JObject>().Single(row => (string?)row["automation_id"] == "Increment");
                var frame = button["frame"]!;
                var point = new Point((int)(frame.Value<double>("x") + frame.Value<double>("w") / 2), (int)(frame.Value<double>("y") + frame.Value<double>("h") / 2));
                var foreground = GetForegroundWindow();
                Assert.IsTrue(indicator.Operation(41, "prepare", target.Handle, "点击「Increment」", "YTray 桌面操作反馈测试"));
                var surfaces = FindSurfaces();
                Assert.AreEqual(System.Windows.Forms.Screen.AllScreens.Length * 4, surfaces.Count(window => WindowsActionOverlay.WindowKind(window) == 3), "Every monitor needs four narrow edge surfaces");
                Assert.AreEqual(1, surfaces.Count(window => WindowsActionOverlay.WindowKind(window) == 2), "Only one operation banner should be shown");
                Assert.AreEqual(foreground, GetForegroundWindow(), "Global operation feedback stole focus");
                foreach (var surface in surfaces) Assert.AreEqual(0x080800a8, GetWindowLong(surface, -20) & 0x080800a8);
                foreach (var surface in surfaces) {
                    Assert.IsTrue(GetWindowDisplayAffinity(surface, out var affinity));
                    Assert.AreEqual(0u, affinity, "UU/remote viewers must be able to capture feedback without changing its flags");
                }
                Assert.IsTrue(indicator.Operation(41, "running", target.Handle, "", ""));
                using (var feedback = indicator.Begin(target.Handle, point, "点击")) {
                    var overlay = FindOverlay(); Assert.AreNotEqual(IntPtr.Zero, overlay, "The visual renderer did not create its window");
                    await Wait(() => IsWindowVisible(overlay));
                    Assert.AreEqual(0x080800a8, GetWindowLong(overlay, -20) & 0x080800a8);
                    Assert.AreEqual(foreground, GetForegroundWindow(), "Showing feedback stole focus");
                    Assert.IsFalse(target.IsObscured(IntPtr.Zero), "Feedback was treated as an application covering the fixture");
                    Assert.IsFalse(WindowsControlTarget.Enumerate().Any(window => window.Handle == overlay), "Feedback leaked into window discovery");
                    await WindowsApplicationControl.ActivateAsync(target);
                    WindowsNativeInput.Click(target.Handle, point.X, point.Y, "left", 1, true);
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
                    await Task.Delay(70); // Cross several render ticks: it must remain hidden.
                    using var hidden = Capture(rectangle);
                    if (!string.IsNullOrEmpty(output)) hidden.Save(Path.Combine(output, "indicator-hidden.png"), ImageFormat.Png);
                    Assert.IsFalse(IsWindowVisible(handle), "The renderer reappeared during capture");
                    Assert.IsTrue(surfaces.All(window => !IsWindowVisible(window)), "Global feedback contaminated the AI capture");
                    Assert.IsTrue(DifferentPixels(shown, hidden) > 60, "Hiding feedback did not remove the painted cursor from the image");
                }
                await Wait(() => IsWindowVisible(handle));
                var monitor = System.Windows.Forms.Screen.FromHandle(target.Handle).Bounds;
                var captured = WindowsNativeCapture.Read(IntPtr.Zero, monitor, monitor.Width, false);
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
                Assert.IsTrue(timer.ElapsedMilliseconds < 15000, "Readable motion must still have a bounded action delay");
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
        private static Bitmap Capture(Rectangle rectangle)
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
        private static int DifferentPixels(Bitmap first, Bitmap second)
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
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(IntPtr window, out uint affinity);
        [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    }
}
