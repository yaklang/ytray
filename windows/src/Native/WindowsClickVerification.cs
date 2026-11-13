#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Newtonsoft.Json.Linq;
using YTray.Core.SystemControl;
using YTray.Models;

namespace YTray.Native
{
    internal sealed class WindowsClickVerification
    {
        private sealed class Capture
        {
            internal readonly string Id = Guid.NewGuid().ToString("N");
            internal readonly WindowsControlTarget Target;
            internal readonly Rectangle Client;
            internal readonly Size Size;
            internal readonly string Source, Data;
            internal readonly bool Foreground;
            internal readonly WindowsGuiFocus? Focus;

            internal Capture(WindowsControlTarget target, Rectangle client, Size size, string source, string data)
            {
                Target = target; Client = client; Size = size; Source = source; Data = data;
                Foreground = WindowsApplicationControl.IsForeground(target.Handle);
                if (Foreground) Focus = WindowsGuiFocus.Read(target.Handle);
            }
        }

        // One recent MCP image per window, kept only in the worker's memory.
        private readonly LinkedList<Capture> _captures = new LinkedList<Capture>();

        internal string Remember(WindowsControlTarget target, Rectangle bounds, JObject capture, JObject image)
        {
            var old = _captures.First;
            while (old != null && old.Value.Target.Handle != target.Handle) old = old.Next;
            if (old != null) _captures.Remove(old);
            var data = (string)image["data"]!;
            var retained = new Capture(target, WindowsNativeCapture.ClientBounds(target.Handle, bounds),
                new Size(capture.Value<int>("screenshot_width"), capture.Value<int>("screenshot_height")), (string)capture["screenshot_source"]!, data);
            _captures.AddLast(retained);
            while (_captures.Count > 8 || _captures.Count > 1 && _captures.Sum(item => (long)item.Data.Length) > NativeControlConnection.MaximumFrameBytes)
                _captures.RemoveFirst();
            return retained.Id;
        }

