using System;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using YTray.Core;
using YTray.Views;

namespace YTray
{
    /// <summary>
    /// System tray integration: NotifyIcon + context menu + widget popup.
    /// WPF has no native tray, so we interop with WinForms NotifyIcon.
    /// </summary>
    public class TrayApp : IDisposable
    {
        private readonly InstanceStore _store;
        private readonly System.Windows.Forms.NotifyIcon _notify;
        private WidgetView _widget;
        private ManagerView _manager;
        private EdgeDock _edgeDock;
        private System.Windows.Forms.ContextMenu _menu;

        public TrayApp(InstanceStore store)
        {
            _store = store;
            _store.PropertyChanged += (s, e) => UpdateStatusTitle();

            _notify = new System.Windows.Forms.NotifyIcon
            {
                Icon = CreateTrayIcon(),
                Visible = true,
                Text = "YTray · 左键打开小组件 / 右键菜单",
            };
            _notify.MouseClick += (s, e) =>
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Left) ToggleWidget();
            };
            BuildMenu();
            UpdateStatusTitle();
            // Show the edge dock (screen-edge tab, mirrors macOS EdgeDock).
            _edgeDock = new EdgeDock(_store, () => ShowManager());
            _edgeDock.ShowDock();
        }

        private void BuildMenu()
        {
            _menu = new System.Windows.Forms.ContextMenu();
            _menu.MenuItems.Add("无代理启动", (s, e) => _store.LaunchConfigured(false));
            _menu.MenuItems.Add("使用 HTTP 代理启动", (s, e) => _store.LaunchConfigured(true));
            _menu.MenuItems.Add("显示小组件", (s, e) => ShowWidget());
            _menu.MenuItems.Add("全部管理", (s, e) => ShowManager());
            _menu.MenuItems.Add("-");
            _menu.MenuItems.Add("显示边缘小组件", (s, e) => _edgeDock?.ShowDock());
            _menu.MenuItems.Add("-");
            _menu.MenuItems.Add("退出 YTray", (s, e) => Application.Current.Shutdown());
            _notify.ContextMenu = _menu;
        }

        private void UpdateStatusTitle()
        {
            var count = _store.RunningInstances.Count;
            _notify.Text = $"YTray · {count} 个运行中实例";
        }

        public void ToggleWidget()
        {
            if (_widget != null && _widget.IsVisible) { _widget.Hide(); return; }
            ShowWidget();
        }

        public void ShowWidget()
        {
            if (_widget == null)
            {
                _widget = new WidgetView(_store) { ShowActivated = false };
                _widget.OpenManagerRequested += (s, e) => ShowManager();
            }
            _widget.Show();
            _widget.PositionNearCursor();
            _widget.Activate();
        }

        public void ShowManager()
        {
            if (_manager == null)
            {
                _manager = new ManagerView(_store);
                _manager.Closed += (s, e) => _manager = null;
            }
            _manager.Show();
            _manager.Activate();
        }

        private Icon CreateTrayIcon()
        {
            // Simple tray glyph: a 16x16 black "chrome + plus" circle, matching macOS TrayIconRenderer concept.
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(System.Drawing.Color.Transparent);
                using (var b = new SolidBrush(System.Drawing.Color.Black))
                    g.FillEllipse(b, 1, 1, 14, 14);
                // plus badge
                g.FillEllipse(new SolidBrush(System.Drawing.Color.Transparent), 9, 9, 6, 6);
                using (var b = new SolidBrush(System.Drawing.Color.Black))
                    g.FillEllipse(b, 10, 10, 5, 5);
            }
            var handle = bmp.GetHicon();
            return Icon.FromHandle(handle);
        }

        public void Dispose()
        {
            _edgeDock?.Hide();
            _notify.Visible = false;
            _notify.Dispose();
        }
    }
}