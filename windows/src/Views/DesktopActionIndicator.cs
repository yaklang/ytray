#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace YTray.Views
{
    internal sealed class DesktopActionIndicator : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private Window? _window;
        internal IntPtr NativeHandle { get; private set; }
        private readonly DispatcherTimer _hide;
        internal DesktopActionIndicator(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher; _hide = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(600) };
            _hide.Tick += (_, __) => Hide();
        }
        internal void Show(double x, double y, Func<bool>? admitted = null)
        {
            if (Application.Current == null || _dispatcher.HasShutdownStarted) return;
            _dispatcher.InvokeAsync(() => {
                if (admitted != null && !admitted()) return;
                _hide.Stop();
                if (_window == null) {
                    _window = new Window { Width = 120, Height = 120, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                        ShowInTaskbar = false, ShowActivated = false, Topmost = true, ResizeMode = ResizeMode.NoResize, IsHitTestVisible = false, Content = new CursorArtwork() };
                    _window.SourceInitialized += (_, __) => { var h = new WindowInteropHelper(_window).Handle; NativeHandle = h; SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x08000000 | 0x20 | 0x80); SetWindowDisplayAffinity(h, 0x11); };
                }
                _window.Show(); var handle = new WindowInteropHelper(_window).Handle;
                var transform = PresentationSource.FromVisual(_window)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
                var tip = transform.Transform(new Point(42, 34)); SetWindowPos(handle, new IntPtr(-1), (int)(x - tip.X), (int)(y - tip.Y), 0, 0, 0x10 | 0x1);
                _hide.Start();
            }, DispatcherPriority.Send);
        }
        internal void Hide()
        {
            if (_dispatcher.HasShutdownStarted) return;
            if (!_dispatcher.CheckAccess()) { _dispatcher.InvokeAsync(Hide, DispatcherPriority.Send); return; }
            _hide.Stop(); _window?.Hide();
        }
        public void Dispose() { if (_dispatcher.HasShutdownStarted) return; _dispatcher.InvokeAsync(() => { _hide.Stop(); _window?.Close(); _window = null; }); }
        private sealed class CursorArtwork : FrameworkElement
        {
            protected override void OnRender(DrawingContext drawing)
            {
                var color = Color.FromRgb(201, 107, 80);
                var glow = new RadialGradientBrush(Color.FromArgb(60, color.R, color.G, color.B), Colors.Transparent);
                drawing.DrawEllipse(glow, null, new Point(54, 55), 52, 52);
                var shape = Geometry.Parse("M 42,34 L 44,72 L 54,62 L 65,80 L 72,76 L 61,58 L 76,56 Z");
                drawing.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(25, color.R, color.G, color.B)), 10), shape);
                drawing.DrawGeometry(null, new Pen(new SolidColorBrush(color), 2.5), shape);
                drawing.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(100, color.R, color.G, color.B)), 1.5), new Point(42, 34), 11, 11);
            }
        }
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int index, int value);
        [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }
}
