#nullable enable
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using YTray.Core.SystemControl;

namespace YTray.Native
{
    internal static class WindowsNativeCapture
    {
        // PrintWindow can block in a target process. At most one pending print is
        // kept in this worker; the host can terminate the worker on a timeout.
        private static Task<Bitmap?>? _pending;
        internal static JObject Read(WindowsControlTarget? target, Rectangle bounds, int maximum, bool preferScreen,
            WaitHandle? cancellation, Action<string> stage)
        {
            if (target?.IsCaptureRestricted == true) return Unavailable("目标窗口禁止截图。");
            var owned = target?.IsOwned == true;
            using var image = Capture(target, bounds, owned || !preferScreen, !owned, cancellation, stage, out var source);
            if (image == null) return Unavailable();
            var scale = Math.Min(1.0, maximum / (double)Math.Max(image.Width, image.Height));
            var size = new Size(Math.Max(1, (int)Math.Round(image.Width * scale)), Math.Max(1, (int)Math.Round(image.Height * scale)));
            using var resized = Resize(image, size);
            using var output = new MemoryStream(); resized.Save(output, ImageFormat.Png);
            return new JObject { ["screenshot_width"] = size.Width, ["screenshot_height"] = size.Height, ["screenshot_frame_valid"] = true,
                ["screenshot_source"] = source, ["image"] = new JObject { ["type"] = "image", ["mimeType"] = "image/png", ["data"] = Convert.ToBase64String(output.ToArray()) } };
        }

        internal static Bitmap? Refresh(WindowsControlTarget target, Rectangle bounds, Size size, string source, WaitHandle? cancellation)
        {
            if (target.IsCaptureRestricted) return null;
            using var image = Capture(target, bounds, source == "window_render", source == "screen" && !target.IsOwned, cancellation, null, out _);
            return image == null ? null : Resize(image, size);
        }

        private static Bitmap? Capture(WindowsControlTarget? target, Rectangle bounds, bool renderWindow, bool copyScreen,
            WaitHandle? cancellation, Action<string>? stage, out string source)
        {
            source = "";
            if (target != null && renderWindow) {
                stage?.Invoke("capture_window");
                var rendered = target.IsDisplayed ? RenderWindow(target.Handle, bounds) : null;
                try {
                    ControlCancellation.Check(cancellation);
                    if (rendered != null) {
                        target.RequireVisibleOwned();
                        target.Revalidate(requireGeometry: true);
                        source = "window_render";
                        return rendered;
                    }
                } catch { rendered?.Dispose(); throw; }
            }
            if (!copyScreen || target != null && !CanReadScreen(target)) return null;
            var frame = bounds;
            if (target != null) {
                var visible = WindowsControlTarget.VisibleBounds(target.Handle);
                frame.Intersect(Rectangle.FromLTRB(visible.Left, visible.Top, visible.Right, visible.Bottom));
                frame.Intersect(System.Windows.Forms.SystemInformation.VirtualScreen);
            }
            if (frame.Width <= 0 || frame.Height <= 0) return null;
            ControlCancellation.Check(cancellation);
            stage?.Invoke("capture_screen");
            var image = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
            try {
                using (var graphics = Graphics.FromImage(image))
                using (WindowsActionOverlay.HideForCapture(frame)) {
                    graphics.CopyFromScreen(frame.Left, frame.Top, frame.Left - bounds.Left, frame.Top - bounds.Top, frame.Size, CopyPixelOperation.SourceCopy);
                }
                if (target != null) {
                    target.Revalidate(requireGeometry: true);
                    if (!CanReadScreen(target)) { image.Dispose(); return null; }
                }
                source = "screen";
                return image;
            } catch { image.Dispose(); throw; }
        }

        private static bool CanReadScreen(WindowsControlTarget target) => target.IsDisplayed && !target.IsObscured(IntPtr.Zero);
        private static JObject Unavailable(string message = "未能取得可靠的窗口画面。") =>
            new JObject { ["screenshot_error"] = message, ["screenshot_frame_valid"] = false };

        private static Bitmap? RenderWindow(IntPtr window, Rectangle bounds)
        {
            if (_pending is { IsCompleted: false }) return null;
            if (_pending?.Status == TaskStatus.RanToCompletion) _pending.Result?.Dispose();
            _pending = Task.Run(() => Print(window, bounds));
            if (!_pending.Wait(450)) return null;
            var image = _pending.Result;
            _pending = null;
            return image;
        }

        private static Bitmap Resize(Bitmap image, Size size)
        {
            var resized = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb);
            try {
                using var graphics = Graphics.FromImage(resized);
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(image, 0, 0, size.Width, size.Height);
                return resized;
            } catch { resized.Dispose(); throw; }
        }

        internal static Rectangle ClientBounds(IntPtr window, Rectangle bounds)
        {
            var origin = new WindowsAutomation.NativePoint();
            if (!GetClientRect(window, out var client) || !ClientToScreen(window, ref origin)) return Rectangle.Empty;
            return Rectangle.Intersect(bounds, new Rectangle(origin.X, origin.Y, client.Right, client.Bottom));
        }

        private static Bitmap? Print(IntPtr window, Rectangle bounds)
        {
            var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
            try {
                bool captured;
                using (var graphics = Graphics.FromImage(bitmap)) {
                    var dc = graphics.GetHdc();
                    try { captured = PrintWindow(window, dc, 2); }
                    finally { graphics.ReleaseHdc(dc); }
                }
                if (captured && HasClientContent(bitmap, window, bounds)) return bitmap;
            } catch { bitmap.Dispose(); return null; }
            bitmap.Dispose(); return null;
        }

        private static bool HasClientContent(Bitmap image, IntPtr window, Rectangle bounds)
        {
            // A successful PrintWindow can still leave the client area black or flat.
            // Use the client rectangle so a painted title bar alone is insufficient.
            var pixels = ClientBounds(window, bounds);
            if (pixels.Width < 2 || pixels.Height < 2) return false;
            pixels.Offset(-bounds.Left, -bounds.Top);
            var first = image.GetPixel(pixels.Left + pixels.Width / 12, pixels.Top + pixels.Height / 12);
            for (var x = 1; x < 12; x++) for (var y = 1; y < 12; y++) {
                var pixel = image.GetPixel(pixels.Left + x * pixels.Width / 12, pixels.Top + y * pixels.Height / 12);
                if (Math.Abs(first.R - pixel.R) > 8 || Math.Abs(first.G - pixel.G) > 8 || Math.Abs(first.B - pixel.B) > 8) return true;
            }
            return false;
        }
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out WindowsControlTarget.Rect bounds);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref WindowsAutomation.NativePoint point);
    }
}
