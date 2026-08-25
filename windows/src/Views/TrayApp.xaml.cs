#nullable enable
using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
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
        private readonly LaunchAtLoginManager _launchAtLogin;
        private readonly System.Windows.Forms.NotifyIcon _notify;
        private Icon? _trayIcon;
        private WidgetView? _widget;
        private ManagerView? _manager;
        private readonly EdgeDock _edgeDock;
        private System.Windows.Forms.ContextMenu? _menu;
        private bool _statusRefreshScheduled;
        private bool _launchAtLoginRefreshScheduled;

        public TrayApp(InstanceStore store, LaunchAtLoginManager launchAtLogin)
        {
            _store = store;
            _launchAtLogin = launchAtLogin;
            _store.PropertyChanged += OnStorePropertyChanged;
            _launchAtLogin.PropertyChanged += OnLaunchAtLoginChanged;

            _trayIcon = LoadTrayIcon();
            _notify = new System.Windows.Forms.NotifyIcon
            {
                Icon = _trayIcon,
                Visible = true,
                Text = "YTray · 左键打开主界面 · 右键打开菜单",
            };
            _notify.MouseClick += OnNotifyMouseClick;
            // Subscribe only after NotifyIcon is fully constructed. A Windows theme event can
            // arrive at any time; the handler must never observe a partially initialized owner.
            ThemeManager.ThemeChanged += OnThemeChanged;

            _edgeDock = new EdgeDock(_store, ShowWidgetBesideEdge);
            BuildMenu();
            UpdateStatusTitle();
            if (_store.Settings.EdgeDockEnabled) _edgeDock.ShowDock(remember: false);
        }

        private void OnThemeChanged(object sender, EventArgs e)
        {
            var replacement = LoadTrayIcon();
            var previous = _trayIcon;
            _trayIcon = replacement;
            _notify.Icon = replacement;
            previous?.Dispose();
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

        private void OnLaunchAtLoginChanged(object sender, PropertyChangedEventArgs e)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted || _launchAtLoginRefreshScheduled) return;
            _launchAtLoginRefreshScheduled = true;
            dispatcher.BeginInvoke(new Action(() =>
            {
                _launchAtLoginRefreshScheduled = false;
                BuildMenu();
            }), DispatcherPriority.Background);
        }

        private void BuildMenu()
        {
            _notify.ContextMenu = null;
            _menu?.Dispose();
            _menu = new System.Windows.Forms.ContextMenu();
            _menu.MenuItems.Add("无代理启动", (s, e) => _store.LaunchConfigured(false));
            _menu.MenuItems.Add("使用 HTTP 代理启动", (s, e) => _store.LaunchConfigured(true));
            _menu.MenuItems.Add("显示小组件", (s, e) => ShowWidget());
            _menu.MenuItems.Add("全部管理", (s, e) => ShowManager());
            _menu.MenuItems.Add(_launchAtLogin.IsEnabled ? "关闭开机启动…" : "开启开机启动", (s, e) => ToggleLaunchAtLogin());
            _menu.MenuItems.Add("-");
            _menu.MenuItems.Add("显示边缘小组件", (s, e) => _edgeDock.ShowDock());
            _menu.MenuItems.Add("-");
            _menu.MenuItems.Add("退出 YTray", (s, e) => Application.Current.Shutdown());
            _notify.ContextMenu = _menu;
        }

        private void UpdateStatusTitle()
        {
            var count = _store.RunningInstances.Count;
            _notify.Text = $"YTray · {count} 个运行中实例 · 左键打开主界面";
        }

        private void OnNotifyMouseClick(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            // Keep right-click exclusively for the native tray menu. A regular left-click is
            // the shortest path back to the manager after its window has been closed.
            if (e.Button != System.Windows.Forms.MouseButtons.Left) return;

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted) return;
            if (dispatcher.CheckAccess())
                ShowManager();
            else
                dispatcher.BeginInvoke(new Action(ShowManager), DispatcherPriority.Send);
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
                _manager = new ManagerView(_store, _launchAtLogin);
                _manager.Closed += OnManagerClosed;
            }
            if (!_manager.IsVisible) _manager.Show();
            if (_manager.WindowState == WindowState.Minimized) _manager.WindowState = WindowState.Normal;
            _manager.Activate();
            _manager.Focus();

            // Activate() alone is not reliable when the window is restored from a tray event.
            // The tray click is foreground user input, so Windows permits this foreground handoff.
            var handle = new WindowInteropHelper(_manager).Handle;
            if (handle != IntPtr.Zero) Win32.SetForegroundWindow(handle);
        }

        private void ToggleLaunchAtLogin()
        {
            if (_launchAtLogin.IsEnabled)
            {
                var choice = MessageBox.Show(
                    "关闭后，登录 Windows 时 YTray 将不会自动进入系统托盘。",
                    "确认关闭开机启动？",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No
                );
                if (choice != MessageBoxResult.Yes) return;
            }
            if (!_launchAtLogin.SetEnabled(!_launchAtLogin.IsEnabled))
                MessageBox.Show(_launchAtLogin.ErrorMessage ?? "设置失败。", "YTray", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void OnManagerClosed(object sender, EventArgs e)
        {
            if (sender is ManagerView manager) manager.Closed -= OnManagerClosed;
            _manager = null;
        }

        private static Icon LoadTrayIcon()
        {
            var resourceName = ThemeManager.IsDark
                ? "ytray-tray-on-dark.ico"
                : "ytray-tray-on-light.ico";
            var uri = new Uri($"pack://application:,,,/Assets/Icons/{resourceName}", UriKind.Absolute);
            var resource = Application.GetResourceStream(uri);
            if (resource == null)
                throw new FileNotFoundException("找不到系统托盘图标资源", resourceName);
            using (resource.Stream)
            using (var borrowed = new Icon(resource.Stream))
            {
                // Clone before disposing the pack resource stream: System.Drawing.Icon otherwise
                // keeps a lazy reference to the stream and can fail when Explorer repaints it.
                return (Icon)borrowed.Clone();
            }
        }

        public void Dispose()
        {
            _store.PropertyChanged -= OnStorePropertyChanged;
            _launchAtLogin.PropertyChanged -= OnLaunchAtLoginChanged;
            ThemeManager.ThemeChanged -= OnThemeChanged;
            _widget?.Close();
            _manager?.Close();
            _edgeDock?.Close();
            _notify.MouseClick -= OnNotifyMouseClick;
            _notify.Visible = false;
            _notify.ContextMenu = null;
            _notify.Dispose();
            _menu?.Dispose();
            _menu = null;
            _trayIcon?.Dispose();
            _trayIcon = null;
        }
    }
}
