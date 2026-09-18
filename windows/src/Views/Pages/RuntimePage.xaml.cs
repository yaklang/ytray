#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using YTray.Core;
using YTray.Models;
using YTray.Native;

namespace YTray.Views.Pages
{
    public partial class RuntimePage : Page
    {
        private sealed class RuntimeRow
        {
            public BrowserRuntime Runtime { get; set; } = null!;
            public bool IsDefault { get; set; }
            public string SourceTitle => Runtime.Source.Title();
            public ImageSource? IconSource => BrowserIconSource.FromExecutable(Runtime.ExecutablePath);
            public Visibility DefaultVisibility => IsDefault ? Visibility.Visible : Visibility.Collapsed;
            public bool IsAvailable => File.Exists(Runtime.ExecutablePath);
            public bool CanSetDefault => IsAvailable && !IsDefault;
            public Visibility InvalidVisibility => IsAvailable ? Visibility.Collapsed : Visibility.Visible;
            public Visibility RemovableVisibility => Runtime.Source == RuntimeSource.System
                ? Visibility.Collapsed : Visibility.Visible;
            public string RemoveTooltip => Runtime.Source == RuntimeSource.Managed
                ? "卸载 Chrome for Testing" : "移除此浏览器记录";
        }

        private readonly InstanceStore _store;
        private readonly ObservableCollection<MirrorVersion> _versions = new ObservableCollection<MirrorVersion>();
        private bool _subscribed;
        private bool _loadingManifest;
        private bool _refreshScheduled;
        private int _feedbackGeneration;
        private RuntimeRow? _selectedRow;

        public RuntimePage(InstanceStore store)
        {
            InitializeComponent();
            _store = store;
            VersionCombo.ItemsSource = _versions;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            Refresh();
            if (_store.AvailableVersions.Count == 0)
                CrashGuard.Observe(LoadManifestAsync(), "load-runtime-manifest");
            else RefreshVersions();
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
            ScheduleRefresh();
        }

        private void ScheduleRefresh()
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
            if (RuntimeList == null) return;
            var defaultId = _store.Settings.DefaultRuntimeID;
            // OrderByDescending is stable, so only the selected default moves to the first row;
            // every other browser keeps the discovery/install order the user already recognizes.
            var rows = _store.Runtimes.OrderByDescending(runtime => runtime.Id == defaultId).Select(runtime => new RuntimeRow
            {
                Runtime = runtime,
                IsDefault = runtime.Id == defaultId,
            }).ToList();
            RuntimeList.ItemsSource = rows;
            RuntimeEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RuntimeList.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            var selectedId = _selectedRow?.Runtime.Id;
            _selectedRow = selectedId.HasValue
                ? rows.FirstOrDefault(row => row.Runtime.Id == selectedId.Value)
                : null;
            RuntimeList.SelectedItem = _selectedRow;
            RefreshDetails();
            RuntimeCountLabel.Text = rows.Count.ToString();
            var current = _store.DefaultRuntime;
            DefaultRuntimeLabel.Text = current == null ? "尚未选择默认浏览器" : $"默认 · {current.DisplayTitle} {current.VersionLabel}";
            ProfileRootText.Text = _store.ResolveProfileRoot();
            ProfileRootButton.ToolTip = _store.ResolveProfileRoot();
            ResetProfileRootButton.Visibility = string.IsNullOrWhiteSpace(_store.Settings.ProfileRootPath)
                ? Visibility.Collapsed : Visibility.Visible;

            InstallBtn.IsEnabled = !_store.IsInstalling && VersionCombo.SelectedItem != null;
            RefreshManifestBtn.IsEnabled = !_store.IsInstalling && !_loadingManifest;
            VersionCombo.IsEnabled = !_store.IsInstalling;
            InstallBtn.Content = _store.IsInstalling ? $"安装中 {_store.InstallProgressPercent}%" : "安装此版本";

            var showProgress = _store.IsInstalling || _store.InstallProgressPercent > 0 || !string.IsNullOrEmpty(_store.ErrorMessage);
            InstallProgressPanel.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
            InstallProgressBar.Value = _store.InstallProgressPercent;
            InstallPercentLabel.Text = "";
            InstallStatus.Text = !string.IsNullOrEmpty(_store.ActivityMessage)
                ? InstallPhase(_store.ActivityMessage)
                : (_store.ErrorMessage ?? "");
            InstallStatus.Foreground = (Brush)FindResource(string.IsNullOrEmpty(_store.ErrorMessage)
                ? (_store.IsInstalling ? "TextSecondaryBrush" : "SuccessBrush")
                : "DangerBrush");
            InstallBytesLabel.Text = FormatInstallProgress(
                _store.InstallBytesReceived,
                _store.InstallBytesTotal,
                _store.InstallProgressPercent,
                showProgress);
        }

