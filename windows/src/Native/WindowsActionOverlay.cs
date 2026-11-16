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
using Newtonsoft.Json.Linq;
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
        private static readonly ConcurrentDictionary<IntPtr, uint> Surfaces = new ConcurrentDictionary<IntPtr, uint>();
        private static readonly object VisibilitySync = new object();
        private readonly object _sync = new object();
        private readonly object _watchSync = new object();
        private readonly AutoResetEvent _changed = new AutoResetEvent(false);
        private WindowsControlTarget? _watched;
        private HashSet<IntPtr>? _watchedPopups;
        private uint _watchStarted;
        private readonly Queue<Action<OverlayWindow>> _pending = new Queue<Action<OverlayWindow>>();
        private OverlayWindow? _window;
        private Thread? _thread;
        private bool _disposed;
        private long _sequence;
        private long _announcedOperation;
        private uint _surfaceThread;
        private static int _captureSuppression;
        private readonly Action<string>? _showTrace;

        internal WindowsActionOverlay(Action<string>? showTrace = null) => _showTrace = showTrace;

        // Private worker feedback, one lifecycle per public MCP operation. The
        // cursor's internal key/text steps do not restart the operation banner.
        internal bool Operation(long id, string phase, IntPtr target, string description, string context, JObject? task = null,
            bool showEdges = true, string? traceId = null, string? label = null)
        {
            var previous = Interlocked.Read(ref _announcedOperation);
            if (id > previous && Interlocked.CompareExchange(ref _announcedOperation, id, previous) == previous
                && showEdges && phase is "prepare" or "running") {
                // Withdraw hit targets before the next native action checks the
                // pointer location. The activity card itself is click-through.
                var thread = Volatile.Read(ref _surfaceThread);
                foreach (var surface in Surfaces)
                    if (surface.Value == thread && IsInteractiveKind(WindowKind(surface.Key))) ShowWindow(surface.Key, 0);
            }
            return Post(window => window.Operation(id, phase, target, description, context, task, showEdges, traceId, label));
        }
        internal bool OperationTarget(long id, IntPtr target) => Post(window => window.OperationTarget(id, target));
        internal void DismissOperation() => Post(window => window.DismissOperation());
        internal bool Highlight(Rectangle bounds) => Post(window => window.Highlight(bounds));

        internal Feedback Begin(IntPtr target, Point point, string label, bool animate = true)
        {
            var sequence = Interlocked.Increment(ref _sequence);
            return Move(sequence, target, point, label, animate) ? new Feedback(this, sequence) : new Feedback(null, 0);
        }
        private bool Move(long sequence, IntPtr target, Point point, string label, bool animate) =>
            Post(window => window.Begin(sequence, target, point, label, animate));

        private void Run()
        {
            var changeHook = IntPtr.Zero;
            var visibilityHook = IntPtr.Zero;
            WinEventCallback changed = OnWindowChanged;
            try {
                Volatile.Write(ref _surfaceThread, GetCurrentThreadId());
                using var window = new OverlayWindow {
                    ShowTrace = _showTrace,
                    IsCurrentOperation = id => Interlocked.Read(ref _announcedOperation) == id
                };
                _ = window.Handle;
                changeHook = SetWinEventHook(0x800c, 0x800e, IntPtr.Zero, changed, 0, 0, 2);
                visibilityHook = SetWinEventHook(0x8001, 0x8003, IntPtr.Zero, changed, 0, 0, 2);
                lock (_sync) {
                    if (_disposed) return;
                    _window = window;
                    while (_pending.Count > 0) Post(window, _pending.Dequeue());
                }
                Forms.Application.Run();
            } catch { }
            finally {
                if (changeHook != IntPtr.Zero) UnhookWinEvent(changeHook);
                if (visibilityHook != IntPtr.Zero) UnhookWinEvent(visibilityHook);
                GC.KeepAlive(changed);
                lock (_sync) { _window = null; _pending.Clear(); }
            }
        }
        internal void WatchWindowChange(WindowsControlTarget target)
        {
            var popups = new HashSet<IntPtr>(target.VisibleOwnedHandles());
            lock (_watchSync) {
                _changed.Reset();
                _watchStarted = unchecked((uint)Environment.TickCount);
                _watched = target;
                _watchedPopups = popups;
            }
        }
        internal bool WaitForWindowChange(int milliseconds, WaitHandle? cancellation = null) =>
            cancellation == null ? _changed.WaitOne(milliseconds) : WaitHandle.WaitAny(new[] { _changed, cancellation }, milliseconds) == 0;
        internal void EndWindowWatch()
        {
            lock (_watchSync) { _watched = null; _watchedPopups = null; }
        }
        private void OnWindowChanged(IntPtr hook, uint kind, IntPtr window, int item, int child, uint thread, uint tick)
        {
            if (window == IntPtr.Zero || kind == 0x800d) return;
            lock (_watchSync) {
                var target = _watched;
                if (target == null || unchecked((int)(tick - _watchStarted)) < 0) return;
                if (kind is 0x8001 or 0x8002 or 0x8003) {
                    // A destroyed HWND may have lost its owner by the time WinEvent is delivered.
                    if (item == 0 && child == 0 && (window == target.Handle || _watchedPopups?.Contains(window) == true || target.Owns(window)))
                        _changed.Set();
                } else if (window == target.Handle || GetAncestor(window, 2) == target.Handle) _changed.Set();
            }
        }
        private bool Post(Action<OverlayWindow> action)
        {
            lock (_sync) {
                if (_disposed) return false;
                if (_window != null) return Post(_window, action);
                if (_thread == null) {
                    try {
                        _thread = new Thread(Run) { IsBackground = true, Name = "YTray action indicator" };
                        _thread.SetApartmentState(ApartmentState.STA);
                        _thread.Start();
                    } catch { return false; }
                }
                if (!_thread.IsAlive) return false;
                if (_pending.Count == 32) _pending.Dequeue();
                _pending.Enqueue(action);
                return true;
            }
        }
        private static bool Post(OverlayWindow window, Action<OverlayWindow> action)
        {
            try {
                window.BeginInvoke(new Action(() => {
                    try { action(window); }
                    catch (Exception error) { Console.Error.WriteLine("YTray action indicator: " + error.Message); window.Stop(); }
                }));
                return true;
            } catch { return false; }
        }
        public void Dispose()
        {
            Thread? thread;
            lock (_sync) {
                if (_disposed) return;
                _disposed = true;
                _pending.Clear();
                if (_window != null) Post(_window, window => { window.Close(); Forms.Application.ExitThread(); });
                thread = _thread;
            }
            if (thread != null && thread != Thread.CurrentThread) thread.Join(200);
            lock (_watchSync) { _watched = null; _watchedPopups = null; _changed.Dispose(); }
        }
        internal sealed class Feedback : IDisposable
        {
            private readonly WindowsActionOverlay? _owner;
            private readonly long _sequence;
            private bool _finished;
            internal Feedback(WindowsActionOverlay? owner, long sequence) { _owner = owner; _sequence = sequence; }
            internal void Complete()
            {
                if (_finished) return;
                _finished = true;
                _owner?.Post(window => window.Finish(_sequence, true));
            }
            internal void Retarget(IntPtr target, Point point, string label) => _owner?.Move(_sequence, target, point, label, false);
            public void Dispose()
            {
                if (_finished) return;
                _finished = true;
                _owner?.Post(window => window.Finish(_sequence, false));
            }
        }

        internal static bool IsOverlayWindow(IntPtr window) => GetProp(window, WindowMarker) == new IntPtr(1);
        internal static int WindowKind(IntPtr window) => GetProp(window, KindMarker).ToInt32();
        private static bool IsInteractiveKind(int kind) => kind is 6 or 7;
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        // Keep normal capture (including UU/RDP/screen sharing) intact. Hide
        // only during our own screen-copy fallback and only where it reads pixels.
        internal static IDisposable HideForCapture(Rectangle? bounds = null)
        {
            // Suppress new surfaces as well as the ones that are already visible.
            lock (VisibilitySync) Interlocked.Increment(ref _captureSuppression);
            var windows = Surfaces.Keys.Where(window => IsWindowVisible(window) && (!bounds.HasValue
                || !GetWindowRect(window, out var rect) || bounds.Value.IntersectsWith(Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom)))).ToArray();
            foreach (var window in windows) ShowWindow(window, 0);
            if (windows.Length > 0) DwmFlush();
            return new CaptureVisibility();
        }
        private sealed class CaptureVisibility : IDisposable
        {
            private int _active = 1;
            public void Dispose() {
                if (Interlocked.Exchange(ref _active, 0) != 0 && Interlocked.Decrement(ref _captureSuppression) == 0)
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
            private Rectangle _presentedBounds;
            private byte _presentedOpacity;
            private bool _pixelsChanged;
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
            private readonly int _kind;
            private IntPtr _registeredHandle;
            private OperationFeedback? _activity;
            private OverlayWindow? _outline;
            private Forms.Timer? _outlineTimer;
            internal Action<string>? ShowTrace;
            internal Func<long, bool>? IsCurrentOperation;
            internal Action? OnDetailsClick;
            private bool AcceptsClicks => IsInteractiveKind(_kind);
            internal bool IsShown => IsHandleCreated && IsWindowVisible(Handle);

            internal OverlayWindow(int kind = 1)
            {
                _kind = kind;
                Text = ""; ShowInTaskbar = false; TopMost = true; FormBorderStyle = Forms.FormBorderStyle.None;
                StartPosition = Forms.FormStartPosition.Manual;
                AutoScaleMode = Forms.AutoScaleMode.None;
                if (AcceptsClicks) Cursor = Forms.Cursors.Hand;
                _timer.Tick += (_, __) => RenderSafely();
            }
            protected override bool ShowWithoutActivation => true;
            protected override Forms.CreateParams CreateParams {
                get { var value = base.CreateParams; value.ExStyle |= AcceptsClicks ? 0x08080088 : 0x080800a8; return value; }
            }
            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e); SetProp(Handle, WindowMarker, new IntPtr(1));
                SetProp(Handle, KindMarker, new IntPtr(_kind)); _registeredHandle = Handle; Surfaces[Handle] = GetCurrentThreadId();
                _pixelsChanged = true;
                // System-wide capture exclusion would hide feedback from remote
                // viewers too. Own screenshots use the short capture fence above.
            }
            protected override void OnHandleDestroyed(EventArgs e) { Surfaces.TryRemove(_registeredHandle, out _); _registeredHandle = IntPtr.Zero; base.OnHandleDestroyed(e); }
            protected override void WndProc(ref Forms.Message message)
            {
                if (message.Msg == 0x84) { message.Result = new IntPtr(AcceptsClicks ? 1 : -1); return; } // HTCLIENT / HTTRANSPARENT
                if (message.Msg == 0x21) { message.Result = new IntPtr(3); return; } // MA_NOACTIVATE
                // Every layer is laid out in screen pixels on render. Applying
                // WinForms' DPI-suggested rectangle can move the layer again.
                if (message.Msg == 0x02e0) { message.Result = IntPtr.Zero; return; } // WM_DPICHANGED
                if (message.Msg == 0x8001) { _activity?.RenderSafely(); if (_timer.Enabled) RenderSafely(); return; }
                base.WndProc(ref message);
            }
            protected override void OnMouseUp(Forms.MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (AcceptsClicks && e.Button == Forms.MouseButtons.Left) OnDetailsClick?.Invoke();
            }
            internal void Operation(long id, string phase, IntPtr target, string description, string context, JObject? task,
                bool showEdges, string? traceId, string? label)
            {
                _activity ??= new OperationFeedback(this);
                _activity.Update(id, phase, target, description, context, task, showEdges, traceId, label);
            }
            internal void OperationTarget(long id, IntPtr target) => _activity?.SetTarget(id, target);
            internal void DismissOperation() => _activity?.Stop();
            internal void Highlight(Rectangle bounds)
            {
                _outline ??= new OverlayWindow(5);
                var graphics = _outline.Canvas(bounds.Width, bounds.Height);
                using var stroke = new Pen(Color.FromArgb(245, 238, 130, 101), 4);
                graphics.DrawRectangle(stroke, 2, 2, Math.Max(0, bounds.Width - 4), Math.Max(0, bounds.Height - 4));
                _outline.Present(bounds);
                if (_outlineTimer == null) {
                    _outlineTimer = new Forms.Timer { Interval = 1800 };
                    _outlineTimer.Tick += (_, __) => {
                        _outlineTimer.Stop(); _outline?.Dispose(); _outline = null;
                    };
                }
                _outlineTimer.Stop(); _outlineTimer.Start();
            }
            internal void Begin(long sequence, IntPtr target, Point point, string label, bool animate)
            {
                if (sequence < _sequence) return;
                _sequence = sequence; _target = target; _label = _activity?.ActiveDescription ?? label; AccessibleDescription = "AI · " + _label;
                _started = _clock.ElapsedMilliseconds; _finished = 0; _successful = false;
                _from = _placed ? _position : GetCursorPos(out var cursor) ? new PointF(cursor.X, cursor.Y) : new PointF(point.X, point.Y);
                _to = point;
                var scale = Math.Max(1, GetDpiForWindow(target) / 96f);
                _path = animate ? new WindowsCursorMotion(_from, _to, Forms.SystemInformation.VirtualScreen, scale) : null;
                _motion = _path?.DurationMilliseconds ?? 0;
                _trailHistory.Clear(); _trailUntil = 0;
                if (_motion > 0) { _trailUntil = _started + _motion + 160; _trailHistory.Add(_from); }
                _placed = true; _timer.Interval = 16; _timer.Start(); Render();
            }
            internal void Finish(long sequence, bool success)
            {
                if (sequence != _sequence) return;
                _finished = Math.Max(1, _clock.ElapsedMilliseconds); _successful = success;
                _motion = 0; _path = null; _position = _to; _trailUntil = 0;
                if (!success) AccessibleDescription = "操作未完成";
                _timer.Interval = 16;
                Render();
            }
            internal void Stop()
            {
                _timer.Stop();
                if (IsShown) ShowWindow(Handle, 0);
            }
            private void RenderSafely() { try { Render(); } catch (Exception error) { Console.Error.WriteLine("YTray action indicator: " + error.Message); Stop(); } }
            private void Render()
            {
                var now = _clock.ElapsedMilliseconds;
                var completed = _finished != 0;
                var idle = completed ? Math.Max(0, now - _finished) : 0;
                if (_target != IntPtr.Zero && (!IsWindowVisible(_target) || IsIconic(_target)) || (completed && idle >= HoldMilliseconds)) { Stop(); return; }
                if (Volatile.Read(ref _captureSuppression) != 0) return;
                var fraction = _motion == 0 ? 1 : Math.Min(1, (now - _started) / (double)_motion);
                // Motion and pulses remain smooth; the long, static dwell does
                // not need animation-rate redraws. A new action resumes at 60Hz.
                var interval = fraction < 1 || now < _trailUntil || completed && (idle < 650 || idle >= HoldMilliseconds - FadeMilliseconds) ? 16 : 160;
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
                var tipX = flipX ? 384f : 96f; const float tipY = 96;
                var left = (int)Math.Round(_position.X - tipX * scale); var top = (int)Math.Round(_position.Y - tipY * scale);
                var graphics = Canvas(width, height);
                graphics.ScaleTransform(scale, scale);
                var alpha = completed ? Math.Min(1, Math.Max(0, (HoldMilliseconds - idle) / (float)FadeMilliseconds)) : 1f;
                var color = completed && !_successful ? TerracottaError : Terracotta;
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
                if (completed && idle < 650) {
                    var pulse = idle / 650f; var radius = 10 + 26 * pulse;
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
                var label = completed ? (_successful ? "已" + _label : "操作未完成") : _label;
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
            }
            internal Graphics Canvas(int width, int height)
            {
                EnsureSurface(width, height);
                _pixelsChanged = true;
                var graphics = _graphics!; graphics.ResetTransform(); graphics.CompositingMode = CompositingMode.SourceCopy; graphics.Clear(Color.Transparent);
                graphics.CompositingMode = CompositingMode.SourceOver; return graphics;
            }
            internal bool Present(Rectangle bounds, byte opacity = 255)
            {
                lock (VisibilitySync) {
                    if (Volatile.Read(ref _captureSuppression) != 0) return true;
                    var handle = Handle;
                    if (_pixelsChanged || bounds != _presentedBounds || opacity != _presentedOpacity) {
                        var destination = new NativePoint { X = bounds.X, Y = bounds.Y };
                        var origin = new NativePoint();
                        var size = new NativeSize { Width = bounds.Width, Height = bounds.Height };
                        var blend = new Blend { Operation = 0, Alpha = opacity, Format = 1 };
                        if (!UpdateLayeredWindow(handle, IntPtr.Zero, ref destination, ref size, _dc, ref origin, 0, ref blend, 2)) return false;
                        _pixelsChanged = false;
                        _presentedBounds = bounds;
                        _presentedOpacity = opacity;
                    }
                    // Reordering a visible layered window on every frame can briefly cover its siblings.
                    return IsWindowVisible(handle) || SetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0, 0x0053);
                }
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
                if (disposing) {
                    _activity?.Dispose(); _outlineTimer?.Dispose(); _outline?.Dispose();
                    _timer.Stop(); _timer.Dispose(); _labelFont?.Dispose(); _labelFont = null; ReleaseSurface();
                }
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
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out WindowsControlTarget.Rect bounds);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
        private delegate void WinEventCallback(IntPtr hook, uint kind, IntPtr window, int item, int child, uint thread, uint tick);
        [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint minimum, uint maximum, IntPtr module, WinEventCallback callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
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