        internal string? RequireMatch(WindowsControlTarget target, Rectangle bounds, string captureId, Point point,
            JObject? targetRegion, JArray? regions, WaitHandle? cancellation)
        {
            var previous = _captures.FirstOrDefault(item => item.Id == captureId);
            if (previous == null) throw Unavailable();
            var client = WindowsNativeCapture.ClientBounds(target.Handle, bounds);
            var foreground = WindowsApplicationControl.IsForeground(target.Handle);
            JObject Detail(string reason) => new JObject {
                ["reason"] = reason, ["screenshot_source"] = previous.Source,
                ["foreground_at_observation"] = previous.Foreground, ["foreground_at_verification"] = foreground
            };
            if (previous.Target.Handle != target.Handle || previous.Target.Pid != target.Pid || previous.Target.Created != target.Created || previous.Target.Owner != target.Owner)
                throw Changed(Detail("window_identity"));
            if (!previous.Target.Bounds.Equals(target.Bounds))
                throw Changed(BoundsChanged(previous.Target.Bounds, target.Bounds));
            if (previous.Target.Title != target.Title) throw Changed(Detail("window_title"));
            if (previous.Client != client) {
                var geometry = Detail("client_bounds");
                geometry["previous_bounds"] = RectangleJson(previous.Client);
                geometry["current_bounds"] = RectangleJson(client);
                throw Changed(geometry);
            }

            var imageBounds = new Rectangle(Point.Empty, previous.Size);
            var clickArea = targetRegion == null ? Rectangle.Intersect(imageBounds, new Rectangle(point.X - 32, point.Y - 32, 65, 65)) : Region(targetRegion);
            if (!imageBounds.Contains(clickArea) || !clickArea.Contains(point))
                throw new ControlException("InvalidRegion", "目标区域必须位于原截图内并包含点击坐标。");
            using var png = new MemoryStream(Convert.FromBase64String(previous.Data));
            using var old = new Bitmap(png);
            var checks = new List<(Rectangle Area, string Kind, int? Index)> {
                (clickArea, targetRegion == null ? "click_neighborhood" : "target", null)
            };
            if (regions is { Count: > 0 }) {
                var index = 0;
                foreach (var region in regions.OfType<JObject>()) checks.Add((Region(region), "context", ++index));
            } else if (targetRegion == null) checks.Add((ClientPixels(client, bounds, imageBounds), "client", null));
            if (checks.Any(check => !imageBounds.Contains(check.Area) || check.Area.Width < 1 || check.Area.Height < 1))
                throw new ControlException("InvalidRegion", "复核区域必须位于原截图内。");

            (Rectangle Changed, JObject Detail)? Compare()
            {
                using var fresh = WindowsNativeCapture.Refresh(target, bounds, previous.Size, previous.Source, cancellation);
                if (fresh == null) throw Unavailable();
                ControlCancellation.Check(cancellation);
                foreach (var (area, kind, index) in checks) {
                    var (count, changed) = FindChangedPixels(old, fresh, area, cancellation);
                    if (count == 0) continue;
                    var difference = Detail("pixels");
                    difference["region"] = kind;
                    if (index.HasValue) difference["region_index"] = index.Value;
                    difference["checked_bounds"] = RectangleJson(area);
                    difference["changed_bounds"] = RectangleJson(changed);
                    difference["changed_pixels"] = count;
                    return (changed, difference);
                }
                return null;
            }

            var mismatch = Compare();
            if (!mismatch.HasValue) return null;
            var initial = mismatch.Value;
            if (!foreground || previous.Focus is not WindowsGuiFocus observed || WindowsGuiFocus.Read(target.Handle) is not WindowsGuiFocus live
                || observed.Window != live.Window || observed.CaretWindow != live.CaretWindow) throw Changed(initial.Detail);

            var native = observed.CaretWindow != IntPtr.Zero;
            Rectangle candidate;
            if (native) {
                if (observed.CaretBounds.IsEmpty || observed.CaretBounds != live.CaretBounds) throw Changed(initial.Detail);
                candidate = ToImagePixels(observed.CaretBounds, bounds, imageBounds);
                candidate.Inflate(2, 2); // Resampling can spread a caret into neighboring pixels.
                if (!candidate.Contains(initial.Changed)) throw Changed(initial.Detail);
            } else {
                // A painted caret may have no HWND. Only wait on a narrow change away
                // from the target. The final comparison still checks every pixel.
                candidate = initial.Changed;
                if (candidate.Height < 6 || candidate.Height > 64 || candidate.Width > Math.Max(4, (candidate.Height + 3) / 3)
                    || candidate.IntersectsWith(clickArea)) throw Changed(initial.Detail);
            }

            var clock = Stopwatch.StartNew();
            var budget = WindowsGuiFocus.BlinkWaitMilliseconds();
            bool? sampledVisibility = null;
            while (clock.ElapsedMilliseconds < budget) {
                if (cancellation == null) Thread.Sleep(30);
                else cancellation.WaitOne(30);
                ControlCancellation.Check(cancellation);
                if (!WindowsApplicationControl.IsForeground(target.Handle) || WindowsGuiFocus.Read(target.Handle) is not WindowsGuiFocus current
                    || current.Window != observed.Window || current.CaretWindow != observed.CaretWindow
                    || native && current.CaretBounds != observed.CaretBounds) break;
                // The GUI state and capture are sampled separately. Try both phases
                // when available, while avoiding repeated captures in the same phase.
                if (native && sampledVisibility == current.CaretVisible) continue;
                sampledVisibility = current.CaretVisible;
                mismatch = Compare();
                if (!mismatch.HasValue) {
                    if (WindowsApplicationControl.IsForeground(target.Handle) && WindowsGuiFocus.Read(target.Handle) is WindowsGuiFocus confirmed
                        && confirmed.Window == observed.Window && confirmed.CaretWindow == observed.CaretWindow
                        && (!native || confirmed.CaretBounds == observed.CaretBounds))
                        return native ? "win32" : "painted";
                    break;
                }
                if (!candidate.Contains(mismatch.Value.Changed)) throw Changed(mismatch.Value.Detail);
            }
            throw Changed(initial.Detail);
        }

