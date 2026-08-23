#nullable enable
using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using YTray.Core;
using YTray.Models;

namespace YTray.Views
{
    public partial class WidgetView : Window
    {
        private readonly InstanceStore _store;
        private readonly LaunchAtLoginManager _launchAtLogin;
        private bool _refreshScheduled;
        private int _dismissGeneration;
        private int _actionFeedbackGeneration;
        private readonly ThumbnailPreviewWindow _thumbnailPreview = new ThumbnailPreviewWindow();
        private string? _runningSignature;
        private string? _historySignature;

        public event EventHandler? OpenManagerRequested;

        public WidgetView(InstanceStore store, LaunchAtLoginManager launchAtLogin)
        {
            InitializeComponent();
            _store = store;
            _launchAtLogin = launchAtLogin;
            Loaded += (s, e) => Refresh();
            Deactivated += OnDeactivated;
            Closed += (s, e) =>
            {
                _store.PropertyChanged -= OnStorePropertyChanged;
                _launchAtLogin.PropertyChanged -= OnLaunchAtLoginChanged;
                InstanceThumbnailImageSource.ImageLoaded -= OnThumbnailImageLoaded;
                _thumbnailPreview.Close();
            };
            _store.PropertyChanged += OnStorePropertyChanged;
            _launchAtLogin.PropertyChanged += OnLaunchAtLoginChanged;
            InstanceThumbnailImageSource.ImageLoaded += OnThumbnailImageLoaded;
        }

        private void OnLaunchAtLoginChanged(object sender, PropertyChangedEventArgs e) => ScheduleRefresh();

        private void OnThumbnailImageLoaded(object sender, EventArgs e)
        {
            _runningSignature = null;
            ScheduleRefresh();
        }

        private void OnStorePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            ScheduleRefresh();
        }