        private static string InstallPhase(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return "";
            var separator = message.IndexOf(" · ", StringComparison.Ordinal);
            return separator > 0 ? message.Substring(0, separator) : message;
        }

        internal static string FormatInstallProgress(long received, long? total, int percent, bool showProgress)
        {
            if (!showProgress) return "";
            if (received > 0 && total.HasValue && total.Value > 0)
                return $"{RuntimeInstaller.FormatBytes(received)} / {RuntimeInstaller.FormatBytes(total.Value)} ({percent}%)";
            if (received > 0)
                return $"{RuntimeInstaller.FormatBytes(received)} ({percent}%)";
            return percent > 0 ? percent + "%" : "";
        }

        private async System.Threading.Tasks.Task LoadManifestAsync()
        {
            if (_loadingManifest) return;
            _loadingManifest = true;
            RefreshManifestBtn.IsEnabled = false;
            ManifestStatus.Text = "正在获取可安装版本…";
            try
            {
                await _store.RefreshManifestAsync();
                RefreshVersions();
            }
            catch (Exception ex)
            {
                ManifestStatus.Text = ex.Message;
            }
            finally
            {
                _loadingManifest = false;
                RefreshManifestBtn.IsEnabled = true;
            }
        }

        private void RefreshVersions()
        {
            var selection = VersionCombo.SelectedItem as MirrorVersion;
            _versions.Clear();
            foreach (var version in _store.AvailableVersions.Where(RuntimeInstaller.IsCompatible).Take(20))
                _versions.Add(version);
            if (selection != null)
                VersionCombo.SelectedItem = _versions.FirstOrDefault(v => v.Version == selection.Version);
            else if (_versions.Count > 0)
                VersionCombo.SelectedIndex = 0;
            ManifestStatus.Text = _versions.Count == 0
                ? (_store.ErrorMessage ?? $"没有适用于 {RuntimeInstaller.Platform} 的可安装版本")
                : $"{_versions.Count} 个版本 · {RuntimeInstaller.Platform}";
            Refresh();
        }

        private void Rescan_Click(object sender, RoutedEventArgs e)
        {
            _store.RefreshSystemBrowsers();
            ShowFeedback("已重新扫描本机浏览器");
        }

