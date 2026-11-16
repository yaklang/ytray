#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Newtonsoft.Json.Linq;
using Forms = System.Windows.Forms;

namespace YTray.Native
{
    internal sealed partial class WindowsActionOverlay
    {
        private const int VisibleCalls = 4;
        private const int CallWidth = 126, ConnectorWidth = 22, NavigationWidth = 30;

        internal static (Rectangle Bounds, float Scale) BannerLayout(Rectangle workArea, float scale, int calls = 1)
        {
            const int logicalHeight = 92;
            var visible = Math.Max(1, Math.Min(VisibleCalls, calls));
            var logicalWidth = Math.Max(320, 44 + visible * CallWidth + (visible - 1) * ConnectorWidth
                + (calls > VisibleCalls ? 2 * NavigationWidth : 0));
            var margin = Math.Min(16, Math.Min(workArea.Width, workArea.Height) / 4);
            scale = Math.Min(Math.Max(1, Math.Min(2.5f, scale)),
                Math.Min((workArea.Width - 2 * margin) / (float)logicalWidth, (workArea.Height - 2 * margin) / (float)logicalHeight));
            var width = Math.Max(1, (int)(logicalWidth * scale));
            var height = Math.Max(1, (int)(logicalHeight * scale));
            var top = workArea.Top + Math.Min(Math.Max(margin, (int)(14 * scale)), workArea.Height - height - margin);
            return (new Rectangle(workArea.Left + (workArea.Width - width) / 2, top, width, height), scale);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            internal int Size;
            internal WindowsControlTarget.Rect Bounds, WorkArea;
            internal uint Flags;
        }
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("shcore.dll")] private static extern int GetScaleFactorForMonitor(IntPtr monitor, out int scale);

        // Thin edge surfaces avoid a desktop-sized bitmap. All surfaces share
        // the cursor's STA loop, capture fence and kill-on-close worker lifetime.
        private sealed class OperationFeedback : IDisposable
        {
            private sealed class Edge : IDisposable
            {
                internal readonly OverlayWindow Window = new OverlayWindow(3);
                internal Rectangle Bounds;
                internal int Direction;
                internal int ColorKey;
                public void Dispose() => Window.Dispose();
            }
            private sealed class ButtonSurface : IDisposable
            {
                internal readonly int Kind;
                internal readonly OverlayWindow Window;
                internal Rectangle Bounds;
                internal Size PixelSize;
                internal ButtonSurface(int kind) { Kind = kind; Window = new OverlayWindow(kind); }
                public void Dispose() => Window.Dispose();
            }

            private readonly OverlayWindow _cursor;
            private readonly OverlayWindow _banner = new OverlayWindow(2);
            private readonly List<Edge> _edges = new List<Edge>();
            private readonly List<ButtonSurface> _buttons = new List<ButtonSurface>();
            private readonly List<(long Id, long Position, string Label, string Status, string? TraceId)> _calls =
                new List<(long, long, string, string, string?)>();
            private readonly Forms.Timer _timer = new Forms.Timer { Interval = 160 };
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private Rectangle[] _screens = Array.Empty<Rectangle>();
            private long _id, _finished, _checkpoint, _position;
            private int? _firstCall;
            private IntPtr _target;
            private string _phase = "prepare", _description = "", _context = "";
            private bool _dirty = true;
            private Rectangle _bannerBounds;
            private bool _disposed, _preview, _showEdges;
            internal string? ActiveDescription => _timer.Enabled && _finished == 0 ? _description : null;

