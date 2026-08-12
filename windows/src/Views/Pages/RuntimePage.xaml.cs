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
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using YTray.Core;
using YTray.Models;

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
        }

        private readonly InstanceStore _store;
        private readonly ObservableCollection<MirrorVersion> _versions = new ObservableCollection<MirrorVersion>();
        private bool _subscribed;
        private bool _loadingManifest;
        private bool _refreshScheduled;
        private int _feedbackGeneration;

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
            RuntimeCountLabel.Text = rows.Count.ToString();
            var current = _store.DefaultRuntime;
            DefaultRuntimeLabel.Text = current == null ? "尚未选择默认浏览器" : $"默认 · {current.DisplayTitle} {current.VersionLabel}";

            InstallBtn.IsEnabled = !_store.IsInstalling && VersionCombo.SelectedItem != null;
            RefreshManifestBtn.IsEnabled = !_store.IsInstalling && !_loadingManifest;
            VersionCombo.IsEnabled = !_store.IsInstalling;
            InstallBtn.Content = _store.IsInstalling ? $"安装中 {_store.InstallProgressPercent}%" : "安装此版本";

            var showProgress = _store.IsInstalling || _store.InstallProgressPercent > 0 || !string.IsNullOrEmpty(_store.ErrorMessage);
            InstallProgressPanel.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
            InstallProgressBar.Value = _store.InstallProgressPercent;
            InstallPercentLabel.Text = _store.IsInstalling || _store.InstallProgressPercent > 0
                ? _store.InstallProgressPercent + "%" : "";
            InstallStatus.Text = !string.IsNullOrEmpty(_store.ActivityMessage)
                ? _store.ActivityMessage
                : (_store.ErrorMessage ?? "");
            InstallStatus.Foreground = (Brush)FindResource(string.IsNullOrEmpty(_store.ErrorMessage)
                ? (_store.IsInstalling ? "TextSecondaryBrush" : "SuccessBrush")
                : "DangerBrush");
            InstallBytesLabel.Text = _store.InstallBytesReceived <= 0 ? "" : _store.InstallBytesTotal.HasValue
                ? $"{RuntimeInstaller.FormatBytes(_store.InstallBytesReceived)} / {RuntimeInstaller.FormatBytes(_store.InstallBytesTotal.Value)}"
                : RuntimeInstaller.FormatBytes(_store.InstallBytesReceived);
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
            _store.SelectDefaultRuntime(runtime);
            Refresh();
            ShowFeedback($"{runtime.DisplayTitle} 已设为默认");
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
