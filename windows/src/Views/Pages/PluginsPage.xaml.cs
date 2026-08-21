#nullable enable
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using YTray.Core;
using YTray.Models;
using WinForms = System.Windows.Forms;

namespace YTray.Views.Pages
{
    public partial class PluginsPage : Page
    {
        private sealed class PluginRow
        {
            public BrowserPlugin Plugin { get; set; } = null!;
            public ImageSource? IconSource { get; set; }
            public string VersionLabel => $"v{Plugin.Version} · Manifest V{Plugin.ManifestVersion}";
            public bool IsManaged { get; set; }
        }

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
            var managedId = _store.ManagedExtension?.Id;
            PluginList.ItemsSource = _store.Plugins
                .OrderBy(plugin => plugin.Id == managedId ? 0 : 1)
                .ThenBy(plugin => plugin.CreatedAt)
                .Select(plugin => new PluginRow
                {
                    Plugin = plugin,
                    IconSource = PluginIconSource.FromPlugin(plugin),
                    IsManaged = plugin.Id == managedId,
                }).ToList();
            _selected = _store.Plugins.FirstOrDefault(p => p.Id == selectedId) ?? _store.Plugins.FirstOrDefault();
            PluginList.SelectedItem = PluginList.Items.Cast<PluginRow>()
                .FirstOrDefault(row => row.Plugin.Id == _selected?.Id);
            PluginCountLabel.Text = $"{_store.Plugins.Count} 个插件";
            PluginCountDot.Fill = (Brush)FindResource(_store.Plugins.Count == 0 ? "TextTertiaryBrush" : "SuccessBrush");
            PluginEmpty.Visibility = _store.Plugins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            PluginList.Visibility = _store.Plugins.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            RefreshExtensionBar();
            RefreshDetails();
        }

        private void RefreshExtensionBar()
        {
            if (ExtensionInstallBtn == null) return;
            var installed = _store.ManagedExtension;
            var hasBundledExtension = ExtensionInstaller.TryGetBundledVersion(out var bundledVersion);
            ExtensionInstallBtn.IsEnabled = hasBundledExtension;

            string status;
            if (!string.IsNullOrWhiteSpace(_store.ExtensionStatusMessage))
                status = _store.ExtensionStatusMessage;
            else if (installed != null)
                status = $"Yakit 浏览器插件 v{installed.Version} 已随 YTray 内置；运行时不会联网下载";
            else if (hasBundledExtension)
                status = $"YTray 内置 Yakit 浏览器插件 v{bundledVersion}";
            else
                status = "当前构建未包含 Yakit 浏览器插件";
            ExtensionStatusText.Text = status;
            ExtensionStatusText.ToolTip = status;
        }

        private void ExtensionInstall_Click(object sender, RoutedEventArgs e) =>
            _store.ReinstallBundledExtension();

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
            _selected = (PluginList.SelectedItem as PluginRow)?.Plugin;
            RefreshDetails();
        }

        private void RefreshDetails()
        {
            if (DetailName == null) return;
            var hasSelection = _selected != null;
            DetailContent.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
            DetailEmpty.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
            if (!hasSelection) return;
            DetailName.Text = _selected!.Name;
            DetailVersion.Text = $"v{_selected.Version} · Manifest V{_selected.ManifestVersion}";
            DetailPath.Text = _selected.Path;
            DetailPath.ToolTip = _selected.Path;
            DetailIcon.Source = PluginIconSource.FromPlugin(_selected);
            DetailState.Text = _selected.Enabled ? "已启用" : "已停用";
            DetailStateDot.Fill = (Brush)FindResource(_selected.Enabled ? "SuccessBrush" : "TextTertiaryBrush");
            var isManaged = _store.ManagedExtension?.Id == _selected.Id;
            DetailRemoveButton.Visibility = isManaged ? Visibility.Collapsed : Visibility.Visible;
            Grid.SetColumnSpan(DetailOpenFolderButton, isManaged ? 3 : 1);
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
