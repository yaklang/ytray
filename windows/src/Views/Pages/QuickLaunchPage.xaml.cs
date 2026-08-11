using System.Linq;
using System.Windows;
using System.Windows.Controls;
using YTray.Core;
using YTray.Models;

namespace YTray.Views.Pages
{
    public partial class QuickLaunchPage : Page
    {
        private readonly InstanceStore _store;

        public QuickLaunchPage(InstanceStore store)
        {
            InitializeComponent();
            _store = store;
            Refresh();
            _store.PropertyChanged += (s, e) => Dispatcher.Invoke(Refresh);
        }

        private void Refresh()
        {
            if (RuntimeLabel == null) return;
            var rt = _store.DefaultRuntime;
            RuntimeLabel.Text = rt != null ? $"{rt.DisplayTitle} {rt.VersionLabel} · {rt.Source.Title()}" : "未设置";
            HomeUrlLabel.Text = _store.Settings.HomeURL;
            DebugPortLabel.Text = $"127.0.0.1:{_store.Settings.DebugPort} 起自动避让";
            PluginLabel.Text = $"{_store.Settings.DefaultPluginIDs.Count} 个";
            DirectBtn.IsEnabled = ProxyBtn.IsEnabled = !_store.IsLaunching;
        }

        private void Direct_Click(object s, RoutedEventArgs e) => _store.LaunchConfigured(false);
        private void Proxy_Click(object s, RoutedEventArgs e) => _store.LaunchConfigured(true);
        private void Wizard_Click(object s, RoutedEventArgs e)
        {
            var wiz = new CustomLaunchWizard(_store) { Owner = Window.GetWindow(this) };
            wiz.ShowDialog();
        }
    }
}