        private void ScheduleRefresh()
        {
            if (_refreshScheduled || !IsLoaded) return;
            _refreshScheduled = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _refreshScheduled = false;
                if (IsVisible) Refresh();
            }), DispatcherPriority.Background);
        }

        private void OnDeactivated(object sender, EventArgs e)
        {
            var generation = ++_dismissGeneration;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (generation == _dismissGeneration && !IsActive && IsVisible) HideWidget();
            }), DispatcherPriority.Background);
        }

        public void PrepareToShow()
        {
            _dismissGeneration++;
            Opacity = 0;
            EntranceTransform.Y = 7;
        }

        public void PlayEntrance()
        {
            _dismissGeneration++;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(135))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
            EntranceTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(7, 0, TimeSpan.FromMilliseconds(155))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                });
        }

        public void CancelPendingDismiss() => _dismissGeneration++;

        public void HideWidget()
        {
            _dismissGeneration++;
            _thumbnailPreview.Dismiss();
            Hide();
        }

        public void RefreshAndMeasure()
        {
            Refresh();
            UpdateLayout();
        }

        public void PositionNearCursor()
        {
            var cursor = System.Windows.Forms.Cursor.Position;
            var screen = System.Windows.Forms.Screen.FromPoint(cursor);
            var scale = GetDpiScale();
            var work = screen.WorkingArea;
            var workLeft = work.Left / scale.DpiScaleX;
            var workTop = work.Top / scale.DpiScaleY;
            var workRight = work.Right / scale.DpiScaleX;
            var workBottom = work.Bottom / scale.DpiScaleY;
            var cursorX = cursor.X / scale.DpiScaleX;
            var cursorY = cursor.Y / scale.DpiScaleY;

            Left = cursorX - ActualWidth / 2;
            Top = cursorY - ActualHeight - 12;
            Left = Math.Max(workLeft + 8, Math.Min(Left, workRight - ActualWidth - 8));
            Top = Math.Max(workTop + 8, Math.Min(Top, workBottom - ActualHeight - 8));
        }

        public void PositionBeside(Window anchor, bool onLeft)
        {
            var handle = new WindowInteropHelper(anchor).Handle;
            var screen = System.Windows.Forms.Screen.FromHandle(handle);
            var scale = VisualTreeHelper.GetDpi(anchor);
            var work = screen.WorkingArea;
            var workLeft = work.Left / scale.DpiScaleX;
            var workTop = work.Top / scale.DpiScaleY;
            var workRight = work.Right / scale.DpiScaleX;
            var workBottom = work.Bottom / scale.DpiScaleY;

            var preferredX = onLeft ? anchor.Left + anchor.ActualWidth + 7 : anchor.Left - ActualWidth - 7;
            var preferredY = anchor.Top + anchor.ActualHeight / 2 - ActualHeight / 2;
            Left = Math.Max(workLeft + 8, Math.Min(preferredX, workRight - ActualWidth - 8));
            Top = Math.Max(workTop + 8, Math.Min(preferredY, workBottom - ActualHeight - 8));
        }

        private DpiScale GetDpiScale()
        {
            try { return VisualTreeHelper.GetDpi(this); }
            catch { return new DpiScale(1, 1); }
        }

        private void Refresh()
        {
            if (!IsLoaded) return;

            var running = _store.RunningInstances;
            var history = _store.HistoryInstances;
            var visibleRunning = running.Take(4).ToList();
            var visibleHistory = history.Take(4).ToList();
            var runningSignature = InstanceSignature(visibleRunning);
            var historySignature = InstanceSignature(visibleHistory);
            if (_runningSignature != runningSignature)
            {
                _runningSignature = runningSignature;
                RunningList.ItemsSource = visibleRunning.Select(i => new BrowserInstancePresentation(i)).ToList();
            }
            if (_historySignature != historySignature)
            {
                _historySignature = historySignature;
                HistoryList.ItemsSource = visibleHistory;
            }
            RunningCountLabel.Text = running.Count.ToString();
            HistoryCountLabel.Text = history.Count.ToString();
            RunningEmpty.Visibility = running.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            HistoryEmpty.Visibility = history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var runtime = _store.DefaultRuntime;
            DefaultRuntimeLabel.Text = runtime != null
                ? $"默认 · {runtime.DisplayTitle} {runtime.VersionLabel}"
                : "未选择默认浏览器";
            SchemeCombo.SelectedIndex = (int)_store.Settings.PresetProxyScheme;
            HostBox.Text = _store.Settings.PresetProxyHost;
            PortBox.Text = _store.Settings.PresetProxyPort.ToString();
            RemarkBox.Text = _store.Settings.PresetProxyRemark;
            if (!string.IsNullOrEmpty(_store.ProxyCheckMessage))
                ProxyStatus.Text = _store.ProxyCheckMessage;
            CheckBtn.IsEnabled = _store.ProxyCheckPhase != ProxyCheckPhase.Checking;
            DirectBtn.IsEnabled = ProxyBtn.IsEnabled = !_store.IsLaunching;
            StartupIcon.Text = _launchAtLogin.IsEnabled ? "\uE7E8" : "\uE71B";
            StartupLabel.Text = _launchAtLogin.IsEnabled ? "开机启动已开" : "开启开机启动";
        }

        private static string InstanceSignature(System.Collections.Generic.IEnumerable<BrowserInstance> instances) =>
            string.Join("|", instances.Select(i => string.Join("~",
                i.Id, i.Status, i.Name, i.LastPageTitle, i.LastPageURL, i.DockBadge,
                i.ProcessID, i.DebugPort, i.ThumbnailPath, i.ThumbnailUpdatedAt?.Ticks ?? 0,
                i.IsCapturing, i.IsStopping, i.PreviewError)));

        private bool CommitProxyEditor()
        {
            if (!int.TryParse(PortBox.Text?.Trim(), out var port) || port < 1 || port > 65535)
            {
                ProxyStatus.Text = "请输入 1–65535 的端口";
                ProxyStatus.Foreground = (Brush)FindResource("DangerBrush");
                return false;
            }

            _store.Settings.PresetProxyScheme = SchemeCombo.SelectedIndex == 1 ? ProxyScheme.Https : ProxyScheme.Http;
            _store.Settings.PresetProxyHost = HostBox.Text?.Trim() ?? "";
            _store.Settings.PresetProxyPort = port;
            _store.Settings.PresetProxyRemark = RemarkBox.Text?.Trim() ?? "";
            ProxyStatus.Foreground = (Brush)FindResource("WidgetTextSecondaryBrush");
            return true;
        }

        private void DirectLaunch_Click(object sender, RoutedEventArgs e) => _store.LaunchConfigured(false);
        private void ProxyLaunch_Click(object sender, RoutedEventArgs e)
        {
            if (!CommitProxyEditor()) return;
            _store.LaunchConfigured(true);
        }

        private void OpenManager_Click(object sender, RoutedEventArgs e)
        {
            HideWidget();
            OpenManagerRequested?.Invoke(this, e);
        }

        private void Startup_Click(object sender, RoutedEventArgs e)
        {
            if (_launchAtLogin.IsEnabled)
            {
                var choice = MessageBox.Show(
                    "关闭后，登录 Windows 时 YTray 将不会自动进入系统托盘。",
                    "确认关闭开机启动？",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No
                );
                if (choice != MessageBoxResult.Yes) return;
            }
            if (!_launchAtLogin.SetEnabled(!_launchAtLogin.IsEnabled))
                MessageBox.Show(_launchAtLogin.ErrorMessage ?? "设置失败。", "YTray", MessageBoxButton.OK, MessageBoxImage.Warning);
            Refresh();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => HideWidget();

        private void Thumbnail_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (sender is FrameworkElement anchor && anchor.Tag is BrowserInstancePresentation row
                && row.ThumbnailSource is ImageSource thumbnail)
                _thumbnailPreview.Schedule(anchor, thumbnail, row.LastPageTitle ?? row.Name);
        }

        private void Thumbnail_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => _thumbnailPreview.Dismiss();

        private void Focus_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).Tag is BrowserInstance instance) _store.Focus(instance);
        }

        private async void Capture_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!(((FrameworkElement)sender).Tag is BrowserInstance instance)) return;
                ShowInstanceAction("正在截取当前页面…", false, keepVisible: true);
                var output = await _store.CaptureAsync(instance);
                ShowInstanceAction(!string.IsNullOrWhiteSpace(output)
                    ? $"截图已保存 · {output}"
                    : (_store.ErrorMessage ?? "截图失败"), string.IsNullOrWhiteSpace(output));
            }
            catch (Exception ex)
            {
                CrashGuard.Record("widget-capture-click", ex);
                ShowInstanceAction("截图失败 · " + ex.Message, true);
            }
        }

        private async void Stop_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!(((FrameworkElement)sender).Tag is BrowserInstance instance)) return;
                ShowInstanceAction($"正在停止 {instance.Name}…", false, keepVisible: true);
                var stopped = await _store.StopAsync(instance);
                ShowInstanceAction(stopped ? $"{instance.Name} 已停止" : (_store.ErrorMessage ?? "停止失败"), !stopped);
            }
            catch (Exception ex)
            {
                CrashGuard.Record("widget-stop-click", ex);
                ShowInstanceAction("停止失败 · " + ex.Message, true);
            }
        }

        private void Restore_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).Tag is BrowserInstance instance) _store.RestoreHistory(instance);
        }

        private void DeleteHistory_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).Tag is BrowserInstance instance) _store.RemoveHistory(instance);
        }

        private async void Check_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!CommitProxyEditor()) return;
                CheckBtn.IsEnabled = false;
                ProxyStatus.Text = "检测中 · 最多 10 秒";
                await _store.CheckPresetProxyAsync();
                Refresh();
            }
            catch (Exception ex)
            {
                CrashGuard.Record("widget-proxy-check", ex);
                ProxyStatus.Text = "检测失败 · " + ex.Message;
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (!CommitProxyEditor()) return;
            var saved = _store.RememberPresetProxy();
            ProxyStatus.Text = saved == null ? (_store.ErrorMessage ?? "保存失败") : "已保存";
            ProxyStatus.Foreground = saved == null
                ? (Brush)FindResource("DangerBrush")
                : (Brush)FindResource("SuccessBrush");
        }

        private async void ShowInstanceAction(string text, bool isError, bool keepVisible = false)
        {
            var generation = ++_actionFeedbackGeneration;
            InstanceActionStatus.Text = text;
            InstanceActionStatus.ToolTip = text;
            InstanceActionStatus.Foreground = (Brush)FindResource(isError ? "DangerBrush" : "WidgetTextSecondaryBrush");
            if (keepVisible) return;
            await System.Threading.Tasks.Task.Delay(3200);
            if (generation == _actionFeedbackGeneration && IsLoaded)
                InstanceActionStatus.Text = "";
        }
    }
}