        private void Choose_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择 Chrome 可执行文件",
                Filter = "可执行文件 (*.exe)|*.exe|所有文件|*.*",
            };
            if (dialog.ShowDialog() == true && _store.AddLocalRuntime(dialog.FileName) is BrowserRuntime runtime)
            {
                Refresh();
                ShowFeedback($"已添加 {runtime.DisplayTitle}");
            }
        }

        private void SetDefault_Click(object sender, RoutedEventArgs e)
        {
            if (!(((FrameworkElement)sender).Tag is BrowserRuntime runtime)) return;
            if (!File.Exists(runtime.ExecutablePath))
            {
                ShowFeedback("浏览器路径已经失效");
                return;
            }
            _store.SelectDefaultRuntime(runtime);
            Refresh();
            ShowFeedback($"{runtime.DisplayTitle} 已设为默认");
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            if (!(((FrameworkElement)sender).Tag is BrowserRuntime runtime)) return;
            if (runtime.Source == RuntimeSource.Managed)
            {
                if (MessageBox.Show($"卸载 Chrome for Testing {runtime.VersionLabel}？\n\n浏览器程序文件会被删除，实例历史和用户数据不会删除。",
                        "YTray", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
                ShowFeedback(_store.UninstallRuntime(runtime)
                    ? $"Chrome for Testing {runtime.VersionLabel} 已卸载"
                    : (_store.ErrorMessage ?? "卸载失败"));
            }
            else
            {
                if (MessageBox.Show($"移除 {runtime.DisplayTitle}？\n\n只会移除 YTray 中的浏览器记录，不会删除浏览器文件、实例历史或用户数据。",
                        "YTray", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
                ShowFeedback(_store.RemoveRuntime(runtime)
                    ? $"{runtime.DisplayTitle} 记录已移除"
                    : (_store.ErrorMessage ?? "移除失败"));
            }
            Refresh();
        }

        private void ChooseProfileRoot_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var path = FolderPicker.PickSingle(Window.GetWindow(this),
                    "选择浏览器实例数据的父目录（YTray 会为每个实例创建独立子目录）",
                    _store.ResolveProfileRoot());
                if (path == null) return;
                if (!_store.SetProfileRoot(path, out var error))
                {
                    MessageBox.Show(error, "无法使用数据目录", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            catch (Exception ex)
            {
                CrashGuard.Record("pick-profile-root", ex);
                MessageBox.Show("无法打开目录选择器：" + ex.Message,
                    "YTray", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Refresh();
            ShowFeedback("新实例数据位置已更新");
        }

        private void ResetProfileRoot_Click(object sender, RoutedEventArgs e)
        {
            if (!_store.SetProfileRoot(null, out var error))
            {
                MessageBox.Show(error, "无法恢复默认目录", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Refresh();
            ShowFeedback("已恢复默认数据位置");
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (!(((FrameworkElement)sender).Tag is BrowserRuntime runtime)) return;
            var directory = Path.GetDirectoryName(runtime.ExecutablePath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                ShowFeedback("浏览器所在文件夹不存在");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = directory,
                    UseShellExecute = true,
                });
                ShowFeedback($"已打开 {runtime.DisplayTitle} 所在文件夹");
            }
            catch (Exception ex)
            {
                ShowFeedback("无法打开所在文件夹：" + ex.Message);
            }
        }

        private void RuntimeSelection_Changed(object sender, SelectionChangedEventArgs e)
        {
            _selectedRow = RuntimeList.SelectedItem as RuntimeRow;
            RefreshDetails();
        }

        private void RuntimeRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is ListBoxItem item) || !item.IsSelected) return;
            for (var source = e.OriginalSource as DependencyObject; source != null;
                 source = ParentOf(source))
                if (source is Button) return;
            RuntimeList.SelectedItem = null;
            e.Handled = true;
        }

        private static DependencyObject? ParentOf(DependencyObject source)
        {
            if (source is FrameworkContentElement content) return content.Parent;
            return (source as FrameworkElement)?.Parent ?? VisualTreeHelper.GetParent(source);
        }

        private void RefreshDetails()
        {
            if (DetailBrowser == null) return;
            var runtime = _selectedRow?.Runtime;
            DetailBrowser.Text = runtime?.DisplayTitle ?? "—";
            DetailSource.Text = runtime?.Source.Title() ?? "—";
            DetailVersion.Text = runtime == null ? "—" : $"{runtime.VersionLabel} ({runtime.Architecture})";
            DetailPath.Text = runtime?.ExecutablePath ?? "—";
            DetailCommand.Text = runtime == null ? "—" : $"\"{runtime.ExecutablePath}\" --remote-debugging-port={_store.Settings.DebugPort}";
            var show = runtime == null ? Visibility.Collapsed : Visibility.Visible;
            RuntimeDetails.Visibility = show;
            DetailsGapRow.Height = show == Visibility.Visible ? new GridLength(13) : new GridLength(0);
            DetailsRow.Height = show == Visibility.Visible ? new GridLength(145) : new GridLength(0);
        }

        private void OpenSelectedFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedRow == null) return;
            var proxy = new Button { Tag = _selectedRow.Runtime };
            OpenFolder_Click(proxy, e);
        }

        private async void ShowFeedback(string text)
        {
            var generation = ++_feedbackGeneration;
            DefaultFeedbackText.Text = text;
            DefaultFeedbackBorder.Visibility = Visibility.Visible;
            await System.Threading.Tasks.Task.Delay(2400);
            if (generation == _feedbackGeneration && IsLoaded)
                DefaultFeedbackBorder.Visibility = Visibility.Collapsed;
        }

        private void RefreshManifest_Click(object sender, RoutedEventArgs e) =>
            CrashGuard.Observe(LoadManifestAsync(), "refresh-runtime-manifest");
        private void VersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => Refresh();

        private void VersionCombo_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is ComboBox combo) || !combo.IsEnabled) return;
            var source = e.OriginalSource as DependencyObject;
            if (source != null && ItemsControl.ContainerFromElement(combo, source) is ComboBoxItem) return;
            var shouldOpen = !combo.IsDropDownOpen;
            combo.Focus();
            e.Handled = true;
            Dispatcher.BeginInvoke(new Action(() => combo.IsDropDownOpen = shouldOpen), DispatcherPriority.Input);
        }

        private async void Install_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!(VersionCombo.SelectedItem is MirrorVersion version)) return;
                InstallProgressPanel.Visibility = Visibility.Visible;
                await _store.InstallAsync(version);
                Refresh();
                if (string.IsNullOrEmpty(_store.ErrorMessage))
                    ShowFeedback($"Chrome for Testing {version.Version} 安装完成");
            }
            catch (Exception ex)
            {
                CrashGuard.Record("runtime-install-click", ex);
                ShowFeedback("安装失败 · " + ex.Message);
            }
        }
    }
}
