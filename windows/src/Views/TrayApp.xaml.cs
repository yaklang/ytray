#nullable enable
using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Threading;
using YTray.Core;
using YTray.Views;
using YTray.Native;

namespace YTray
{
    /// <summary>System tray integration and the single shared popover instance.</summary>
    public class TrayApp : IDisposable
    {
        private readonly InstanceStore _store;
        private readonly System.Windows.Forms.NotifyIcon _notify;
        private readonly Icon _trayIcon;
        private WidgetView? _widget;
        private ManagerView? _manager;
        private readonly EdgeDock _edgeDock;
        private System.Windows.Forms.ContextMenu? _menu;
        private bool _statusRefreshScheduled;

        public TrayApp(InstanceStore store)
        {
            _store = store;
            _store.PropertyChanged += OnStorePropertyChanged;

            _trayIcon = CreateTrayIcon();
            _notify = new System.Windows.Forms.NotifyIcon
            {
                Icon = _trayIcon,
                Visible = true,
                Text = "YTray · 右键打开菜单",
            };

            _edgeDock = new EdgeDock(_store, ShowWidgetBesideEdge);
            BuildMenu();
            UpdateStatusTitle();
            if (_store.Settings.EdgeDockEnabled) _edgeDock.ShowDock(remember: false);
        }

        private void OnStorePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted || _statusRefreshScheduled) return;
            _statusRefreshScheduled = true;
            dispatcher.BeginInvoke(new Action(() =>
            {
                _statusRefreshScheduled = false;
                UpdateStatusTitle();
            }), DispatcherPriority.Background);
        }

        private void BuildMenu()
        {
            _menu = new System.Windows.Forms.ContextMenu();
            _menu.MenuItems.Add("无代理启动", (s, e) => _store.LaunchConfigured(false));
            _menu.MenuItems.Add("使用 HTTP 代理启动", (s, e) => _store.LaunchConfigured(true));
            _menu.MenuItems.Add("显示小组件", (s, e) => ShowWidget());
            _menu.MenuItems.Add("全部管理", (s, e) => ShowManager());
            _menu.MenuItems.Add("-");
            _menu.MenuItems.Add("显示边缘小组件", (s, e) => _edgeDock.ShowDock());
            _menu.MenuItems.Add("-");
            _menu.MenuItems.Add("退出 YTray", (s, e) => Application.Current.Shutdown());
            _notify.ContextMenu = _menu;
        }

        private void UpdateStatusTitle()
        {
            var count = _store.RunningInstances.Count;
            _notify.Text = $"YTray · {count} 个运行中实例";
        }

        private WidgetView EnsureWidget()
        {
            if (_widget != null) return _widget;
            _widget = new WidgetView(_store);
            _widget.OpenManagerRequested += (s, e) => ShowManager();
            return _widget;
        }

        public void ShowWidget()
        {
            var widget = EnsureWidget();
            ShowWidgetCore(widget, () => widget.PositionNearCursor());
        }

        private void ShowWidgetBesideEdge(EdgeDock edge, bool onLeft)
        {
            var widget = EnsureWidget();
            ShowWidgetCore(widget, () => widget.PositionBeside(edge, onLeft));
        }

        private static void ShowWidgetCore(WidgetView widget, Action position)
        {
            widget.PrepareToShow();
            if (!widget.IsVisible) widget.Show();
            widget.RefreshAndMeasure();
            position();
            widget.CancelPendingDismiss();
            widget.Activate();
            widget.PlayEntrance();
        }

        public void ShowManager()
        {
            _widget?.HideWidget();
            if (_manager == null)
            {
                _manager = new ManagerView(_store);
                _manager.Closed += OnManagerClosed;
            }
            _manager.Show();
            if (_manager.WindowState == WindowState.Minimized) _manager.WindowState = WindowState.Normal;
            _manager.Activate();
        }

        private void OnManagerClosed(object sender, EventArgs e)
        {
            if (sender is ManagerView manager) manager.Closed -= OnManagerClosed;
            _manager = null;
        }

        private static Icon CreateTrayIcon()
        {
            using (var bitmap = new Bitmap(16, 16))
            {
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    graphics.Clear(Color.Transparent);
                    using (var orange = new SolidBrush(Color.FromArgb(242, 130, 61)))
                        graphics.FillEllipse(orange, 1, 1, 14, 14);
                    using (var white = new Pen(Color.White, 1.5f))
                    {
                        graphics.DrawEllipse(white, 4, 4, 8, 8);
                        graphics.DrawLine(white, 8, 4, 8, 12);
                    }
                }

                using (var handle = SafeIconHandle.Own(bitmap.GetHicon()))
                {
                    if (handle.IsInvalid) throw new InvalidOperationException("Unable to create tray icon handle.");
                    // Icon.FromHandle does not own HICON. Clone it into a managed owner before
                    // releasing the native handle returned by Bitmap.GetHicon.
                    using (var borrowed = Icon.FromHandle(handle.DangerousGetHandle()))
                        return (Icon)borrowed.Clone();
                }
            }
        }

        public void Dispose()
        {
            _store.PropertyChanged -= OnStorePropertyChanged;
            _widget?.Close();
            _manager?.Close();
            _edgeDock?.Close();
            _notify.Visible = false;
            _notify.ContextMenu = null;
            _notify.Dispose();
            _menu?.Dispose();
            _menu = null;
            _trayIcon.Dispose();
        }
    }
}
