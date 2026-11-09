#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Forms = System.Windows.Forms;

namespace YTray.Native
{
    // A visual observer of input, never an input target. One small, reusable DIB
    // and one STA message loop per worker; the render timer stops while idle.
    internal sealed partial class WindowsActionOverlay : IDisposable
    {
        private const string WindowMarker = "YTray.ActionOverlay";
        private const string KindMarker = "YTray.FeedbackKind";
        private static readonly Color Terracotta = Color.FromArgb(201, 107, 80);
        private static readonly Color TerracottaDone = Color.FromArgb(205, 139, 105);
        private static readonly Color TerracottaError = Color.FromArgb(166, 65, 51);
        private static readonly ConcurrentDictionary<IntPtr, byte> Surfaces = new ConcurrentDictionary<IntPtr, byte>();
        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim();
        private OverlayWindow? _window;
        private Thread? _thread;
        private volatile bool _disposed;
        private long _sequence;
        private static int _captureSuppression;

        private bool EnsureStarted()
        {
            if (_disposed) return false;
            if (_thread == null) {
                _thread = new Thread(Run) { IsBackground = true, Name = "YTray action indicator" };
                _thread.SetApartmentState(ApartmentState.STA); _thread.Start();
            }
            return _ready.Wait(150) && _window != null;
        }

        // Private worker feedback, one lifecycle per public MCP operation. The
        // cursor's internal key/text steps do not restart the operation banner.
        internal bool Operation(long id, string phase, IntPtr target, string description, string context)
        {
            try {
                if (!EnsureStarted()) return false;
                var painted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Post(window => { try { window.Operation(id, phase, target, description, context); painted.TrySetResult(true); } catch { painted.TrySetResult(false); throw; } });
                return painted.Task.Wait(400) && painted.Task.Result;
            } catch { return false; }
        }

        internal Feedback Begin(IntPtr target, Point point, string label)
        {
            try {
                // A broken or overloaded renderer must never block computer use.
                if (!EnsureStarted()) return new Feedback(null, 0);
                var sequence = Interlocked.Increment(ref _sequence);
                Move(sequence, target, point, label);
                return new Feedback(this, sequence);
            } catch { return new Feedback(null, 0); }
        }
        private void Move(long sequence, IntPtr target, Point point, string label)
        {
            var arrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(window => window.Begin(sequence, target, point, label, arrived));
            arrived.Task.Wait(1250);
        }
        private void Run()
        {
            try {
                using var window = new OverlayWindow();
                _ = window.Handle;
                _window = window; _ready.Set();
                if (_disposed) return;
                Forms.Application.Run();
            } catch { _ready.Set(); }
            finally { _window = null; }
        }
        private void Post(Action<OverlayWindow> action)
        {
            try { var window = _window; if (window != null && !window.IsDisposed) window.BeginInvoke(new Action(() => { try { action(window); } catch (Exception error) { Console.Error.WriteLine("YTray action indicator: " + error.Message); window.Stop(); } })); }
            catch { /* Feedback failure does not change the input result. */ }
        }
        public void Dispose()
        {
            _disposed = true;
            Post(window => { window.Close(); Forms.Application.ExitThread(); });
            if (_thread != null && _thread != Thread.CurrentThread) _thread.Join(200);
        }
        internal sealed class Feedback : IDisposable
        {
            private readonly WindowsActionOverlay? _owner;
            private readonly long _sequence;
            private bool _completed;
            internal Feedback(WindowsActionOverlay? owner, long sequence) { _owner = owner; _sequence = sequence; }
            internal void Complete() { _completed = true; }
            internal void Retarget(IntPtr target, Point point, string label) => _owner?.Move(_sequence, target, point, label);
            public void Dispose() => _owner?.Post(window => window.Finish(_sequence, _completed));
        }

