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
                RunningList.ItemsSource = running.Select(i => new BrowserInstancePresentation(i)).ToList();
            }
            if (_historySignature != historySignature)
            {
                _historySignature = historySignature;
                HistoryList.ItemsSource = history;
            }
            RunningCountLabel.Text = $"{running.Count} 运行中";
            HistoryCountLabel.Text = $"{history.Count} 历史";
            RunningEmpty.Visibility = running.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            HistoryEmpty.Visibility = history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
