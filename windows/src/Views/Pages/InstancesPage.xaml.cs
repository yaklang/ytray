#nullable enable
using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Threading.Tasks;
using YTray.Core;
using YTray.Models;

namespace YTray.Views.Pages
{
    public partial class InstancesPage : Page
    {
        private readonly InstanceStore _store;
        private bool _subscribed;
        private bool _refreshScheduled;
        private int _feedbackGeneration;
        private string? _runningSignature;
        private string? _historySignature;
        private BrowserInstancePresentation? _selected;
        private string _activeTab = "all";
        private bool _refreshingSelection;
        private readonly DispatcherTimer _engineStatusTimer;
        private Guid? _engineInstance;
        private bool _readingEngine;
        private bool _changingEngine;
        private bool _hasActiveEngineTask;

        public InstancesPage(InstanceStore store)
        {
            InitializeComponent();
            _store = store;
            _engineStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _engineStatusTimer.Tick += async (_, __) => await RefreshEngineStatusAsync();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            Refresh();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!_subscribed)
            {
                _store.PropertyChanged += OnStorePropertyChanged;
                InstanceThumbnailImageSource.ImageLoaded += OnThumbnailImageLoaded;
                _subscribed = true;
            }
            Refresh();
            _engineStatusTimer.Start();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _engineStatusTimer.Stop();
            if (!_subscribed) return;
            _store.PropertyChanged -= OnStorePropertyChanged;
            InstanceThumbnailImageSource.ImageLoaded -= OnThumbnailImageLoaded;
            _subscribed = false;
        }