        internal static bool IsOverlayWindow(IntPtr window) => GetProp(window, WindowMarker) == new IntPtr(1);
        internal static int WindowKind(IntPtr window) => GetProp(window, KindMarker).ToInt32();
        // Keep normal capture (including UU/RDP/screen sharing) intact. Hide
        // only during our own screen-copy fallback, then restore every surface.
        internal static IDisposable HideForCapture()
        {
            var windows = Surfaces.Keys.ToArray();
            if (windows.Length == 0) return new CaptureVisibility(null);
            Interlocked.Increment(ref _captureSuppression);
            foreach (var window in windows) ShowWindow(window, 0);
            DwmFlush();
            return new CaptureVisibility(windows);
        }
        private sealed class CaptureVisibility : IDisposable
        {
            private IntPtr[]? _windows;
            internal CaptureVisibility(IntPtr[]? windows) { _windows = windows; }
            public void Dispose() {
                var windows = Interlocked.Exchange(ref _windows, null);
                if (windows != null && Interlocked.Decrement(ref _captureSuppression) == 0)
                    foreach (var window in Surfaces.Keys.Where(window => WindowKind(window) == 1)) PostMessage(window, 0x8001, IntPtr.Zero, IntPtr.Zero);
            }
        }

        private sealed class OverlayWindow : Forms.Form
        {
            private const int HoldMilliseconds = 10000;
            private const int FadeMilliseconds = 300;
            private readonly Forms.Timer _timer = new Forms.Timer { Interval = 16 };
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private Bitmap? _bitmap;
            private Graphics? _graphics;
            private IntPtr _dc, _dib, _original;
            private long _sequence, _started, _finished;
            private IntPtr _target;
            private PointF _position, _from, _to;
            private bool _placed, _successful;
            private int _motion;
            private long _trailUntil;
            private readonly List<PointF> _trailHistory = new List<PointF>(12);
            private WindowsCursorMotion? _path;
            private string _label = "";
            private Font? _labelFont;
            private TaskCompletionSource<bool>? _arrived;
            private readonly int _kind;
            private IntPtr _registeredHandle;
            private OperationFeedback? _activity;

