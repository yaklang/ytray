#nullable enable
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        private bool _refreshScheduled;
        private BrowserPlugin? _selected;

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
            if (!IsLoaded || _refreshScheduled) return;
            _refreshScheduled = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _refreshScheduled = false;
                if (IsLoaded) Refresh();
            }), DispatcherPriority.Background);
        }

        private void Refresh()
        {
            var selectedId = _selected?.Id;
            PluginList.ItemsSource = _store.Plugins;
            _selected = _store.Plugins.FirstOrDefault(p => p.Id == selectedId) ?? _store.Plugins.FirstOrDefault();
            PluginList.SelectedItem = _selected;
            PluginCountLabel.Text = $"{_store.Plugins.Count} 个插件";
            PluginEmpty.Visibility = _store.Plugins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            PluginList.Visibility = _store.Plugins.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            RefreshDetails();
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

        private void PluginSelection_Changed(object sender, SelectionChangedEventArgs e)
        {
            _selected = PluginList.SelectedItem as BrowserPlugin;
            RefreshDetails();
        }

        private void RefreshDetails()
        {
            if (DetailName == null) return;
            DetailName.Text = _selected?.Name ?? "—";
            DetailVersion.Text = _selected == null ? "—" : $"v{_selected.Version} · Manifest V{_selected.ManifestVersion}";
            DetailPath.Text = _selected?.Path ?? "—";
            DetailState.Text = _selected == null ? "未选择" : (_selected.Enabled ? "已启用" : "已停用");
            DetailStateDot.Opacity = _selected?.Enabled == true ? 1 : 0.35;
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).Tag is BrowserPlugin plugin) OpenFolder(plugin);
        }

        private void OpenSelectedFolder_Click(object sender, RoutedEventArgs e) { if (_selected != null) OpenFolder(_selected); }
        private void RemoveSelected_Click(object sender, RoutedEventArgs e) { if (_selected != null) _store.RemovePlugin(_selected); Refresh(); }

        private static void OpenFolder(BrowserPlugin plugin)
        {
            if (plugin == null || !Directory.Exists(plugin.Path)) return;
            try { Process.Start(new ProcessStartInfo { FileName = plugin.Path, UseShellExecute = true }); } catch { }
        }
    }
}