            internal OperationFeedback(OverlayWindow cursor)
            {
                _cursor = cursor; _timer.Tick += (_, __) => RenderSafely();
            }
            internal void Update(long id, string phase, IntPtr target, string description, string context, JObject? task,
                bool showEdges, string? traceId, string? label)
            {
                if (id < _id || id == _id && _finished != 0) return;
                var newOperation = id != _id;
                if (newOperation) {
                    _id = id; _finished = 0; _target = target; _description = _context = "";
                    _firstCall = null;
                }
                var preview = task?.Value<bool?>("preview") == true;
                var caption = context.Length == 0 ? _context : SingleLine(context, 100);
                var position = task?.Value<long>("call_count") ?? 1;
                var checkpoint = (long?)task?["checkpoint"]?["id"] ?? 0;
                _dirty |= newOperation || _phase != phase || _preview != preview || _context != caption
                    || _position != position || _checkpoint != checkpoint;
                _preview = preview;
                _context = caption;
                if (newOperation) {
                    if (showEdges) EnsureEdges();
                    else foreach (var edge in _edges) edge.Window.Stop();
                }
                _showEdges = showEdges;
                if (target != IntPtr.Zero) _target = target;
                if (description.Length != 0) _description = SingleLine(description, 110);
                _calls.Clear();
                if (task?["calls"] is JArray calls) {
                    foreach (var call in calls.OfType<JObject>())
                        _calls.Add((call.Value<long>("id"), call.Value<long>("position"),
                            SingleLine((string?)call["label"] ?? "调用", 48), (string?)call["phase"] ?? "running", (string?)call["trace_id"]));
                }
                if (_calls.Count == 0) _calls.Add((id, 1, string.IsNullOrEmpty(label) ? _description : SingleLine(label!, 48), phase, traceId));
                _position = position;
                _checkpoint = checkpoint;
                _phase = phase;
                if (phase == "completed" || phase == "failed") _finished = Math.Max(1, _clock.ElapsedMilliseconds);
                if (_dirty || !_timer.Enabled) {
                    _timer.Start();
                    RenderSafely();
                }
            }
            internal void SetTarget(long id, IntPtr target)
            {
                if (id == _id && _finished == 0 && target != IntPtr.Zero && target != _target) {
                    _target = target;
                    RenderSafely();
                }
            }
            private void EnsureEdges()
            {
                var screens = Forms.Screen.AllScreens.Select(screen => screen.Bounds).ToArray();
                if (_screens.SequenceEqual(screens) && _edges.Count > 0) return;
                foreach (var edge in _edges) edge.Dispose(); _edges.Clear(); _screens = screens;
                foreach (var screen in screens) {
                    var thickness = Math.Min(100, Math.Min(screen.Width, screen.Height) / 10);
                    _edges.Add(new Edge { Bounds = new Rectangle(screen.Left, screen.Top, thickness, screen.Height), Direction = 0 });
                    _edges.Add(new Edge { Bounds = new Rectangle(screen.Right - thickness, screen.Top, thickness, screen.Height), Direction = 1 });
                    _edges.Add(new Edge { Bounds = new Rectangle(screen.Left, screen.Top, screen.Width, thickness), Direction = 2 });
                    _edges.Add(new Edge { Bounds = new Rectangle(screen.Left, screen.Bottom - thickness, screen.Width, thickness), Direction = 3 });
                }
            }
            internal void RenderSafely()
            {
                if (_disposed || !_timer.Enabled) return;
                try { Render(); }
                catch (Exception error) { Console.Error.WriteLine("YTray operation feedback: " + error.Message); Stop(); }
            }
            private void Render()
            {
                var now = _clock.ElapsedMilliseconds; var idle = _finished == 0 ? 0 : now - _finished;
                var failed = _phase == "failed";
                var paused = _checkpoint != 0;
                var hold = failed || paused ? 25000 : 10000;
                if (!_preview && _finished != 0 && idle >= hold) { Stop(); return; }
                if (Volatile.Read(ref _captureSuppression) != 0) return;
                var done = _phase == "completed" || _phase == "failed";
                var color = failed || paused ? Color.FromArgb(255, 158, 144) : done ? Color.FromArgb(152, 220, 184) : Color.FromArgb(247, 186, 142);
                var fade = _preview || _finished == 0 ? 1 : Math.Min(1, (hold - idle) / 300.0);
                var fading = !_preview && done && (idle >= hold - 300 || _showEdges && idle >= 9700 && idle < 10000);
                var interval = fading ? 30 : 160;
                if (_timer.Interval != interval) _timer.Interval = interval;
                var layerAppeared = false;
                if (_showEdges) foreach (var edge in _edges) {
                    if (idle >= 10000) { edge.Window.Stop(); continue; }
                    var edgeColor = failed ? TerracottaError : Terracotta;
                    if (edge.ColorKey != edgeColor.ToArgb()) { PaintEdge(edge, edgeColor); edge.ColorKey = edgeColor.ToArgb(); }
                    var edgeOpacity = (byte)(214 * Math.Min(fade, (10000 - idle) / 300.0));
                    layerAppeared |= !edge.Window.IsShown;
                    if (!edge.Window.Present(edge.Bounds, edgeOpacity)) throw new InvalidOperationException("Cannot present screen edge feedback");
                }
                layerAppeared |= PaintBanner(color, (byte)(255 * fade));
                if (layerAppeared) _cursor.RaiseCursor();
            }
            private static void PaintEdge(Edge edge, Color color)
            {
                var graphics = edge.Window.Canvas(edge.Bounds.Width, edge.Bounds.Height);
                var width = edge.Bounds.Width; var height = edge.Bounds.Height;
                var horizontal = edge.Direction < 2;
                var length = horizontal ? width : height;
                var reverse = edge.Direction == 1 || edge.Direction == 3;
                var start = horizontal ? new PointF(reverse ? length : 0, 0) : new PointF(0, reverse ? length : 0);
                var end = horizontal ? new PointF(reverse ? 0 : length, 0) : new PointF(0, reverse ? 0 : length);
                using var gradient = new LinearGradientBrush(start, end, color, Color.Transparent) {
                    InterpolationColors = new ColorBlend { Positions = new[] { 0f, .04f, .25f, 1f }, Colors = new[] { Color.FromArgb(235, color), Color.FromArgb(210, color), Color.FromArgb(120, color), Color.FromArgb(0, color) } }
                };
                graphics.FillRectangle(gradient, 0, 0, width, height);
            }
            private bool PaintBanner(Color color, byte opacity)
            {
                var window = IsWindowVisible(_target) ? _target : new IntPtr(WindowsApplicationControl.ForegroundWindow);
                var monitor = MonitorFromWindow(window, 2); // MONITOR_DEFAULTTONEAREST
                var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
                var work = GetMonitorInfo(monitor, ref info)
                    ? Rectangle.FromLTRB(info.WorkArea.Left, info.WorkArea.Top, info.WorkArea.Right, info.WorkArea.Bottom)
                    : Forms.Screen.PrimaryScreen!.WorkingArea;
                var dpiScale = GetScaleFactorForMonitor(monitor, out var percent) == 0 ? percent / 100f : GetDpiForWindow(window) / 96f;
                var (bounds, scale) = BannerLayout(work, dpiScale, _calls.Count);
                if (_dirty || bounds != _bannerBounds) {
                    PaintPipeline(bounds, scale, color);
                    _bannerBounds = bounds;
                    _dirty = false;
                }
                var layerAppeared = !_banner.IsShown;
                if (!_banner.Present(bounds, opacity)) throw new InvalidOperationException("Cannot present operation feedback banner");
                if (_cursor.IsCurrentOperation?.Invoke(_id) != false) {
                    foreach (var button in _buttons) {
                        if (button.Bounds.IsEmpty) continue;
                        layerAppeared |= !button.Window.IsShown;
                        button.Window.Present(button.Bounds);
                    }
                }
                return layerAppeared;
            }

