#nullable enable
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace YTray.Native
{
    internal static class WindowsNativeCapture
    {
        // PrintWindow can block in a target process. At most one pending print is
        // kept in this worker; the host can terminate the worker on a timeout.
        private static Task<Bitmap?>? _pending;
        internal static JObject Read(IntPtr window, Rectangle bounds, int maximum, bool obscured)
        {
            Bitmap? image = null;
            if (window != IntPtr.Zero && (_pending == null || _pending.IsCompleted)) {
                if (_pending?.Status == TaskStatus.RanToCompletion) _pending.Result?.Dispose();
                _pending = Task.Run(() => Print(window, bounds));
                if (_pending.Wait(450)) { image = _pending.Result; _pending = null; }
            }
            if (image == null && !obscured) {
                image = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
                using var graphics = Graphics.FromImage(image);
                using var indicator = WindowsActionOverlay.HideForCapture();
                graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            }
            if (image == null) return new JObject { ["screenshot_error"] = "目标窗口未提供可用截图，请恢复窗口或使用控件信息。", ["screenshot_frame_valid"] = false };
            using (image) {
                var scale = Math.Min(1.0, maximum / (double)Math.Max(image.Width, image.Height));
                var width = Math.Max(1, (int)Math.Round(image.Width * scale)); var height = Math.Max(1, (int)Math.Round(image.Height * scale));
                using var resized = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                using (var graphics = Graphics.FromImage(resized)) { graphics.InterpolationMode = InterpolationMode.HighQualityBicubic; graphics.DrawImage(image, 0, 0, width, height); }
                using var output = new MemoryStream(); resized.Save(output, ImageFormat.Png);
                return new JObject { ["screenshot_width"] = width, ["screenshot_height"] = height, ["screenshot_frame_valid"] = true,
                    ["image"] = new JObject { ["type"] = "image", ["mimeType"] = "image/png", ["data"] = Convert.ToBase64String(output.ToArray()) } };
            }
        }
        private static Bitmap? Print(IntPtr window, Rectangle bounds)
        {
            var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            try {
                bool captured;
                using (var graphics = Graphics.FromImage(bitmap)) {
                    var dc = graphics.GetHdc();
                    try { captured = PrintWindow(window, dc, 2); }
                    finally { graphics.ReleaseHdc(dc); }
                }
                if (!captured) { bitmap.Dispose(); return null; }
                // GPU-backed windows sometimes report success but provide black pixels.
                var visible = false;
                for (var x = 1; x <= 6; x++) for (var y = 1; y <= 6; y++) {
                    var pixel = bitmap.GetPixel(x * (bitmap.Width - 1) / 7, y * (bitmap.Height - 1) / 7);
                    if (pixel.R + pixel.G + pixel.B > 12) visible = true;
                }
                if (visible) return bitmap;
            } catch { bitmap.Dispose(); return null; }
            bitmap.Dispose(); return null;
        }
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    }
}
