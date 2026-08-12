using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using YTray.Core;
using YTray.Models;

namespace YTray.Views.Pages
{
    public partial class QuickLaunchPage : Page
    {
        private readonly InstanceStore _store;
        private bool _subscribed;

        public QuickLaunchPage(InstanceStore store)
        {
            InitializeComponent();
            _store = store;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            Refresh();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!_subscribed)
            {
                _store.PropertyChanged += OnStorePropertyChanged;
                _subscribed = true;
            }
            Refresh();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (!_subscribed) return;
            _store.PropertyChanged -= OnStorePropertyChanged;
            _subscribed = false;
        }

        private void OnStorePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!IsLoaded) return;
            if (Dispatcher.CheckAccess()) Refresh();
            else Dispatcher.BeginInvoke(new Action(Refresh), DispatcherPriority.Background);
        }

        private void Refresh()
        {
            if (RuntimeLabel == null) return;
            var rt = _store.DefaultRuntime;
            RuntimeLabel.Text = rt != null ? $"{rt.DisplayTitle} {rt.VersionLabel}" : "未设置";
            RuntimeIcon.Source = rt == null ? null : BrowserIconSource.FromExecutable(rt.ExecutablePath);
            HomeUrlLabel.Text = _store.Settings.HomeURL;
            DebugPortLabel.Text = $"127.0.0.1:{_store.Settings.DebugPort} 起";
            PluginLabel.Text = $"{_store.Settings.DefaultPluginIDs.Count} 个";
            ProxySummaryLabel.Text = _store.Settings.PresetProxyServer;
            DirectBtn.IsEnabled = ProxyBtn.IsEnabled = WizardBtn.IsEnabled = !_store.IsLaunching;

            LaunchStatusLabel.Text = _store.IsLaunching
                ? (string.IsNullOrEmpty(_store.LaunchMessage) ? "正在启动" : _store.LaunchMessage)
                : "准备就绪";
            LaunchStatusDot.Fill = _store.IsLaunching
                ? (Brush)FindResource("BrandOrangeBrush")
                : (Brush)FindResource("SuccessBrush");
            LaunchStatusLabel.Foreground = LaunchStatusDot.Fill;
        }

        private void Direct_Click(object sender, RoutedEventArgs e) => _store.LaunchConfigured(false);
        private void Proxy_Click(object sender, RoutedEventArgs e) => _store.LaunchConfigured(true);

        private void Wizard_Click(object sender, RoutedEventArgs e)
        {
            var wizard = new CustomLaunchWizard(_store) { Owner = Window.GetWindow(this) };
            wizard.ShowDialog();
        }
    }
}
