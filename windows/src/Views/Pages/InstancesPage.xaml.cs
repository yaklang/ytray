#nullable enable
using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
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
        private ThumbnailPreviewWindow? _thumbnailPreview;
        private string? _runningSignature;
        private string? _historySignature;
        private BrowserInstancePresentation? _selected;
        private string _activeTab = "all";

        public InstancesPage(InstanceStore store)
        {
            InitializeComponent();
            _store = store;
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
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (!_subscribed) return;
            _store.PropertyChanged -= OnStorePropertyChanged;
            InstanceThumbnailImageSource.ImageLoaded -= OnThumbnailImageLoaded;
            _thumbnailPreview?.Close();
            _thumbnailPreview = null;
            _subscribed = false;
        }

        private void OnStorePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            ScheduleRefresh();
        }

        private void OnThumbnailImageLoaded(object sender, EventArgs e)
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
            RunningEmpty.Visibility = running.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            HistoryEmpty.Visibility = history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RunningHeading.Text = $"运行中（{running.Count}）";
            HistoryHeading.Text = $"历史（{history.Count}）";
            var showRunning = _activeTab != "history";
            RunningHeading.Visibility = showRunning ? Visibility.Visible : Visibility.Collapsed;
            RunningList.Visibility = showRunning && running.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            RunningEmpty.Visibility = showRunning && running.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            var showHistory = _activeTab != "running";
            HistoryHeadingPanel.Visibility = showHistory ? Visibility.Visible : Visibility.Collapsed;
            HistoryList.Visibility = showHistory && history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            HistoryEmpty.Visibility = showHistory && history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var selectedId = _selected?.Instance.Id;
            var candidate = RunningList.Items.Cast<BrowserInstancePresentation>()
                .Concat(HistoryList.Items.Cast<BrowserInstancePresentation>())
                .FirstOrDefault(item => item.Instance.Id == selectedId)
                ?? RunningList.Items.Cast<BrowserInstancePresentation>().FirstOrDefault()
                ?? HistoryList.Items.Cast<BrowserInstancePresentation>().FirstOrDefault();
            if (candidate != null)
            {
                if (candidate.Instance.Status == InstanceStatus.Running)
                    RunningList.SelectedItem = candidate;
                else
                    HistoryList.SelectedItem = candidate;
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

        private void Focus_Click(object s, RoutedEventArgs e)
        {
            if (((FrameworkElement)s).Tag is BrowserInstance i) _store.Focus(i);
        }

        private async void Capture_Click(object s, RoutedEventArgs e)
        {
            try
            {
                if (!(((FrameworkElement)s).Tag is BrowserInstance i)) return;
                ShowActionFeedback("正在截取当前页面…", false, keepVisible: true);
                var output = await _store.CaptureAsync(i);
                if (!string.IsNullOrWhiteSpace(output))
                    ShowActionFeedback($"截图已保存 · {output}", false);
                else
                    ShowActionFeedback(_store.ErrorMessage ?? "截图失败，请确认浏览器仍在运行", true);
            }
            catch (Exception ex)
            {
                CrashGuard.Record("instances-capture-click", ex);
                ShowActionFeedback("截图失败 · " + ex.Message, true);
            }
        }
        private void Reveal_Click(object s, RoutedEventArgs e)
        {
            if (((FrameworkElement)s).Tag is BrowserInstance i) _store.RevealProfile(i);
        }
        private async void Stop_Click(object s, RoutedEventArgs e)
        {
            try
            {
                if (!(((FrameworkElement)s).Tag is BrowserInstance i)) return;
                ShowActionFeedback($"正在停止 {i.Name}…", false, keepVisible: true);
                var stopped = await _store.StopAsync(i);
                ShowActionFeedback(stopped ? $"{i.Name} 已停止并移入历史" : (_store.ErrorMessage ?? "停止失败"), !stopped);
            }
            catch (Exception ex)
            {
                CrashGuard.Record("instances-stop-click", ex);
                ShowActionFeedback("停止失败 · " + ex.Message, true);
            }
        }
        private void Restore_Click(object s, RoutedEventArgs e)
        {
            if (((FrameworkElement)s).Tag is BrowserInstance i) _store.RestoreHistory(i);
        }
        private void DeleteHistory_Click(object s, RoutedEventArgs e)
        {
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
            button.SetResourceReference(BackgroundProperty, selected ? "BrandPaleBrush" : "SurfaceBrush");
            button.SetResourceReference(Control.BorderBrushProperty, selected ? "BrandOrangeBrush" : "HairlineBrush");
            button.SetResourceReference(ForegroundProperty, selected ? "BrandOrangeBrush" : "TextPrimaryBrush");
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
            if (DetailRuntime == null) return;
            if (selected == null)
            {
                DetailRuntime.Text = DetailName.Text = DetailAumid.Text = DetailProfile.Text = DetailVersion.Text =
                    DetailProxy.Text = DetailDebug.Text = DetailPlugins.Text = "—";
                DetailThumbnail.Source = null;
                DetailPreviewEmpty.Visibility = Visibility.Visible;
                CaptureDetailButton.IsEnabled = StopDetailButton.IsEnabled = false;
                return;
            }
            DetailRuntime.Text = selected.RuntimeTitle;
            DetailName.Text = selected.Name;
            DetailAumid.Text = selected.AppUserModelIdText;
            DetailProfile.Text = selected.ProfilePath;
            DetailVersion.Text = selected.RuntimeVersion;
            DetailProxy.Text = selected.NetworkMode + " " + selected.NetworkAddress;
            DetailDebug.Text = selected.DebugAddress;
            DetailPlugins.Text = selected.PluginCount;
            DetailThumbnail.Source = selected.ThumbnailSource;
            DetailPreviewEmpty.Visibility = selected.HasThumbnail ? Visibility.Collapsed : Visibility.Visible;
            var running = selected.Instance.Status == InstanceStatus.Running;
            CaptureDetailButton.IsEnabled = running && selected.CanCapture;
            StopDetailButton.IsEnabled = running && selected.CanStop;
            StopDetailButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        }

        private void FocusDetail_Click(object sender, RoutedEventArgs e) { if (_selected != null) _store.Focus(_selected.Instance); }
        private async void CaptureDetail_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            var output = await _store.CaptureAsync(_selected.Instance);
            ShowActionFeedback(output == null ? (_store.ErrorMessage ?? "截图失败") : "截图已保存 · " + output, output == null);
        }
        private void RevealDetail_Click(object sender, RoutedEventArgs e) { if (_selected != null) _store.RevealProfile(_selected.Instance); }
        private async void StopDetail_Click(object sender, RoutedEventArgs e) { if (_selected != null) await _store.StopAsync(_selected.Instance); }
        private void NewInstance_Click(object sender, RoutedEventArgs e)
        {
            var wizard = new CustomLaunchWizard(_store) { Owner = Window.GetWindow(this) };
            wizard.ShowDialog();
        }

        private void Thumbnail_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (sender is FrameworkElement anchor && anchor.Tag is BrowserInstancePresentation row
                && row.ThumbnailSource is ImageSource thumbnail)
            {
                if (_thumbnailPreview == null) _thumbnailPreview = new ThumbnailPreviewWindow();
                _thumbnailPreview.Schedule(anchor, thumbnail, row.LastPageTitle ?? row.Name);
            }
        }

        private void Thumbnail_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => _thumbnailPreview?.Dismiss();

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