            private void PaintPipeline(Rectangle bounds, float scale, Color color)
            {
                var graphics = _banner.Canvas(bounds.Width, bounds.Height);
                graphics.ScaleTransform(scale, scale);
                var card = new RectangleF(7, 6, bounds.Width / scale - 14, bounds.Height / scale - 13);
                using (var path = OverlayWindow.Rounded(card, 16)) {
                    using var shadow = new Pen(Color.FromArgb(38, 8, 10, 16), 8);
                    graphics.DrawPath(shadow, path);
                    using var fill = new LinearGradientBrush(card, Color.FromArgb(223, 33, 43, 56), Color.FromArgb(198, 17, 23, 34), 90f);
                    graphics.FillPath(fill, path);
                    using var outline = new Pen(Color.FromArgb(92, 189, 205, 224), 1);
                    graphics.DrawPath(outline, path);
                }
                using var title = new Font("Microsoft YaHei UI", 12.5f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var detail = new Font("Microsoft YaHei UI", 12, FontStyle.Regular, GraphicsUnit.Pixel);
                using var accent = new SolidBrush(color);
                using var caption = new SolidBrush(Color.FromArgb(231, 236, 245));
                using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap, LineAlignment = StringAlignment.Center };
                using var rightAligned = new StringFormat(format) { Alignment = StringAlignment.Far };
                var header = _preview ? "卡片预览" : "电脑控制";
                if (_context.Length != 0) header += "   ·   " + _context;
                using (var tint = new SolidBrush(Color.FromArgb(37, color))) graphics.FillEllipse(tint, 19, 16, 14, 14);
                graphics.FillEllipse(accent, 23, 20, 6, 6);
                graphics.DrawString(header, title, caption, new RectangleF(38, 13, card.Width - 155, 20), format);
                var checkpointPosition = _calls.FirstOrDefault(call => call.Id == _checkpoint).Position;
                var status = _checkpoint != 0 ? "第 " + (checkpointPosition > 0 ? checkpointPosition : _position) + " 步暂停"
                    : _phase == "failed" ? "失败" : _phase == "completed" ? (_position == 1 ? "已完成" : _position + " 步 · 已完成")
                    : "第 " + _position + " 步执行中";
                graphics.DrawString(status, detail, accent, new RectangleF(card.Right - 126, 13, 110, 20), rightAligned);

                foreach (var button in _buttons) button.Bounds = Rectangle.Empty;
                PaintCalls(graphics, bounds, card, scale, detail, format);
                foreach (var button in _buttons)
                    if (button.Bounds.IsEmpty) button.Window.Stop();
            }

