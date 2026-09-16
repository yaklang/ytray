#nullable enable
using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        private bool _syncingEditor = true;
        private bool _editorDirty;
        private bool _isPinned;
        private bool _hasModal;
        private bool _menuOpen;
        private bool _refreshScheduled;
        private int _dismissGeneration;
        private int _actionFeedbackGeneration;
        private readonly ThumbnailPreviewWindow _thumbnailPreview = new ThumbnailPreviewWindow();
        private string? _runningSignature;
        private string? _historySignature;

        public event EventHandler? OpenManagerRequested;

        public WidgetView(InstanceStore store, LaunchAtLoginManager? launchAtLogin = null)
        {
            _store = store;
            _launchAtLogin = launchAtLogin ?? new LaunchAtLoginManager();
            InitializeComponent();
            ApplyWidgetPalette();
            LoadProxyEditor();
            Loaded += (s, e) => Refresh();
            Deactivated += OnDeactivated;
            Closed += (s, e) =>
            {
                _store.PropertyChanged -= OnStorePropertyChanged;
                InstanceThumbnailImageSource.ImageLoaded -= OnThumbnailImageLoaded;
                ThemeManager.ThemeChanged -= OnThemeChanged;
                _launchAtLogin.PropertyChanged -= OnStorePropertyChanged;
                _thumbnailPreview.Close();
            };
            _store.PropertyChanged += OnStorePropertyChanged;
            InstanceThumbnailImageSource.ImageLoaded += OnThumbnailImageLoaded;
            ThemeManager.ThemeChanged += OnThemeChanged;
            _launchAtLogin.PropertyChanged += OnStorePropertyChanged;
        }

        private void OnThumbnailImageLoaded(object sender, EventArgs e)
        {
            _runningSignature = null;
            _historySignature = null;
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
            if (ShouldStayOpen) return;
            var generation = ++_dismissGeneration;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (generation == _dismissGeneration && !ShouldStayOpen && !IsActive && IsVisible) HideWidget();
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
            var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
            MaxHeight = screen.WorkingArea.Height / GetDpiScale().DpiScaleY - 16;
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
                RunningList.ItemsSource = visibleRunning.Select(i => new BrowserInstancePresentation(i,
                    _store.Runtimes.FirstOrDefault(r => r.Id == i.RuntimeID))).ToList();
            }
            if (_historySignature != historySignature)
            {
                _historySignature = historySignature;
                HistoryList.ItemsSource = visibleHistory.Select(i => new BrowserInstancePresentation(i,
                    _store.Runtimes.FirstOrDefault(r => r.Id == i.RuntimeID))).ToList();
            }
            RunningCountLabel.Text = running.Count.ToString();
            HistoryCountLabel.Text = history.Count.ToString();
            RunningEmpty.Visibility = running.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            HistoryEmpty.Visibility = history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RunningList.Visibility = running.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            HistoryList.Visibility = history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ClearHistoryBtn.IsEnabled = history.Count > 0;
            MoreHistoryBtn.Visibility = history.Count > 4 ? Visibility.Visible : Visibility.Collapsed;

            var runtime = _store.DefaultRuntime;
            DefaultRuntimeLabel.Text = runtime != null
                ? $"默认 · {runtime.DisplayTitle} {runtime.VersionLabel}"
                : "未选择默认浏览器";
            if (!_editorDirty) LoadProxyEditor();
            RecentProxyCount.Text = $"最近 {_store.Settings.RecentProxyPresets.Count}/5";
            ProxyCard.Height = _store.IsProxyAdvancedExpanded ? 212 : 136;
            AdvancedPanel.Visibility = _store.IsProxyAdvancedExpanded ? Visibility.Visible : Visibility.Collapsed;
            AdvancedGlyph.Text = _store.IsProxyAdvancedExpanded ? "\uE70E" : "\uE70D";
            StartupLabel.Text = _launchAtLogin.IsEnabled ? "开机启动已开" : "开启开机启动";
            StartupBtn.SetResourceReference(ForegroundProperty,
                _launchAtLogin.IsEnabled ? "WidgetAccentBrush" : "WidgetTextPrimaryBrush");
            if (!string.IsNullOrEmpty(_store.ProxyCheckMessage))
                ProxyStatus.Text = _store.ProxyCheckMessage;
            CheckBtn.IsEnabled = _store.ProxyCheckPhase != ProxyCheckPhase.Checking;
            DirectBtn.IsEnabled = ProxyBtn.IsEnabled = !_store.IsLaunching;
            foreach (var button in FindVisualChildren<Button>(HistoryList).Where(b => b.Name == "RestoreButton"))
                button.IsEnabled = !_store.IsLaunching;
        }

        private bool ShouldStayOpen => _isPinned || _hasModal || _menuOpen
            || SchemeCombo.IsDropDownOpen || _store.IsConfirmingLaunch
            || _store.ProxyCheckPhase == ProxyCheckPhase.Checking;

        private void OnThemeChanged(object? sender, EventArgs e) => ApplyWidgetPalette();

        private void ApplyWidgetPalette()
        {
            // Only the widget uses these neutral macOS popover colors. The manager and edge
            // dock retain their own palette; ThemeChanged refreshes already-open controls.
            var dark = ThemeManager.IsDark;
            var colors = new (string Key, string Dark, string Light)[]
            {
                ("WidgetSurfaceBrush", "#393939", "#F0F0F0"),
                ("WidgetDeepBrush", "#1E1E1E", "#FFFFFF"),
                ("WidgetRaisedBrush", "#292929", "#E6E6E6"),
                ("WidgetInputBrush", "#292929", "#FFFFFF"),
                ("WidgetComboBrush", "#565656", "#DDDDDD"),
                ("WidgetHoverBrush", "#494949", "#EAEAEA"),
                ("WidgetBorderBrush", "#484848", "#C9C9C9"),
                ("WidgetHairlineBrush", "#3E3E3E", "#DCDCDC"),
                ("WidgetTextPrimaryBrush", "#DEDEDE", "#262626"),
                ("WidgetTextSecondaryBrush", "#9E9E9E", "#6B6B6B"),
                ("WidgetTextTertiaryBrush", "#616161", "#8A8A8A"),
                ("WidgetPlaceholderBrush", "#858585", "#929292"),
                ("WidgetAccentBrush", "#F28B44", "#F28B44"),
                ("WidgetLaunchSecondaryBrush", "#444444", "#E3E3E3"),
                ("WidgetAccentBorderBrush", "#67503E", "#E5C8B4"),
                ("WidgetOrangePaleBrush", "#4A413B", "#FAEBDD"),
                ("WidgetDangerBrush", "#FF453A", "#D9362C"),
                ("WidgetDangerPaleBrush", "#312322", "#FDECEB"),
                ("WidgetDangerBorderBrush", "#512B28", "#F4D2CF"),
                ("WidgetSuccessBrush", "#24D836", "#18862E"),
                ("WidgetSuccessPaleBrush", "#233525", "#E7F3E8"),
            };
            foreach (var entry in colors)
                Resources[entry.Key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? entry.Dark : entry.Light));
            UpdatePinAppearance();
        }

        private void LoadProxyEditor()
        {
            _syncingEditor = true;
            try
            {
                var settings = _store.Settings;
                SchemeCombo.SelectedIndex = (int)settings.PresetProxyScheme;
                HostBox.Text = settings.PresetProxyHost;
                PortBox.Text = settings.PresetProxyPort.ToString();
                RemarkBox.Text = settings.PresetProxyRemark;
                UsernameBox.Text = settings.PresetProxyUsername;
                PasswordInput.Password = settings.PresetProxyPassword;
                TargetBox.Text = settings.PresetProxyCheckTarget;
            }
            finally { _syncingEditor = false; }
            PasswordPlaceholder.Visibility = PasswordInput.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ProxyEditor_Changed(object sender, RoutedEventArgs e)
        {
            if (!_syncingEditor) _editorDirty = true;
        }

        private void Password_Changed(object sender, RoutedEventArgs e)
        {
            if (!_syncingEditor) _editorDirty = true;
            if (PasswordPlaceholder != null)
                PasswordPlaceholder.Visibility = PasswordInput.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Advanced_Click(object sender, RoutedEventArgs e)
        {
            _store.IsProxyAdvancedExpanded = !_store.IsProxyAdvancedExpanded;
            RefreshAndMeasure();
            ClampToWorkingArea();
        }

        private void ClampToWorkingArea()
        {
            var screen = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
            var scale = GetDpiScale();
            var work = screen.WorkingArea;
            Left = Math.Max(work.Left / scale.DpiScaleX + 8, Math.Min(Left, work.Right / scale.DpiScaleX - ActualWidth - 8));
            Top = Math.Max(work.Top / scale.DpiScaleY + 8, Math.Min(Top, work.Bottom / scale.DpiScaleY - ActualHeight - 8));
        }

        private void Pin_Click(object sender, RoutedEventArgs e)
        {
            _isPinned = !_isPinned;
            UpdatePinAppearance();
        }

        private void UpdatePinAppearance()
        {
            PinBtn.SetResourceReference(ForegroundProperty, _isPinned ? "WidgetAccentBrush" : "WidgetTextSecondaryBrush");
            PinBtn.ToolTip = _isPinned ? "取消固定；失焦后自动隐藏" : "固定小组件";
        }

        private void RunningSection_Click(object sender, RoutedEventArgs e)
        {
            RunningCard.Visibility = RunningCard.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            RunningChevron.Text = RunningCard.Visibility == Visibility.Visible ? "\uE70D" : "\uE76C";
        }

        private void HistorySection_Click(object sender, RoutedEventArgs e)
        {
            HistoryCard.Visibility = HistoryCard.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            HistoryChevron.Text = HistoryCard.Visibility == Visibility.Visible ? "\uE70D" : "\uE76C";
        }

        private ContextMenu CreateMenu()
        {
            var menu = new ContextMenu { Style = (Style)FindResource("WidgetContextMenu") };
            // ContextMenu lives in a separate popup tree. Give its templates the current
            // widget resources, including a fresh palette after a theme switch.
            menu.Resources.MergedDictionaries.Add(Resources);
            menu.Opened += (s, e) => { _menuOpen = true; CancelPendingDismiss(); };
            menu.Closed += (s, e) => { _menuOpen = false; if (!IsActive) OnDeactivated(this, EventArgs.Empty); };
            return menu;
        }

        private void OpenMenu(ContextMenu menu, Button anchor)
        {
            anchor.ContextMenu = menu;
            menu.PlacementTarget = anchor;
            menu.Placement = PlacementMode.Bottom;
            _menuOpen = true;
            CancelPendingDismiss();
            menu.IsOpen = true;
        }

        private void QuickConfig_Click(object sender, RoutedEventArgs e)
        {
            var menu = CreateMenu();
            foreach (var runtime in _store.Runtimes)
            {
                var item = new MenuItem
                {
                    Header = (runtime.Id == _store.DefaultRuntime?.Id ? "✓  " : "    ") + runtime.DisplayTitle + " " + runtime.VersionLabel,
                };
                item.Click += (s, args) => { _store.SelectDefaultRuntime(runtime); Refresh(); };
                menu.Items.Add(item);
            }
            var manager = new MenuItem { Header = "全部管理…" };
            manager.Click += OpenManager_Click;
            menu.Items.Add(manager);
            OpenMenu(menu, QuickConfigBtn);
        }

        private void ProxyHistory_Click(object sender, RoutedEventArgs e)
        {
            var menu = CreateMenu();
            foreach (var preset in _store.Settings.RecentProxyPresets.Take(5))
            {
                var item = new MenuItem { Header = string.IsNullOrWhiteSpace(preset.Remark) ? preset.Server : preset.Remark + " · " + preset.Server };
                item.Click += (s, args) =>
                {
                    _store.SelectProxyPreset(preset);
                    _editorDirty = false;
                    LoadProxyEditor();
                    Refresh();
                };
                menu.Items.Add(item);
            }
            if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "暂无历史", IsEnabled = false });
            OpenMenu(menu, ProxyHistoryBtn);
        }

        private void Startup_Click(object sender, RoutedEventArgs e)
        {
            _hasModal = true;
            try
            {
                _launchAtLogin.Refresh();
                if (_launchAtLogin.IsEnabled && MessageBox.Show(this, "关闭后，登录 Windows 时 YTray 将不会自动进入系统托盘。",
                        "确认关闭开机启动？", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
                if (!_launchAtLogin.SetEnabled(!_launchAtLogin.IsEnabled))
                    ShowInstanceAction(_launchAtLogin.ErrorMessage ?? "无法保存开机启动设置", true);
                Refresh();
            }
            finally { _hasModal = false; CancelPendingDismiss(); }
        }

        private void ClearHistory_Click(object sender, RoutedEventArgs e)
        {
            _hasModal = true;
            try
            {
                if (MessageBox.Show(this, "将删除全部历史记录；正在运行的浏览器不会受到影响。",
                        "清理全部历史？", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                    _store.RemoveAllHistory();
            }
            finally { _hasModal = false; CancelPendingDismiss(); Refresh(); }
        }

        private void RenameHistory_Click(object sender, RoutedEventArgs e)
        {
            if (!(((FrameworkElement)sender).Tag is BrowserInstance instance)) return;
            var input = new TextBox { Text = instance.Name, Style = (Style)FindResource("WidgetTextBox"), Height = 28 };
            var save = new Button { Content = "保存", Width = 62, Style = (Style)FindResource("WidgetPrimaryButton"), IsDefault = true };
            var cancel = new Button { Content = "取消", Width = 62, Style = (Style)FindResource("WidgetButton"), IsCancel = true, Margin = new Thickness(0, 0, 7, 0) };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            actions.Children.Add(cancel); actions.Children.Add(save);
            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock { Text = "修改历史名称", FontSize = 14, Foreground = (Brush)FindResource("WidgetTextPrimaryBrush"), Margin = new Thickness(0, 0, 0, 12) });
            panel.Children.Add(input); panel.Children.Add(actions);
            var dialog = new Window
            {
                Owner = this, Title = "修改历史名称", Content = panel, Width = 330, SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
                Background = (Brush)FindResource("WidgetSurfaceBrush"), ShowInTaskbar = false,
            };
            dialog.Resources.MergedDictionaries.Add(Resources);
            save.Click += (s, args) => { if (!string.IsNullOrWhiteSpace(input.Text)) dialog.DialogResult = true; };
            dialog.Loaded += (s, args) => { input.Focus(); input.SelectAll(); };
            _hasModal = true;
            try { if (dialog.ShowDialog() == true) _store.RenameHistory(instance, input.Text); }
            finally { _hasModal = false; CancelPendingDismiss(); Refresh(); }
        }

        private void Thumbnail_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is FrameworkElement element)
                element.Clip = new RectangleGeometry(new Rect(0, 0, element.ActualWidth, element.ActualHeight), 8, 8);
        }

        private static System.Collections.Generic.IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match) yield return match;
                foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
            }
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
            _store.Settings.PresetProxyUsername = UsernameBox.Text.Trim();
            _store.Settings.PresetProxyPassword = PasswordInput.Password;
            _store.Settings.PresetProxyCheckTarget = TargetBox.Text.Trim();
            _editorDirty = false;
            ProxyStatus.Foreground = (Brush)FindResource("WidgetTextSecondaryBrush");
            return true;
        }

        private void DirectLaunch_Click(object sender, RoutedEventArgs e)
        {
            if (!_store.LaunchConfigured(false) && !_store.LaunchWasCancelled)
                ShowInstanceAction(_store.ErrorMessage ?? "无法启动浏览器，请检查当前配置。", true);
        }
        private void ProxyLaunch_Click(object sender, RoutedEventArgs e)
        {
            if (!CommitProxyEditor()) return;
            if (!_store.LaunchConfigured(true) && !_store.LaunchWasCancelled)
                ShowInstanceAction(_store.ErrorMessage ?? "无法启动浏览器，请检查当前配置。", true);
        }

        private void OpenManager_Click(object sender, RoutedEventArgs e)
        {
            HideWidget();
            OpenManagerRequested?.Invoke(this, e);
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
            InstanceActionStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
            InstanceActionStatus.ToolTip = text;
            InstanceActionStatus.Foreground = (Brush)FindResource(isError ? "DangerBrush" : "WidgetTextSecondaryBrush");
            if (keepVisible) return;
            await System.Threading.Tasks.Task.Delay(3200);
            if (generation == _actionFeedbackGeneration && IsLoaded)
            {
                InstanceActionStatus.Text = "";
                InstanceActionStatus.Visibility = Visibility.Collapsed;
            }
        }
    }
}
