using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using YTray.Core;
using YTray.Models;
using WinForms = System.Windows.Forms;

namespace YTray.Views.Pages
{
    public partial class PluginsPage : Page
    {
        private readonly InstanceStore _store;
        private bool _subscribed;

        public PluginsPage(InstanceStore store)
        {
            InitializeComponent();
            _store = store;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            Refresh();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!_subscribed) { _store.PropertyChanged += OnStorePropertyChanged; _subscribed = true; }
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
            PluginList.ItemsSource = _store.Plugins;
            PluginCountLabel.Text = _store.Plugins.Count.ToString();
            PluginEmpty.Visibility = _store.Plugins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            PluginList.Visibility = _store.Plugins.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

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

        private void Enabled_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).Tag is BrowserPlugin plugin) _store.UpdatePlugin(plugin);
        }
    }
}
