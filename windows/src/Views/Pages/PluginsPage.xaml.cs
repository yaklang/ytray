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
        }

        private readonly InstanceStore _store;
        private bool _subscribed;
        private bool _refreshScheduled;
        private bool _extensionManifestLoading;
        private BrowserPlugin? _selected;

        public PluginsPage(InstanceStore store)
        {
            InitializeComponent();
            _store = store;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            Refresh();
            if (_store.ExtensionManifest == null)
                CrashGuard.Observe(LoadExtensionManifestAsync(), "load-extension-manifest");
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
            PluginList.ItemsSource = _store.Plugins.Select(plugin => new PluginRow
            {
                Plugin = plugin,
                IconSource = PluginIconSource.FromPlugin(plugin),
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
            var manifest = _store.ExtensionManifest;
            var latest = manifest?.Versions.FirstOrDefault(v =>
                ExtensionInstaller.EnterpriseArtifact(v) != null);
            var busy = _store.IsInstallingExtension || _extensionManifestLoading;

            ExtensionInstallBtn.IsEnabled = !busy && latest != null;
            ExtensionRefreshBtn.IsEnabled = !_store.IsInstallingExtension;
            ExtensionUpdateBadge.Visibility = _store.IsExtensionUpdateAvailable && !busy
                ? Visibility.Visible : Visibility.Collapsed;

            string buttonLabel;
            if (_store.IsInstallingExtension) buttonLabel = $"安装中 {_store.ExtensionInstallPercent}%";
            else if (installed == null) buttonLabel = latest == null ? "下载 Yakit 插件" : $"下载 Yakit 插件 v{latest.Version}";
            else if (_store.IsExtensionUpdateAvailable && latest != null) buttonLabel = $"更新到 v{latest.Version}";
            else buttonLabel = "重新下载";
            ExtensionInstallBtn.Content = buttonLabel;

            ExtensionProgressBar.Visibility = _store.IsInstallingExtension ? Visibility.Visible : Visibility.Collapsed;
            ExtensionProgressBar.Value = _store.ExtensionInstallPercent;

            string status;
            if (_store.IsInstallingExtension)
                status = _store.ExtensionStatusMessage;
            else if (_extensionManifestLoading)
                status = "正在检查插件版本…";
            else if (installed != null && manifest != null)
                status = _store.IsExtensionUpdateAvailable
                    ? $"当前 v{installed.Version} · 最新 v{latest?.Version ?? manifest.Latest}"
                    : $"Yakit 插件 v{installed.Version} 已是最新";
            else if (manifest != null)
                status = $"Yakit 浏览器插件 v{latest?.Version ?? manifest.Latest} 可下载";
            else
                status = _store.ExtensionStatusMessage;
            ExtensionStatusText.Text = status;
            ExtensionStatusText.ToolTip = status;
        }

        private async System.Threading.Tasks.Task LoadExtensionManifestAsync()
        {
            if (_extensionManifestLoading) return;
            _extensionManifestLoading = true;
            RefreshExtensionBar();
            try { await _store.RefreshExtensionManifestAsync(); }
            finally
            {
                _extensionManifestLoading = false;
                if (IsLoaded) RefreshExtensionBar();
            }
        }

        private void ExtensionRefresh_Click(object sender, RoutedEventArgs e) =>
            CrashGuard.Observe(LoadExtensionManifestAsync(), "refresh-extension-manifest");

        private void ExtensionInstall_Click(object sender, RoutedEventArgs e) =>
            CrashGuard.Observe(_store.InstallExtensionAsync(), "install-extension");

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
            DetailIcon.Source = PluginIconSource.FromPlugin(_selected);
            DetailState.Text = _selected.Enabled ? "已启用" : "已停用";
            DetailStateDot.Fill = (Brush)FindResource(_selected.Enabled ? "SuccessBrush" : "TextTertiaryBrush");
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
