#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading;
using Forms = System.Windows.Forms;

namespace YTray.Native
{
    internal sealed partial class WindowsActionOverlay
    {
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
            private readonly OverlayWindow _cursor;
            private readonly OverlayWindow _banner = new OverlayWindow(2);
            private readonly List<Edge> _edges = new List<Edge>();
            private readonly Forms.Timer _timer = new Forms.Timer { Interval = 40 };
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private Rectangle[] _screens = Array.Empty<Rectangle>();
            private long _id, _finished;
            private IntPtr _target;
            private string _phase = "prepare", _description = "", _context = "";
            private bool _disposed;
            internal string? ActiveDescription => _timer.Enabled && _finished == 0 ? _description : null;

            internal OperationFeedback(OverlayWindow cursor)
            {
                _cursor = cursor; _timer.Tick += (_, __) => RenderSafely();
            }
            internal void Update(long id, string phase, IntPtr target, string description, string context)
            {
                if (phase == "prepare") {
                    _id = id; _finished = 0; _target = target;
                    _description = SingleLine(description, 110); _context = SingleLine(context, 100);
                    EnsureEdges();
                } else {
                    if (id != _id) return;
                    if (target != IntPtr.Zero) _target = target;
                    if (context.Length > 0) _context = SingleLine(context, 100);
                    if (phase == "completed" || phase == "failed") _finished = _clock.ElapsedMilliseconds;
                }
                _phase = phase; _timer.Interval = 40; _timer.Start(); RenderSafely();
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
                if (_finished != 0 && idle >= 10000) { Stop(); return; }
                if (Volatile.Read(ref _captureSuppression) != 0) return;
                var done = _phase == "completed" || _phase == "failed";
                var color = _phase == "failed" ? TerracottaError : done ? TerracottaDone : Terracotta;
                var fade = _finished == 0 ? 1 : Math.Min(1, (10000 - idle) / 300.0);
                var strength = done ? (idle < 2500 ? .92 : .72) : .88 + .10 * Math.Sin(now / 450.0);
                var opacity = (byte)(255 * fade * strength);
                var interval = done && idle >= 2500 && idle < 9700 ? 160 : 40;
                if (_timer.Interval != interval) _timer.Interval = interval;
                foreach (var edge in _edges) {
                    var edgeColor = _phase == "failed" ? TerracottaError : Terracotta;
                    if (edge.ColorKey != edgeColor.ToArgb()) { PaintEdge(edge, edgeColor); edge.ColorKey = edgeColor.ToArgb(); }
                    if (!edge.Window.Present(edge.Bounds, opacity)) throw new InvalidOperationException("Cannot present screen edge feedback");
                }
                PaintBanner(color, done, idle, (float)fade);
                _cursor.RaiseCursor();
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
            private void PaintBanner(Color color, bool done, long idle, float fade)
            {
                var screen = _target == IntPtr.Zero ? Forms.Screen.FromHandle(new IntPtr(WindowsApplicationControl.ForegroundWindow)) : Forms.Screen.FromHandle(_target);
                var scale = Math.Max(1, Math.Min(2.5f, _target == IntPtr.Zero ? 1 : GetDpiForWindow(_target) / 96f));
                var width = Math.Min((int)(640 * scale), Math.Max(240, screen.Bounds.Width - 40));
                var height = (int)(124 * scale);
                var bounds = new Rectangle(screen.Bounds.Left + (screen.Bounds.Width - width) / 2, screen.Bounds.Top + Math.Max(16, screen.Bounds.Height / 24), width, height);
                var graphics = _banner.Canvas(width, height); graphics.ScaleTransform(scale, scale);
                var card = new RectangleF(8, 8, width / scale - 16, 108);
                using (var path = OverlayWindow.Rounded(card, 16)) {
                    using var halo = new Pen(Color.FromArgb(25, color), 12); graphics.DrawPath(halo, path);
                    using var fill = new SolidBrush(Color.FromArgb(245, 37, 30, 28)); graphics.FillPath(fill, path);
                    using var outline = new Pen(Color.FromArgb(155, color), 1); graphics.DrawPath(outline, path);
                }
                using var headerFont = new Font("Microsoft YaHei UI", 10, FontStyle.Regular, GraphicsUnit.Point);
                using var actionFont = new Font("Microsoft YaHei UI", 13, FontStyle.Bold, GraphicsUnit.Point);
                using var contextFont = new Font("Microsoft YaHei UI", 9, FontStyle.Regular, GraphicsUnit.Point);
                using var white = new SolidBrush(Color.FromArgb(255, 247, 241));
                using var secondary = new SolidBrush(Color.FromArgb(210, 190, 180));
                using var accent = new SolidBrush(color);
                using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap, LineAlignment = StringAlignment.Center };
                using var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                using (var icon = new SolidBrush(Color.FromArgb(36, color))) graphics.FillEllipse(icon, 23, 20, 29, 29);
                graphics.DrawString("AI", headerFont, accent, new RectangleF(23, 20, 29, 29), centered);
                var header = _phase == "failed" ? "操作未完成" : done ? "刚刚完成的操作" : "正在进行的操作";
                graphics.DrawString(header, headerFont, secondary, new RectangleF(63, 19, card.Width - 160, 30), format);
                var badge = new RectangleF(card.Right - 92, 21, 75, 25);
                using (var badgePath = OverlayWindow.Rounded(badge, 8)) { using var badgeFill = new SolidBrush(Color.FromArgb(30, color)); graphics.FillPath(badgeFill, badgePath); }
                var state = _phase == "prepare" ? "即将执行" : _phase == "running" ? "执行中" : _phase == "failed" ? "未完成" : "已执行";
                graphics.DrawString(state, contextFont, accent, badge, centered);
                graphics.DrawString(_description, actionFont, white, new RectangleF(25, 51, card.Width - 34, 29), format);
                graphics.DrawString(_context, contextFont, secondary, new RectangleF(25, 82, card.Width - 34, 19), format);
                var line = new RectangleF(card.Left + 15, card.Bottom - 4, card.Width - 30, 2);
                if (done) graphics.FillRectangle(accent, line);
                else {
                    var sweep = (float)((Math.Sin(_clock.ElapsedMilliseconds / 450.0) + 1) / 2);
                    graphics.FillRectangle(accent, line.X + (line.Width - 100) * sweep, line.Y, 100, 2);
                }
                var bannerOpacity = (byte)(255 * fade * (done && idle >= 3000 ? .82 : 1));
                if (!_banner.Present(bounds, bannerOpacity)) throw new InvalidOperationException("Cannot present operation feedback banner");
            }
            private static string SingleLine(string value, int maximum)
            {
                var text = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
                return text.Length > maximum ? text.Substring(0, maximum - 1) + "…" : text;
            }
            private void Stop()
            {
                _timer.Stop(); _banner.Stop(); foreach (var edge in _edges) edge.Window.Stop();
            }
            public void Dispose()
            {
                if (_disposed) return; _disposed = true;
                _timer.Stop(); _timer.Dispose(); _banner.Dispose(); foreach (var edge in _edges) edge.Dispose(); _edges.Clear();
            }
        }
    }
}