            private void PaintCalls(Graphics graphics, Rectangle bounds, RectangleF card, float scale, Font font, StringFormat format)
            {
                var lastStart = Math.Max(0, _calls.Count - VisibleCalls);
                var focused = _phase == "failed" ? _id : _checkpoint;
                var focusIndex = _calls.FindIndex(call => call.Id == focused);
                var start = _firstCall ?? (_preview ? 0 : focusIndex < 0 ? lastStart : Math.Min(lastStart, focusIndex));
                start = Math.Min(lastStart, start);
                const float top = 40, height = 33;
                var left = card.Left + 14;
                var right = card.Right - 14;
                var navigation = lastStart > 0 ? NavigationWidth : 0;
                var count = Math.Min(VisibleCalls, _calls.Count);
                var width = (right - left - 2 * navigation - (count - 1) * ConnectorWidth) / count;
                if (navigation != 0) {
                    PaintArrow(graphics, bounds, scale, new RectangleF(left, top, 23, height), -1, start, lastStart);
                    PaintArrow(graphics, bounds, scale, new RectangleF(right - 23, top, 23, height), 1, start, lastStart);
                }
                for (var i = 0; i < count; i++) {
                    var call = _calls[start + i];
                    var rect = new RectangleF(left + navigation + i * (width + ConnectorWidth), top, width, height);
                    if (i > 0) PaintConnector(graphics, rect.Left - ConnectorWidth + 4, rect.Left - 4, top + height / 2);
                    PaintOperationNode(graphics, bounds, scale, rect, call.Position, call.Label, call.Status, font, format, call.TraceId, call.Id == _id);
                }
            }

