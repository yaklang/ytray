#nullable enable
using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using YTray.Core;

namespace YTray.Views
{
    /// <summary>
    /// A non-activating screen-edge tab. Opening the widget is deliberately queued until
    /// after the pointer event and mouse capture have completed, avoiding transparent-window
    /// input re-entrancy on WPF.
    /// </summary>
    public partial class EdgeDock : Window
    {
        private const int DefaultYPercent = 58;
        private readonly InstanceStore _store;
        private readonly Action<EdgeDock, bool>? _onOpenWidget;
        private bool _dragging;
        private bool _openQueued;
        private double _dragStartScreenY;
        private double _dragStartY;

        private bool IsOnLeft
        {
            get => _store.Settings.EdgeDockOnLeft;
            set => _store.Settings.EdgeDockOnLeft = value;
        }

        private int YPercent
        {
            get => Math.Max(5, Math.Min(95, _store.Settings.EdgeDockYPercent));
            set => _store.Settings.EdgeDockYPercent = Math.Max(5, Math.Min(95, value));
        }

        public EdgeDock(InstanceStore store, Action<EdgeDock, bool>? onOpenWidget)
        {
            InitializeComponent();
            _store = store;
            _onOpenWidget = onOpenWidget;
            SourceInitialized += (s, e) => Reposition();
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            Closed += (s, e) => SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            ApplySideAlignment();
        }

        public void ShowDock(bool remember = true)
        {
            if (remember)
            {
                _store.Settings.EdgeDockEnabled = true;
                _store.SaveSettings();
            }
            Reposition();
            Show();
        }

        public void HideDock(bool remember = true)
        {
            if (remember)
            {
                _store.Settings.EdgeDockEnabled = false;
                _store.SaveSettings();
            }
            if (IsMouseCaptured) ReleaseMouseCapture();
            Hide();
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e) =>
            Dispatcher.BeginInvoke(new Action(Reposition), DispatcherPriority.Background);

        private DpiScale CurrentDpi()
        {
            try { return VisualTreeHelper.GetDpi(this); }
            catch { return new DpiScale(1, 1); }
        }

        private System.Windows.Forms.Screen CurrentScreen()
        {
            var handle = new WindowInteropHelper(this).Handle;
            return handle == IntPtr.Zero
                ? System.Windows.Forms.Screen.PrimaryScreen
                : System.Windows.Forms.Screen.FromHandle(handle);
        }

        private void Reposition()
        {
            var screen = CurrentScreen();
            var dpi = CurrentDpi();
            var work = screen.WorkingArea;
            var left = work.Left / dpi.DpiScaleX;
            var top = work.Top / dpi.DpiScaleY;
            var right = work.Right / dpi.DpiScaleX;
            var bottom = work.Bottom / dpi.DpiScaleY;

            Left = IsOnLeft ? left : right - Width;
            Top = top + (bottom - top) * (YPercent / 100.0) - Height / 2;
            Top = Math.Max(top + 4, Math.Min(Top, bottom - Height - 4));
            ApplySideAlignment();
        }

        private void ApplySideAlignment()
        {
            if (TabStrip == null) return;
            TabStrip.HorizontalAlignment = IsOnLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        }

        private void OnMouseEnter(object sender, MouseEventArgs e)
        {
            if (_dragging) return;
            TabStrip.BeginAnimation(WidthProperty, new DoubleAnimation(TabStrip.ActualWidth, 20, TimeSpan.FromMilliseconds(135))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
            DotsPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(DotsPanel.Opacity, 1, TimeSpan.FromMilliseconds(110)));
        }

        private void OnMouseLeave(object sender, MouseEventArgs e)
        {
            if (_dragging) return;
            TabStrip.BeginAnimation(WidthProperty, new DoubleAnimation(TabStrip.ActualWidth, 9, TimeSpan.FromMilliseconds(145)));
            DotsPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(DotsPanel.Opacity, 0, TimeSpan.FromMilliseconds(90)));
        }

        private void OnMouseLeftDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            _dragging = false;
            _dragStartScreenY = System.Windows.Forms.Cursor.Position.Y / CurrentDpi().DpiScaleY;
            _dragStartY = Top;
            Mouse.Capture(this, CaptureMode.SubTree);
            e.Handled = true;
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || !IsMouseCaptured) return;
            var currentY = System.Windows.Forms.Cursor.Position.Y / CurrentDpi().DpiScaleY;
            var delta = currentY - _dragStartScreenY;
            if (!_dragging && Math.Abs(delta) <= 4) return;

            _dragging = true;
            var screen = CurrentScreen();
            var dpi = CurrentDpi();
            var workTop = screen.WorkingArea.Top / dpi.DpiScaleY;
            var workBottom = screen.WorkingArea.Bottom / dpi.DpiScaleY;
            Top = Math.Max(workTop + 4, Math.Min(_dragStartY + delta, workBottom - Height - 4));
            e.Handled = true;
        }

        private void OnMouseLeftUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (IsMouseCaptured) ReleaseMouseCapture();
            e.Handled = true;

            if (_dragging)
            {
                RememberVerticalPosition();
                _dragging = false;
                return;
            }

            QueueOpenWidget();
        }

        private void RememberVerticalPosition()
        {
            var screen = CurrentScreen();
            var dpi = CurrentDpi();
            var top = screen.WorkingArea.Top / dpi.DpiScaleY;
            var height = screen.WorkingArea.Height / dpi.DpiScaleY;
            YPercent = (int)Math.Round((Top + Height / 2 - top) / height * 100);
            _store.SaveSettings();
        }

        private void QueueOpenWidget()
        {
            if (_openQueued) return;
            _openQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _openQueued = false;
                _onOpenWidget?.Invoke(this, IsOnLeft);
            }), DispatcherPriority.ContextIdle);
        }

        private void OnMouseRightUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            var menu = new System.Windows.Controls.ContextMenu();
            var leftItem = new System.Windows.Controls.MenuItem { Header = "贴屏幕左缘", IsCheckable = true, IsChecked = IsOnLeft };
            leftItem.Click += (s, args) => SetSide(true);
            var rightItem = new System.Windows.Controls.MenuItem { Header = "贴屏幕右缘", IsCheckable = true, IsChecked = !IsOnLeft };
            rightItem.Click += (s, args) => SetSide(false);
            menu.Items.Add(leftItem);
            menu.Items.Add(rightItem);
            menu.Items.Add(new System.Windows.Controls.Separator());

            var reset = new System.Windows.Controls.MenuItem { Header = "恢复默认位置" };
            reset.Click += (s, args) =>
            {
                IsOnLeft = false;
                YPercent = DefaultYPercent;
                _store.SaveSettings();
                Reposition();
            };
            menu.Items.Add(reset);

            var hide = new System.Windows.Controls.MenuItem { Header = "隐藏边缘小组件" };
            hide.Click += (s, args) => HideDock();
            menu.Items.Add(hide);
            menu.PlacementTarget = RootGrid;
            menu.IsOpen = true;
        }

        private void SetSide(bool onLeft)
        {
            IsOnLeft = onLeft;
            _store.SaveSettings();
            Reposition();
        }
    }
}
