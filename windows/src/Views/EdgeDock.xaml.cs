using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using YTray.Core;

namespace YTray.Views
{
    /// <summary>
    /// 屏幕边缘吸附标签（对应 macOS EdgeDock）。
    /// 鼠标悬停展开橙色条 + 圆点；点击展开完整小组件；可上下拖动；右键切换左右/重置/隐藏。
    /// </summary>
    public partial class EdgeDock : Window
    {
        private readonly InstanceStore _store;
        private readonly Action _onOpenWidget;
        private bool _dragging;
        private Point _dragStartMouse;
        private double _dragStartY;
        private bool _isOnLeft;
        private int _yPercent = 58;
        private WidgetView _widget;

        public new bool IsEnabled { get; set; } = true;

        public EdgeDock(InstanceStore store, Action onOpenWidget)
        {
            InitializeComponent();
            _store = store;
            _onOpenWidget = onOpenWidget;
            Reposition();
        }

        public void ShowDock()
        {
            Reposition();
            Show();
        }

        public void HideDock() => Hide();

        private void Reposition()
        {
            var wa = SystemParameters.WorkArea;
            Left = _isOnLeft ? 0 : wa.Width - Width;
            Top = wa.Height * (_yPercent / 100.0) - Height / 2;
            // clamp
            if (Top < 0) Top = 0;
            if (Top + Height > wa.Height) Top = wa.Height - Height;
        }

        private void OnMouseEnter(object s, MouseEventArgs e)
        {
            // Expand: widen the strip and show dots
            var widen = new DoubleAnimation(10, 22, TimeSpan.FromMilliseconds(160));
            TabStrip.BeginAnimation(WidthProperty, widen);
            DotsPanel.Visibility = Visibility.Visible;
        }

        private void OnMouseLeave(object s, MouseEventArgs e)
        {
            var narrow = new DoubleAnimation(22, 10, TimeSpan.FromMilliseconds(160));
            TabStrip.BeginAnimation(WidthProperty, narrow);
            DotsPanel.Visibility = Visibility.Collapsed;
        }

        private void OnMouseLeftDown(object s, MouseButtonEventArgs e)
        {
            _dragging = false;
            _dragStartMouse = e.GetPosition(null);
            _dragStartY = Top;
            CaptureMouse();
        }

        private void OnMouseMove(object s, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var pos = e.GetPosition(null);
            var dy = pos.Y - _dragStartMouse.Y;
            if (Math.Abs(dy) > 4)
            {
                _dragging = true;
                Top = _dragStartY + dy;
                var wa = SystemParameters.WorkArea;
                if (Top < 0) Top = 0;
                if (Top + Height > wa.Height) Top = wa.Height - Height;
            }
        }

        private void OnMouseLeftUp(object s, MouseButtonEventArgs e)
        {
            ReleaseMouseCapture();
            if (!_dragging)
            {
                // Click — open the widget
                OpenWidget();
            }
            else
            {
                // Save vertical position as percentage
                var wa = SystemParameters.WorkArea;
                _yPercent = (int)((Top + Height / 2) / wa.Height * 100);
                if (_yPercent < 5) _yPercent = 5;
                if (_yPercent > 95) _yPercent = 95;
            }
        }

        private void OpenWidget()
        {
            if (_widget == null)
            {
                _widget = new WidgetView(_store) { ShowActivated = false };
                _widget.OpenManagerRequested += (s, e) => _onOpenWidget?.Invoke();
            }
            // Show first so ActualWidth/ActualHeight become valid, then position beside the tab.
            _widget.Show();
            _widget.PositionBeside(
                _isOnLeft ? Left + Width : Left,
                Top + Height / 2,
                _isOnLeft);
            _widget.Activate();
        }

        private void OnMouseRightUp(object s, MouseButtonEventArgs e)
        {
            var menu = new ContextMenu();
            var leftItem = new MenuItem { Header = "贴屏幕左缘", IsCheckable = true, IsChecked = _isOnLeft };
            leftItem.Click += (s2, e2) => { _isOnLeft = true; Reposition(); };
            var rightItem = new MenuItem { Header = "贴屏幕右缘", IsCheckable = true, IsChecked = !_isOnLeft };
            rightItem.Click += (s2, e2) => { _isOnLeft = false; Reposition(); };
            menu.Items.Add(leftItem);
            menu.Items.Add(rightItem);
            menu.Items.Add(new Separator());
            var reset = new MenuItem { Header = "恢复默认位置" };
            reset.Click += (s2, e2) => { _isOnLeft = false; _yPercent = 58; Reposition(); };
            menu.Items.Add(reset);
            var hide = new MenuItem { Header = "隐藏边缘小组件" };
            hide.Click += (s2, e2) => { IsEnabled = false; HideDock(); };
            menu.Items.Add(hide);
            menu.IsOpen = true;
        }
    }
}