            private static void PaintConnector(Graphics graphics, float left, float right, float y)
            {
                using var pen = new Pen(Color.FromArgb(181, 194, 210, 230), 1.25f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                graphics.DrawLine(pen, left, y, right, y);
                graphics.DrawLines(pen, new[] { new PointF(right - 4, y - 3.5f), new PointF(right, y), new PointF(right - 4, y + 3.5f) });
            }

            private void PaintArrow(Graphics graphics, Rectangle bounds, float scale, RectangleF rect, int direction, int start, int lastStart)
            {
                var next = start + direction;
                var enabled = _finished != 0 && next >= 0 && next <= lastStart;
                DrawArrow(graphics, rect, direction, enabled);
                if (!enabled) return;
                PlaceButton(6, bounds, scale, rect, direction < 0 ? "较早的步骤" : "较新的步骤", () => {
                    _firstCall = next;
                    if (_finished != 0) _finished = Math.Max(1, _clock.ElapsedMilliseconds);
                    _dirty = true;
                    RenderSafely();
                });
            }

            private static void DrawArrow(Graphics graphics, RectangleF rect, int direction, bool enabled)
            {
                if (enabled) {
                    using var path = OverlayWindow.Rounded(RectangleF.Inflate(rect, -.6f, -.6f), 6);
                    using var fill = new SolidBrush(Color.FromArgb(132, 70, 84, 105));
                    graphics.FillPath(fill, path);
                }
                using var pen = new Pen(enabled ? Color.FromArgb(230, 233, 241) : Color.FromArgb(104, 110, 124), 1.7f)
                    { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                var x = rect.Left + rect.Width / 2; var y = rect.Top + rect.Height / 2;
                graphics.DrawLines(pen, new[] { new PointF(x - 2 * direction, y - 4), new PointF(x + 2 * direction, y), new PointF(x - 2 * direction, y + 4) });
            }

            private void PaintOperationNode(Graphics graphics, Rectangle bounds, float scale, RectangleF rect, long position,
                string label, string status, Font font, StringFormat format, string? traceId, bool selected)
            {
                PaintStep(graphics, rect, position, label, status, font, format, selected);
                var show = _cursor.ShowTrace;
                if (_finished != 0 && show != null && traceId != null) {
                    PlaceButton(7, bounds, scale, rect, "第 " + position + " 步，" + label + "，查看 Trace",
                        () => ThreadPool.QueueUserWorkItem(_ => show(traceId)));
                }
            }

            private void PlaceButton(int kind, Rectangle bounds, float scale, RectangleF rect, string label, Action onClick)
            {
                var button = _buttons.FirstOrDefault(item => item.Kind == kind && item.Bounds.IsEmpty);
                if (button == null) { button = new ButtonSurface(kind); _buttons.Add(button); }
                button.Bounds = Rectangle.FromLTRB(bounds.Left + (int)(rect.Left * scale), bounds.Top + (int)(rect.Top * scale),
                    bounds.Left + (int)Math.Ceiling(rect.Right * scale), bounds.Top + (int)Math.Ceiling(rect.Bottom * scale));
                button.Window.AccessibleName = label;
                button.Window.OnDetailsClick = onClick;
                // The card owns all visible pixels. Withdrawing these hit areas
                // for the next action must leave the displayed steps intact.
                if (button.PixelSize != button.Bounds.Size) {
                    button.Window.Canvas(button.Bounds.Width, button.Bounds.Height).Clear(Color.FromArgb(1, 0, 0, 0));
                    button.PixelSize = button.Bounds.Size;
                }
            }

            private static void PaintStep(Graphics graphics, RectangleF rect, long position, string label, string status, Font font, StringFormat format, bool selected)
            {
                var done = status == "completed";
                var failed = status == "failed";
                var ink = failed ? Color.FromArgb(255, 177, 166) : done ? Color.FromArgb(171, 225, 205) : Color.FromArgb(255, 200, 156);
                var fill = failed ? Color.FromArgb(170, 98, 48, 53) : done ? Color.FromArgb(136, 50, 76, 81) : Color.FromArgb(150, 96, 68, 54);
                using (var path = OverlayWindow.Rounded(RectangleF.Inflate(rect, -.7f, -.7f), 9)) {
                    using var surface = new SolidBrush(fill);
                    using var border = new Pen(Color.FromArgb(failed || selected ? 175 : 70, ink));
                    graphics.FillPath(surface, path); graphics.DrawPath(border, path);
                }
                var number = position.ToString();
                const float gap = 5;
                var badgeWidth = Math.Max(19, Math.Min(37, number.Length * 6 + 9));
                var measuredLabel = graphics.MeasureString(label, font, new SizeF(800, rect.Height), format).Width;
                var labelWidth = Math.Min(rect.Width - badgeWidth - gap - 12, (float)Math.Ceiling(measuredLabel) + 1);
                var contentWidth = badgeWidth + gap + labelWidth;
                var badge = new RectangleF(rect.Left + (rect.Width - contentWidth) / 2, rect.Top + (rect.Height - 19) / 2, badgeWidth, 19);
                using (var path = OverlayWindow.Rounded(badge, 7)) {
                    using var tint = new SolidBrush(Color.FromArgb(48, ink));
                    graphics.FillPath(tint, path);
                }
                using var indexBrush = new SolidBrush(ink);
                using var textBrush = new SolidBrush(Color.FromArgb(246, 248, 253));
                using var centered = new StringFormat(format) { Alignment = StringAlignment.Center };
                graphics.DrawString(number, font, indexBrush, badge, centered);
                graphics.DrawString(label, font, textBrush, new RectangleF(badge.Right + gap, rect.Top, labelWidth, rect.Height), format);
            }

            private static string SingleLine(string value, int maximum)
            {
                var text = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
                return text.Length > maximum ? text.Substring(0, maximum - 1) + "…" : text;
            }
            internal void Stop()
            {
                _timer.Stop(); _banner.Stop();
                foreach (var button in _buttons) button.Window.Stop();
                foreach (var edge in _edges) edge.Window.Stop();
            }
            public void Dispose()
            {
                if (_disposed) return; _disposed = true;
                _timer.Stop(); _timer.Dispose(); _banner.Dispose();
                foreach (var button in _buttons) button.Dispose(); _buttons.Clear();
                foreach (var edge in _edges) edge.Dispose(); _edges.Clear();
            }
        }
    }
}
