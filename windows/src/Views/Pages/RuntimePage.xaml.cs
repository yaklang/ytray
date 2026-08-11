using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using YTray.Core;
using YTray.Models;

namespace YTray.Views.Pages
{
    public partial class RuntimePage : Page
    {
        private readonly InstanceStore _store;
        private ObservableCollection<MirrorVersion> _versions = new ObservableCollection<MirrorVersion>();

        public RuntimePage(InstanceStore store)
        {
            InitializeComponent();
            _store = store;
            VersionCombo.ItemsSource = _versions;
            Refresh();
            _store.PropertyChanged += (s, e) => Dispatcher.Invoke(Refresh);
            _ = LoadManifestAsync();
        }

        private void Refresh()
        {
            RuntimeList.ItemsSource = _store.Runtimes;
            InstallBtn.IsEnabled = !_store.IsInstalling && VersionCombo.SelectedItem != null;
        }

        private async System.Threading.Tasks.Task LoadManifestAsync()
        {
            try
            {
                await _store.RefreshManifestAsync();
                _versions.Clear();
                foreach (var v in _store.AvailableVersions.Take(20))
                    _versions.Add(v);
            }
            catch { }
        }

        private void Rescan_Click(object s, RoutedEventArgs e) => _store.RefreshSystemBrowsers();

        private void Choose_Click(object s, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Title = "选择 Chrome 可执行文件", Filter = "可执行文件 (*.exe)|*.exe|所有文件|*.*" };
            if (dlg.ShowDialog() == true)
            {
                if (_store.AddLocalRuntime(dlg.FileName) != null)
                    Refresh();
            }
        }

        private void SetDefault_Click(object s, RoutedEventArgs e)
        {
            if (((FrameworkElement)s).Tag is BrowserRuntime rt) _store.SelectDefaultRuntime(rt);
        }

        private void RefreshManifest_Click(object s, RoutedEventArgs e) => _ = LoadManifestAsync();

        private void Install_Click(object s, RoutedEventArgs e)
        {
            if (VersionCombo.SelectedItem is MirrorVersion v)
            {
                InstallSpinner.Visibility = Visibility.Visible;
                InstallBtn.IsEnabled = false;
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    await _store.InstallAsync(v);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        InstallSpinner.Visibility = Visibility.Collapsed;
                        InstallBtn.IsEnabled = true;
                        InstallStatus.Text = _store.IsInstalling ? "" : (_store.ErrorMessage ?? $"已安装 {v.Version}");
                        Refresh();
                    });
                });
            }
        }
    }
}