            internal OverlayWindow(int kind = 1)
            {
                _kind = kind;
                Text = ""; ShowInTaskbar = false; TopMost = true; FormBorderStyle = Forms.FormBorderStyle.None;
                AutoScaleMode = Forms.AutoScaleMode.None;
                _timer.Tick += (_, __) => RenderSafely();
            }
            protected override bool ShowWithoutActivation => true;
            protected override Forms.CreateParams CreateParams {
                get { var value = base.CreateParams; value.ExStyle |= 0x080800a8; return value; } // NOACTIVATE | LAYERED | TOOLWINDOW | TRANSPARENT | TOPMOST
            }
            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e); SetProp(Handle, WindowMarker, new IntPtr(1));
                SetProp(Handle, KindMarker, new IntPtr(_kind)); _registeredHandle = Handle; Surfaces[Handle] = 0;
                // System-wide capture exclusion would hide feedback from remote
                // viewers too. Own screenshots use the short capture fence above.
            }
            protected override void OnHandleDestroyed(EventArgs e) { Surfaces.TryRemove(_registeredHandle, out _); _registeredHandle = IntPtr.Zero; base.OnHandleDestroyed(e); }
            protected override void WndProc(ref Forms.Message message)
            {
                if (message.Msg == 0x84) { message.Result = new IntPtr(-1); return; } // HTTRANSPARENT
                if (message.Msg == 0x21) { message.Result = new IntPtr(3); return; } // MA_NOACTIVATE
                if (message.Msg == 0x8001) { _activity?.RenderSafely(); if (_timer.Enabled) RenderSafely(); return; }
                base.WndProc(ref message);
            }
            internal void Operation(long id, string phase, IntPtr target, string description, string context)
            {
                if (phase != "prepare" && _activity == null) return;
                _activity ??= new OperationFeedback(this);
                _activity.Update(id, phase, target, description, context);
            }
            internal void Begin(long sequence, IntPtr target, Point point, string label, TaskCompletionSource<bool> arrived)
            {
                _arrived?.TrySetResult(false); _arrived = arrived;
                _sequence = sequence; _target = target; _label = _activity?.ActiveDescription ?? label; AccessibleDescription = "AI · " + _label;
                _started = _clock.ElapsedMilliseconds; _finished = 0; _successful = false;
                _from = _placed ? _position : GetCursorPos(out var cursor) ? new PointF(cursor.X, cursor.Y) : new PointF(point.X, point.Y);
                _to = point;
                var scale = Math.Max(1, GetDpiForWindow(target) / 96f);
                _path = new WindowsCursorMotion(_from, _to, Forms.SystemInformation.VirtualScreen, scale);
                _motion = _path.DurationMilliseconds;
                if (_motion > 0) { _trailUntil = _started + _motion + 220; _trailHistory.Clear(); _trailHistory.Add(_from); }
                _placed = true; _timer.Interval = 16; _timer.Start(); Render();
            }
            internal void Finish(long sequence, bool success)
            {
                if (sequence != _sequence) return;
                _position = _to; _motion = 0; _arrived?.TrySetResult(true); _arrived = null;
                _finished = _clock.ElapsedMilliseconds; _successful = success;
                if (!success) AccessibleDescription = "操作未完成";
                _timer.Interval = 16;
                Render();
            }
            internal void Stop()
            {
                _timer.Stop(); _arrived?.TrySetResult(false); _arrived = null;
                ShowWindow(Handle, 0);
            }
            private void RenderSafely() { try { Render(); } catch (Exception error) { Console.Error.WriteLine("YTray action indicator: " + error.Message); Stop(); } }
            private void Render()
            {
                var now = _clock.ElapsedMilliseconds;
                var idle = _finished == 0 ? 0 : now - _finished;
                if (_target != IntPtr.Zero && (!IsWindowVisible(_target) || IsIconic(_target)) || (_finished != 0 && idle >= HoldMilliseconds)) { Stop(); return; }
                if (Volatile.Read(ref _captureSuppression) != 0) return;
                var fraction = _motion == 0 ? 1 : Math.Min(1, (now - _started) / (double)_motion);
                // Motion and pulses remain smooth; the long, static dwell does
                // not need animation-rate redraws. A new action resumes at 60Hz.
                var interval = fraction < 1 || now < _trailUntil || _finished != 0 && (idle < 650 || idle >= HoldMilliseconds - FadeMilliseconds) ? 16 : 160;
                if (_timer.Interval != interval) _timer.Interval = interval;
                var previous = _position;
                _position = _path?.Sample(fraction) ?? _to;
                var moved = Math.Sqrt(Math.Pow(_position.X - previous.X, 2) + Math.Pow(_position.Y - previous.Y, 2));
                if (_motion > 0 && fraction > 0 && moved > .1) { _trailHistory.Add(_position); if (_trailHistory.Count > 12) _trailHistory.RemoveAt(0); }
                var scale = Math.Max(1, Math.Min(2.5f, GetDpiForWindow(_target) / 96f));
                var monitor = Forms.Screen.FromPoint(Point.Round(_position)).WorkingArea;
                var labelWidth = Math.Min(250f, Math.Max(100, monitor.Width / scale - 40));
                var flipX = _position.X + (44 + labelWidth) * scale > monitor.Right - 8;
                var flipY = _position.Y + 52 * scale > monitor.Bottom - 8;
                var width = (int)Math.Ceiling(480 * scale); var height = (int)Math.Ceiling(224 * scale);
                EnsureSurface(width, height);
                var tipX = flipX ? 384f : 96f; const float tipY = 96;
                var left = (int)Math.Round(_position.X - tipX * scale); var top = (int)Math.Round(_position.Y - tipY * scale);
                var graphics = _graphics!;
                graphics.ResetTransform(); graphics.CompositingMode = CompositingMode.SourceCopy; graphics.Clear(Color.Transparent);
                graphics.CompositingMode = CompositingMode.SourceOver; graphics.ScaleTransform(scale, scale);
                var alpha = _finished == 0 ? 1f : Math.Min(1, Math.Max(0, (HoldMilliseconds - idle) / (float)FadeMilliseconds));
                var color = _finished != 0 && !_successful ? TerracottaError : Terracotta;
                if (now < _trailUntil) {
                    var trailAlpha = (float)Math.Min(1, (_trailUntil - now) / 220.0) * alpha;
                    var head = _position; var length = 0f;
                    for (var index = _trailHistory.Count - 2; index >= 0 && length < 60 * scale; index--) {
                        var old = _trailHistory[index]; var dx = old.X - head.X; var dy = old.Y - head.Y;
                        var distance = (float)Math.Sqrt(dx * dx + dy * dy); if (distance < .1f) continue;
                        var used = Math.Min(distance, 60 * scale - length); var end = new PointF(head.X + dx * used / distance, head.Y + dy * used / distance);
                        using var trail = new Pen(Color.FromArgb((int)(95 * trailAlpha * (1 - (length + used / 2) / (60 * scale))), color), 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                        graphics.DrawLine(trail, tipX + (head.X - _position.X) / scale, tipY + (head.Y - _position.Y) / scale, tipX + (end.X - _position.X) / scale, tipY + (end.Y - _position.Y) / scale);
                        length += used; head = end;
                    }
                }
                using (var glow = new GraphicsPath()) {
                    glow.AddEllipse(tipX - 36, tipY - 36, 80, 80);
                    using var halo = new PathGradientBrush(glow) { CenterPoint = new PointF(tipX + 8, tipY + 8), CenterColor = Color.FromArgb((int)(130 * alpha), color), SurroundColors = new[] { Color.FromArgb(0, color) } };
                    graphics.FillPath(halo, glow);
                }
                if (_finished != 0 && now - _finished < 650) {
                    var pulse = (now - _finished) / 650f; var radius = 10 + 26 * pulse;
                    using var pen = new Pen(Color.FromArgb((int)(200 * (1 - pulse) * alpha), color), 2);
                    graphics.DrawEllipse(pen, tipX - radius, tipY - radius, radius * 2, radius * 2);
                }
                using (var arrow = new GraphicsPath()) {
                    arrow.AddPolygon(new[] { new PointF(tipX, tipY), new PointF(tipX + 32, tipY + 12), new PointF(tipX + 19, tipY + 19), new PointF(tipX + 12, tipY + 32) });
                    using var fill = new SolidBrush(Color.FromArgb((int)(180 * alpha), color));
                    using var outline = new Pen(Color.FromArgb((int)(225 * alpha), 255, 244, 234), 1.5f) { LineJoin = LineJoin.Round };
                    graphics.FillPath(fill, arrow); graphics.DrawPath(outline, arrow);
                    using var fold = new Pen(Color.FromArgb((int)(150 * alpha), 255, 220, 199), 1);
                    graphics.DrawLine(fold, tipX + 4, tipY + 4, tipX + 19, tipY + 19);
                }
                _labelFont ??= new Font("Microsoft YaHei UI", 10, FontStyle.Regular, GraphicsUnit.Point);
                var label = _finished == 0 ? _label : _successful ? "已" + _label : "操作未完成";
                labelWidth = Math.Min(labelWidth, graphics.MeasureString(label, _labelFont).Width + 24);
                var pill = new RectangleF(flipX ? tipX - labelWidth - 16 : tipX + 44, flipY ? tipY - 44 : tipY + 14, labelWidth, 30);
                using (var path = Rounded(pill, 9)) {
                    using var fill = new SolidBrush(Color.FromArgb((int)(237 * alpha), 37, 30, 28)); graphics.FillPath(fill, path);
                    using var border = new Pen(Color.FromArgb((int)(165 * alpha), color), 1); graphics.DrawPath(border, path);
                }
                using (var text = new SolidBrush(Color.FromArgb((int)(255 * alpha), 255, 247, 241))) {
                    using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
                    graphics.DrawString(label, _labelFont, text, pill, format);
                }
                if (!Present(new Rectangle(left, top, width, height))) { Stop(); return; }
                if (Volatile.Read(ref _captureSuppression) == 0) {
                    if (fraction >= 1) { _arrived?.TrySetResult(true); _arrived = null; }
                }
            }
            internal Graphics Canvas(int width, int height)
            {
                EnsureSurface(width, height);
                var graphics = _graphics!; graphics.ResetTransform(); graphics.CompositingMode = CompositingMode.SourceCopy; graphics.Clear(Color.Transparent);
                graphics.CompositingMode = CompositingMode.SourceOver; return graphics;
            }
            internal bool Present(Rectangle bounds, byte opacity = 255)
            {
                if (Volatile.Read(ref _captureSuppression) != 0) return true;
                var destination = new NativePoint { X = bounds.X, Y = bounds.Y }; var origin = new NativePoint(); var size = new NativeSize { Width = bounds.Width, Height = bounds.Height };
                var blend = new Blend { Operation = 0, Alpha = opacity, Format = 1 };
                if (!UpdateLayeredWindow(Handle, IntPtr.Zero, ref destination, ref size, _dc, ref origin, 0, ref blend, 2)) return false;
                if (Volatile.Read(ref _captureSuppression) == 0) SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0053);
                return true;
            }
            internal void RaiseCursor() { if (IsHandleCreated && IsWindowVisible(Handle)) SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0013); }
            private void EnsureSurface(int width, int height)
            {
                if (_bitmap?.Width == width && _bitmap.Height == height) return;
                ReleaseSurface();
                _dc = CreateCompatibleDC(IntPtr.Zero);
                var info = new BitmapInfo { HeaderSize = 40, Width = width, Height = -height, Planes = 1, BitCount = 32, Size = width * height * 4 };
                _dib = CreateDIBSection(_dc, ref info, 0, out var pixels, IntPtr.Zero, 0);
                if (_dc == IntPtr.Zero || _dib == IntPtr.Zero) throw new InvalidOperationException("Cannot create action indicator surface");
                _original = SelectObject(_dc, _dib);
                _bitmap = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, pixels);
                _graphics = Graphics.FromImage(_bitmap); _graphics.SmoothingMode = SmoothingMode.AntiAlias; _graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            }
            private void ReleaseSurface()
            {
                _graphics?.Dispose(); _graphics = null; _bitmap?.Dispose(); _bitmap = null;
                if (_original != IntPtr.Zero) { SelectObject(_dc, _original); _original = IntPtr.Zero; }
                if (_dib != IntPtr.Zero) { DeleteObject(_dib); _dib = IntPtr.Zero; }
                if (_dc != IntPtr.Zero) { DeleteDC(_dc); _dc = IntPtr.Zero; }
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing) { _activity?.Dispose(); _timer.Stop(); _timer.Dispose(); _labelFont?.Dispose(); _labelFont = null; _arrived?.TrySetResult(false); ReleaseSurface(); }
                base.Dispose(disposing);
            }
            internal static GraphicsPath Rounded(RectangleF rectangle, float radius)
            {
                var path = new GraphicsPath(); var diameter = radius * 2;
                path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90); path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
                path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90); path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90); path.CloseFigure(); return path;
            }
        }
        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeSize { internal int Width, Height; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct Blend { internal byte Operation, Flags, Alpha, Format; }
        [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { internal int HeaderSize, Width, Height; internal short Planes, BitCount; internal int Compression, Size, XResolution, YResolution, Used, Important, Color; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetProp(IntPtr window, string name, IntPtr value);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetProp(IntPtr window, string name);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr key, IntPtr value);
        [DllImport("dwmapi.dll")] private static extern int DwmFlush();
        [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr screen, ref NativePoint destination, ref NativeSize size, IntPtr dc, ref NativePoint origin, uint color, ref Blend blend, uint flags);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr pixels, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    }
}