        private void OnStorePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            ScheduleRefresh();
        }

        private void OnThumbnailImageLoaded(object? sender, EventArgs e)
        {
            // The model signature does not change when a background decode completes. Invalidate
            // only the presentation snapshot so the frozen bitmap is attached without waiting for
            // another CDP/state update.
            _runningSignature = null;
            ScheduleRefresh();
        }

        private void ScheduleRefresh()
        {
            if (_refreshScheduled || !IsLoaded) return;
            _refreshScheduled = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _refreshScheduled = false;
                if (IsLoaded) Refresh();
            }), DispatcherPriority.Background);
        }

        private void Refresh()
        {
            var running = _store.RunningInstances;
            var history = _store.HistoryInstances;
            var selectedId = _selected?.Instance.Id;
            var runningSignature = InstanceSignature(running);
            var historySignature = InstanceSignature(history);
            if (_runningSignature != runningSignature)
            {
                _runningSignature = runningSignature;
                RunningList.ItemsSource = Filter(running).Select(i => new BrowserInstancePresentation(i, _store.RuntimeFor(i))).ToList();
            }
            if (_historySignature != historySignature)
            {
                _historySignature = historySignature;
                HistoryList.ItemsSource = Filter(history).Select(i => new BrowserInstancePresentation(i, _store.RuntimeFor(i))).ToList();
            }
            RunningCountLabel.Text = $"{running.Count} 运行中";
            HistoryCountLabel.Text = $"{history.Count} 历史";
            var visibleRunningCount = RunningList.Items.Count;
            var visibleHistoryCount = HistoryList.Items.Count;
            RunningHeading.Text = $"运行中（{visibleRunningCount}）";
            HistoryHeading.Text = $"历史（{visibleHistoryCount}）";
            var showRunning = _activeTab != "history";
            RunningSection.Visibility = showRunning ? Visibility.Visible : Visibility.Collapsed;
            RunningList.Visibility = visibleRunningCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            RunningEmpty.Visibility = visibleRunningCount == 0 ? Visibility.Visible : Visibility.Collapsed;
            var showHistory = _activeTab != "running";
            HistorySection.Visibility = showHistory ? Visibility.Visible : Visibility.Collapsed;
            HistorySection.Margin = showRunning ? new Thickness(0, 16, 0, 0) : new Thickness(0);
            HistoryList.Visibility = visibleHistoryCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            HistoryEmpty.Visibility = visibleHistoryCount == 0 ? Visibility.Visible : Visibility.Collapsed;

            var candidate = RunningList.Items.Cast<BrowserInstancePresentation>()
                .Concat(HistoryList.Items.Cast<BrowserInstancePresentation>())
                .FirstOrDefault(item => item.Instance.Id == selectedId);
            // Preserve an explicit empty inspector on first navigation. Once an instance was
            // selected, only keep it selected while it remains in the filtered/visible result.
            _refreshingSelection = true;
            try
            {
                if (candidate != null)
                {
                    if (candidate.Instance.Status == InstanceStatus.Running)
                        RunningList.SelectedItem = candidate;
                    else
                        HistoryList.SelectedItem = candidate;
                }
                else
                {
                    RunningList.SelectedItem = null;
                    HistoryList.SelectedItem = null;
                }
            }
            finally
            {
                _refreshingSelection = false;
            }
            SetSelected(candidate);
            UpdateTabVisuals();
        }

        private System.Collections.Generic.IEnumerable<BrowserInstance> Filter(System.Collections.Generic.IEnumerable<BrowserInstance> source)
        {
            var query = SearchBox?.Text?.Trim();
            if (string.IsNullOrWhiteSpace(query)) return source;
            return source.Where(i => (i.Name ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                || (i.LastPageTitle ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                || (i.LastPageURL ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string InstanceSignature(System.Collections.Generic.IEnumerable<BrowserInstance> instances) =>
            string.Join("|", instances.Select(i => string.Join("~",
                i.Id, i.Status, i.Name, i.LastPageTitle, i.LastPageURL, i.DockBadge,
                i.ProcessID, i.DebugPort, i.AppUserModelId, i.ThumbnailPath,
                i.ThumbnailUpdatedAt?.Ticks ?? 0, i.IsCapturing, i.IsStopping, i.PreviewError)));

        private void FocusRow_Click(object s, RoutedEventArgs e)
        {
            e.Handled = true;
            if (((FrameworkElement)s).Tag is BrowserInstance i) _store.Focus(i);
        }
        private async void StopRow_Click(object s, RoutedEventArgs e)
        {
            e.Handled = true;
            if (((FrameworkElement)s).Tag is BrowserInstance i) await _store.StopAsync(i);
        }
        private void Restore_Click(object s, RoutedEventArgs e)
        {
            e.Handled = true;
            if (((FrameworkElement)s).Tag is BrowserInstance i) _store.RestoreHistory(i);
        }
        private void DeleteHistory_Click(object s, RoutedEventArgs e)
        {
            e.Handled = true;
            if (((FrameworkElement)s).Tag is BrowserInstance i) _store.RemoveHistory(i);
        }
        private void ClearAll_Click(object s, RoutedEventArgs e) => _store.RemoveAllHistory();

        private void Tab_Click(object sender, RoutedEventArgs e)
        {
            _activeTab = ((FrameworkElement)sender).Tag?.ToString() ?? "all";
            _runningSignature = _historySignature = null;
            Refresh();
        }

        private void UpdateTabVisuals()
        {
            if (AllTab == null) return;
            SetTabState(AllTab, _activeTab == "all");
            SetTabState(RunningTab, _activeTab == "running");
            SetTabState(HistoryTab, _activeTab == "history");
        }

        private void SetTabState(Button button, bool selected)
        {
            button.SetResourceReference(BackgroundProperty, selected ? "SurfaceRaisedBrush" : "Transparent");
            if (!selected) button.Background = Brushes.Transparent;
            button.SetResourceReference(Control.BorderBrushProperty, selected ? "HairlineBrush" : "Transparent");
            if (!selected) button.BorderBrush = Brushes.Transparent;
            button.SetResourceReference(ForegroundProperty, selected ? "BrandOrangeBrush" : "TextSecondaryBrush");
        }

        private void Search_Changed(object sender, TextChangedEventArgs e)
        {
            if (SearchPlaceholder != null)
                SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            _runningSignature = _historySignature = null;
            if (IsLoaded) Refresh();
        }

        private void InstanceSelection_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_refreshingSelection) return;
            if (sender == RunningList && RunningList.SelectedItem is BrowserInstancePresentation running)
            {
                HistoryList.SelectedItem = null;
                SetSelected(running);
            }
            else if (sender == HistoryList && HistoryList.SelectedItem is BrowserInstancePresentation history)
            {
                RunningList.SelectedItem = null;
                SetSelected(history);
            }
        }

        private void SetSelected(BrowserInstancePresentation? selected)
        {
            _selected = selected;
            DetailContent.Tag = selected;
            if (DetailRuntime == null) return;
            if (selected == null)
            {
                DetailEmptyState.Visibility = Visibility.Visible;
                DetailContent.Visibility = Visibility.Collapsed;
                DetailThumbnail.Source = null;
                FocusDetailButton.IsEnabled = CaptureDetailButton.IsEnabled = RevealDetailButton.IsEnabled =
                    StopDetailButton.IsEnabled = RestoreDetailButton.IsEnabled = false;
                return;
            }
            DetailEmptyState.Visibility = Visibility.Collapsed;
            DetailContent.Visibility = Visibility.Visible;
            DetailRuntime.Text = selected.RuntimeTitle;
            DetailName.Text = selected.Name;
            DetailAumid.Text = selected.AppUserModelIdText;
            DetailProfile.Text = selected.ProfilePath;
            DetailVersion.Text = selected.RuntimeVersion;
            DetailProxy.Text = selected.UsesProxy
                ? $"{selected.NetworkBadgeLabel} · {selected.NetworkAddress}"
                : selected.NetworkBadgeLabel;
            DetailDebug.Text = selected.DebugAddress;
            DetailPlugins.Text = selected.PluginCount;
            DetailThumbnail.Source = selected.ThumbnailSource;
            DetailPreviewEmpty.Visibility = selected.HasThumbnail ? Visibility.Collapsed : Visibility.Visible;
            DetailPreviewMessage.Text = selected.PreviewMessage;
            var running = selected.Instance.Status == InstanceStatus.Running;
            FocusDetailButton.IsEnabled = running;
            CaptureDetailButton.IsEnabled = running && selected.CanCapture;
            RevealDetailButton.IsEnabled = true;
            StopDetailButton.IsEnabled = running && selected.CanStop;
            StopDetailButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            RestoreDetailButton.IsEnabled = !running;
            RestoreDetailButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
            var instance = selected.Instance;
            EngineConnectionActions.Visibility = running && !_store.CanManageBrowserEngine(instance) ? Visibility.Collapsed : Visibility.Visible;
            ApplyEngineButton.Content = running ? "应用" : "接入并恢复";
            if (_engineInstance != instance.Id)
            {
                _engineInstance = instance.Id;
                _hasActiveEngineTask = false;
                var saved = instance.SettingsSnapshot?.EngineTarget;
                EnginePicker.Configure(saved?.Mode == "auto" ? null : saved, allowAuto: false);
                EngineConnectionStatus.Text = instance.EngineTargetSummary;
                if (running) _ = RefreshEngineStatusAsync();
            }
        }

        private void RoundedPreview_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!(sender is FrameworkElement preview) || preview.ActualWidth <= 0 || preview.ActualHeight <= 0) return;
            preview.Clip = new RectangleGeometry(
                new Rect(0, 0, preview.ActualWidth, preview.ActualHeight), 7, 7);
        }

        private void FocusDetail_Click(object sender, RoutedEventArgs e) { if (_selected != null) _store.Focus(_selected.Instance); }
        private async void CaptureDetail_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_selected == null) return;
                ShowActionFeedback("正在截取当前页面…", false, keepVisible: true);
                var output = await _store.CaptureAsync(_selected.Instance);
                ShowActionFeedback(output == null ? (_store.ErrorMessage ?? "截图失败") : "截图已保存 · " + output, output == null);
            }
            catch (Exception ex)
            {
                CrashGuard.Record("instances-detail-capture", ex);
                ShowActionFeedback("截图失败 · " + ex.Message, true);
            }
        }
        private void RevealDetail_Click(object sender, RoutedEventArgs e) { if (_selected != null) _store.RevealProfile(_selected.Instance); }
        private async void StopDetail_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_selected == null) return;
                var instance = _selected.Instance;
                ShowActionFeedback($"正在停止 {instance.Name}…", false, keepVisible: true);
                var stopped = await _store.StopAsync(instance);
                ShowActionFeedback(stopped ? $"{instance.Name} 已停止并移入历史" : (_store.ErrorMessage ?? "停止失败"), !stopped);
            }
            catch (Exception ex)
            {
                CrashGuard.Record("instances-detail-stop", ex);
                ShowActionFeedback("停止失败 · " + ex.Message, true);
            }
        }
        private void RestoreDetail_Click(object sender, RoutedEventArgs e) { if (_selected != null) _store.RestoreHistory(_selected.Instance); }
        private async Task RefreshEngineStatusAsync()
        {
            var instance = _selected?.Instance;
            if (instance == null || _readingEngine || _changingEngine || !_store.CanManageBrowserEngine(instance)) return;
            var selected = instance.SettingsSnapshot?.EngineTarget;
            if (selected?.Mode == "none" || selected?.Mode == "auto")
            {
                EngineConnectionStatus.Text = instance.EngineTargetSummary;
                return;
            }
            _readingEngine = true;
            try
            {
                var result = await _store.BrowserEngineConnectionAsync(instance);
                if (_selected?.Instance != instance || _changingEngine) return;
                var status = result["status"];
                var pairing = result["pairing"];
                var state = (string?)status?["state"];
                _hasActiveEngineTask = !string.IsNullOrEmpty((string?)status?["taskId"]);
                var name = selected?.DisplayName ?? "原有连接设置";
                EngineConnectionStatus.Text = state == "connected" ? "已接入 " + name
                    : (string?)pairing?["state"] == "pending" ? "请在 " + name + " 中确认配对"
                    : state == "connecting" ? "正在连接 " + name
                    : (string?)status?["message"] ?? instance.EngineTargetSummary;
            }
            catch (Exception error)
            {
                if (_selected?.Instance == instance && !_changingEngine) EngineConnectionStatus.Text = error.Message;
            }
            finally { _readingEngine = false; }
        }

        private async void ApplyEngine_Click(object sender, RoutedEventArgs e)
        {
            if (_selected?.Instance is not BrowserInstance instance || EnginePicker.Target is not BrowserEngineTarget target || _changingEngine) return;
            if (instance.Status != InstanceStatus.Running)
            {
                _store.RestoreHistory(instance, target);
                return;
            }
            if (_hasActiveEngineTask && MessageBox.Show(Window.GetWindow(this), "切换将结束此浏览器在原应用中的连接任务。",
                "切换接入应用", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
            _changingEngine = true;
            ApplyEngineButton.IsEnabled = false;
            try
            {
                await _store.BrowserEngineConnectionAsync(instance, target);
                if (_selected?.Instance == instance) EngineConnectionStatus.Text = target.Mode == "none" ? "仅浏览器" : "正在连接 " + target.DisplayName;
            }
            catch (Exception error)
            {
                if (_selected?.Instance == instance) EngineConnectionStatus.Text = error.Message;
            }
            finally
            {
                _changingEngine = false;
                ApplyEngineButton.IsEnabled = true;
            }
        }
        private void NewInstance_Click(object sender, RoutedEventArgs e)
        {
            var wizard = new CustomLaunchWizard(_store) { Owner = Window.GetWindow(this) };
            wizard.ShowDialog();
        }

        private async void ShowActionFeedback(string text, bool isError, bool keepVisible = false)
        {
            var generation = ++_feedbackGeneration;
            ActionFeedbackText.Text = text;
            ActionFeedbackText.ToolTip = text;
            ActionFeedbackText.Foreground = (Brush)FindResource(isError ? "DangerBrush" : "TextSecondaryBrush");
            ActionFeedbackBorder.Visibility = Visibility.Visible;
            if (keepVisible) return;
            await System.Threading.Tasks.Task.Delay(3200);
            if (generation == _feedbackGeneration && IsLoaded)
                ActionFeedbackBorder.Visibility = Visibility.Collapsed;
        }
    }
}
