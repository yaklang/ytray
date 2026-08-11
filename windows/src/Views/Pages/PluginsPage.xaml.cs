using System.Windows;
using System.Windows.Controls;
using YTray.Core;
using YTray.Models;
using WinForms = System.Windows.Forms;

namespace YTray.Views.Pages
{
    public partial class PluginsPage : Page
    {
        private readonly InstanceStore _store;

        public PluginsPage(InstanceStore store)
        {
            InitializeComponent();
            _store = store;
            Refresh();
            _store.PropertyChanged += (s, e) => Dispatcher.Invoke(Refresh);
        }

        private void Refresh() => PluginList.ItemsSource = _store.Plugins;

        private void Add_Click(object s, RoutedEventArgs e)
        {
            using (var dlg = new WinForms.FolderBrowserDialog { Description = "选择已解压 Chrome 插件目录" })
            {
                if (dlg.ShowDialog() == WinForms.DialogResult.OK) _store.AddPlugin(dlg.SelectedPath);
            }
        }

        private void Remove_Click(object s, RoutedEventArgs e)
        {
            if (((FrameworkElement)s).Tag is BrowserPlugin p) _store.RemovePlugin(p);
            Refresh();
        }
    }
}