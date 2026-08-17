#nullable enable
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using YTray.Core;
using YTray.Native;

namespace YTray.Views
{
    /// <summary>Non-activating, mouse-transparent secondary preview matching the Swift hover panel.</summary>
    public partial class ThumbnailPreviewWindow : Window
    {
        private readonly DispatcherTimer _delay;
        private FrameworkElement? _anchor;
        private ImageSource? _image;
        private string _title = "浏览器页面预览";

        public ThumbnailPreviewWindow()
        {
            InitializeComponent();
            _delay = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(180),
            };
            _delay.Tick += (s, e) =>
            {
                _delay.Stop();
                ShowPending();
            };
            SourceInitialized += (s, e) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                var style = NativeWindowStyles.GetExtendedStyle(hwnd);
                NativeWindowStyles.SetExtendedStyle(hwnd,
                    style | NativeWindowStyles.WsExTransparent | NativeWindowStyles.WsExNoActivate | NativeWindowStyles.WsExToolWindow);
            };
        }

        public void Schedule(FrameworkElement anchor, ImageSource image, string title)
        {
            if (anchor == null || image == null) return;
            _anchor = anchor;
            _image = image;
            _title = string.IsNullOrWhiteSpace(title) ? "浏览器页面预览" : title;
            _delay.Stop();
            _delay.Start();
        }

        public void Dismiss()
        {
            _delay.Stop();
            _anchor = null;
            _image = null;
            if (IsVisible) Hide();
        }

        private void ShowPending()
        {
            if (_anchor == null || !_anchor.IsMouseOver || _image == null) return;
            PreviewImage.Source = _image;
            PreviewTitle.Text = _title;

            var point = _anchor.PointToScreen(new Point(0, 0));
            var scale = VisualTreeHelper.GetDpi(_anchor);
            var anchorLeft = point.X / scale.DpiScaleX;
            var anchorTop = point.Y / scale.DpiScaleY;
            var anchorRight = anchorLeft + _anchor.ActualWidth;
            var anchorMidY = anchorTop + _anchor.ActualHeight / 2;
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)point.X, (int)point.Y));
            var workLeft = screen.WorkingArea.Left / scale.DpiScaleX;
            var workTop = screen.WorkingArea.Top / scale.DpiScaleY;
            var workRight = screen.WorkingArea.Right / scale.DpiScaleX;
            var workBottom = screen.WorkingArea.Bottom / scale.DpiScaleY;

            var x = anchorLeft - Width - 10;
            if (x < workLeft + 8) x = anchorRight + 10;
            Left = Math.Max(workLeft + 8, Math.Min(x, workRight - Width - 8));
            Top = Math.Max(workTop + 8, Math.Min(anchorMidY - Height / 2, workBottom - Height - 8));
            if (!IsVisible) Show();
        }
    }
}
