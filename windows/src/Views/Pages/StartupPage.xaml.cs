#nullable enable
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using YTray.Core;

namespace YTray.Views.Pages
{
    public partial class StartupPage : Page
    {
        private readonly LaunchAtLoginManager _manager;

        public StartupPage(LaunchAtLoginManager manager)
        {
            InitializeComponent();
            _manager = manager;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            Refresh();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _manager.PropertyChanged += OnManagerChanged;
            _manager.Refresh();
            Refresh();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) =>
            _manager.PropertyChanged -= OnManagerChanged;

        private void OnManagerChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!Dispatcher.CheckAccess()) Dispatcher.BeginInvoke(new Action(Refresh));
            else Refresh();
        }

        private void Refresh()
        {
            if (StatusTitle == null) return;
            StatusTitle.Text = _manager.StatusTitle;
            StatusDetail.Text = _manager.StatusDetail;
            StatusIcon.Text = _manager.IsEnabled ? "\uE7E8" : "\uE71B";
            ToggleButton.Content = _manager.IsEnabled ? "关闭开机启动" : "开启开机启动";
            ToggleButton.SetResourceReference(StyleProperty,
                _manager.IsEnabled ? "YTrayButton" : "YTrayPrimaryButton");
        }

        private void Toggle_Click(object sender, RoutedEventArgs e)
        {
            if (_manager.IsEnabled)
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

            if (!_manager.SetEnabled(!_manager.IsEnabled))
                MessageBox.Show(_manager.ErrorMessage ?? "设置失败。", "YTray", MessageBoxButton.OK, MessageBoxImage.Warning);
            Refresh();
        }
    }
}