        internal static Rectangle Region(JObject region) =>
            new Rectangle(region.Value<int>("x"), region.Value<int>("y"), region.Value<int>("width"), region.Value<int>("height"));

        private static JObject RectangleJson(Rectangle area) =>
            new JObject { ["x"] = area.X, ["y"] = area.Y, ["width"] = area.Width, ["height"] = area.Height };

        internal static JObject BoundsChanged(WindowsControlTarget.Rect previous, WindowsControlTarget.Rect current) => new JObject {
            ["reason"] = "window_bounds",
            ["previous_bounds"] = RectangleJson(Rectangle.FromLTRB(previous.Left, previous.Top, previous.Right, previous.Bottom)),
            ["current_bounds"] = RectangleJson(Rectangle.FromLTRB(current.Left, current.Top, current.Right, current.Bottom))
        };

        private static Rectangle ToImagePixels(Rectangle client, Rectangle bounds, Rectangle image)
        {
            var scaleX = image.Width / (double)bounds.Width;
            var scaleY = image.Height / (double)bounds.Height;
            return Rectangle.FromLTRB((int)Math.Ceiling((client.Left - bounds.Left) * scaleX), (int)Math.Ceiling((client.Top - bounds.Top) * scaleY),
                (int)Math.Floor((client.Right - bounds.Left) * scaleX), (int)Math.Floor((client.Bottom - bounds.Top) * scaleY));
        }

        private static Rectangle ClientPixels(Rectangle client, Rectangle bounds, Rectangle image)
        {
            if (client.Width < 1 || client.Height < 1) return image;
            var area = ToImagePixels(client, bounds, image);
            // Resizing may blend the non-client frame into the first client pixels.
            if (area.Width > 4 && area.Height > 4) area.Inflate(-2, -2);
            return area.Width > 0 && area.Height > 0 ? area : image;
        }

        private static (int Count, Rectangle Bounds) FindChangedPixels(Bitmap before, Bitmap after, Rectangle area, WaitHandle? cancellation)
        {
            var first = before.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try {
                var second = after.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try {
                    var left = new byte[area.Width * 3];
                    var right = new byte[left.Length];
                    var count = 0;
                    var minX = area.Width; var minY = area.Height; var maxX = -1; var maxY = -1;
                    for (var row = 0; row < area.Height; row++) {
                        if (row % 64 == 0) ControlCancellation.Check(cancellation);
                        Marshal.Copy(IntPtr.Add(first.Scan0, row * first.Stride), left, 0, left.Length);
                        Marshal.Copy(IntPtr.Add(second.Scan0, row * second.Stride), right, 0, right.Length);
                        for (var x = 0; x < area.Width; x++) {
                            var pixel = x * 3;
                            if (left[pixel] == right[pixel] && left[pixel + 1] == right[pixel + 1] && left[pixel + 2] == right[pixel + 2]) continue;
                            count++;
                            minX = Math.Min(minX, x);
                            maxX = Math.Max(maxX, x);
                            minY = Math.Min(minY, row);
                            maxY = row;
                        }
                    }
                    return (count, count == 0 ? Rectangle.Empty : Rectangle.FromLTRB(area.Left + minX, area.Top + minY, area.Left + maxX + 1, area.Top + maxY + 1));
                } finally { after.UnlockBits(second); }
            } finally { before.UnlockBits(first); }
        }

        private static ControlException Changed(JObject difference) =>
            new ControlException("ViewChanged", "点击前复核发现窗口或目标画面已变化，请在新画面中重新选点。", verification: difference);
        private static ControlException Unavailable() => new ControlException("CaptureVerificationUnavailable", "点击前无法取得可比较的窗口画面，请在新画面中重新选点。");
    }
}
