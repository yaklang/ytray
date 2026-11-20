#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using YTray.Core;
using YTray.Models;

namespace YTray.Views.Pages
{
    public partial class QuickLaunchPage : Page
    {
        private sealed class RuntimeChoice
        {
            public BrowserRuntime Runtime { get; set; } = null!;
            public ImageSource? IconSource { get; set; }
            public string Title => Runtime.DisplayTitle;
            public string Version => Runtime.VersionLabel;
        }

        private sealed class NetworkChoice : INotifyPropertyChanged
        {
            private Brush _dotBrush = Brushes.Transparent;
            private Brush _titleBrush = Brushes.Transparent;
            public ProxyPreset? Preset { get; set; }
            public string Title { get; set; } = "";
            public string Subtitle { get; set; } = "";
            public string DisplayAddress => Subtitle.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? "HTTPS · " + Subtitle.Substring("https://".Length)
                : Subtitle.Replace("http://", "");

            public Brush DotBrush
            {
                get => _dotBrush;
                set { if (!ReferenceEquals(_dotBrush, value)) { _dotBrush = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DotBrush))); } }
            }

            public Brush TitleBrush
            {
                get => _titleBrush;
                set { if (!ReferenceEquals(_titleBrush, value)) { _titleBrush = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TitleBrush))); } }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
        }

        private readonly InstanceStore _store;
        private bool _subscribed;
        private bool _refreshScheduled;
        private bool _isRebindingRuntimeChoices;
        private bool _isRebindingNetworkChoices;
        private bool _usePresetProxy = true;
        private int _toastGeneration;
        private string? _runtimeChoicesSignature;
        private List<RuntimeChoice> _runtimeChoices = new List<RuntimeChoice>();
        private List<BrowserEnvironment> _environmentChoices = new List<BrowserEnvironment>();
        private string? _choiceSignature;
        private BrowserEnvironment? SelectedEnvironment => (EnvironmentCombo.SelectedItem as ComboBoxItem)?.Tag as BrowserEnvironment;

        public QuickLaunchPage(InstanceStore store)
        {
            InitializeComponent();
            _store = store;
            EngineCombo.Configure(store.DefaultBrowserEngine);
            EngineCombo.SelectionChanged += (_, __) =>
            {
                EngineDefaultButton.Content = "设为默认";
                EngineDefaultButton.SetResourceReference(ForegroundProperty, "TextSecondaryBrush");
            };
            EnvironmentCombo.SelectionChanged += (_, __) => ApplyEnvironmentSelection();
            NetworkCombo.SelectionChanged += (_, __) =>
            {
                if (!_isRebindingNetworkChoices) _usePresetProxy = true;
                UpdateNetworkModeVisuals();
            };
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

        private void OnStorePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!IsLoaded || _refreshScheduled) return;
            _refreshScheduled = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _refreshScheduled = false;
                if (IsLoaded) Refresh();
            }), DispatcherPriority.Background);
        }

        private BrowserInstancePresentation Present(BrowserInstance instance) =>
            new BrowserInstancePresentation(instance, _store.RuntimeFor(instance));

        internal void SelectNetworkMode(bool useProxy)
        {
            _usePresetProxy = useProxy;
            UpdateNetworkModeVisuals();
        }

        private void DirectMode_Click(object sender, RoutedEventArgs e) => SelectNetworkMode(false);
        private void ProxyMode_Click(object sender, RoutedEventArgs e) => SelectNetworkMode(true);

        private void EditProxy_Click(object sender, RoutedEventArgs e)
        {
            if (Window.GetWindow(this) is ManagerView manager)
                manager.NavigateTo("launch");
        }

        private void ManageEnvironments_Click(object sender, RoutedEventArgs e)
        {
            if (Window.GetWindow(this) is ManagerView manager)
                manager.NavigateTo("environments");
        }

        private void UpdateNetworkModeVisuals()
        {
            if (DirectModeButton == null || NetworkCombo == null) return;

            DirectModeButton.Background = !_usePresetProxy ? (Brush)FindResource("SurfaceRaisedBrush") : Brushes.Transparent;
            DirectModeButton.BorderBrush = !_usePresetProxy ? (Brush)FindResource("HairlineBrush") : Brushes.Transparent;
            DirectModeLabel.SetResourceReference(TextBlock.ForegroundProperty, !_usePresetProxy ? "TextPrimaryBrush" : "TextSecondaryBrush");
            DirectModeDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, !_usePresetProxy ? "SuccessBrush" : "TextTertiaryBrush");

            NetworkCombo.Background = _usePresetProxy ? (Brush)FindResource("SurfaceRaisedBrush") : Brushes.Transparent;
            NetworkCombo.BorderBrush = _usePresetProxy ? (Brush)FindResource("BrandOrangeBrush") : Brushes.Transparent;
            NetworkCombo.Opacity = _usePresetProxy ? 1.0 : 0.72;

            var activeDot = (Brush)FindResource("BrandOrangeBrush");
            var mutedDot = (Brush)FindResource("TextTertiaryBrush");
            var primaryText = (Brush)FindResource("TextPrimaryBrush");
            var secondaryText = (Brush)FindResource("TextSecondaryBrush");
            var selected = NetworkCombo.SelectedItem as NetworkChoice;
            if (NetworkCombo.ItemsSource is IEnumerable<NetworkChoice> choices)
            {
                foreach (var item in choices)
                {
                    var isSelectedActive = _usePresetProxy && ReferenceEquals(item, selected);
                    item.DotBrush = isSelectedActive ? activeDot : mutedDot;
                    item.TitleBrush = isSelectedActive ? activeDot : (_usePresetProxy ? primaryText : secondaryText);
                }
            }
        }

        private void Refresh()
        {
            if (RuntimeCombo == null) return;
            RefreshRuntimeChoices();
            if (!HomeUrlBox.IsKeyboardFocusWithin) HomeUrlBox.Text = _store.Settings.HomeURL;
            RefreshLaunchChoices();
            ApplyEnvironmentSelection();
            UpdateNetworkModeVisuals();

            var running = _store.RunningInstances.Select(Present).ToList();
            var history = _store.HistoryInstances.Take(5).Select(Present).ToList();
            RunningList.ItemsSource = running;
            HistoryList.ItemsSource = history;
            RunningEmpty.Visibility = running.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            HistoryEmpty.Visibility = history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RunningList.Visibility = running.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            HistoryList.Visibility = history.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            RunningCountLabel.Text = $"共 {running.Count} 个实例";
            LaunchStatusLabel.Text = _store.IsLaunching
                ? (string.IsNullOrWhiteSpace(_store.LaunchMessage) ? "正在启动实例" : _store.LaunchMessage)
                : $"{running.Count} 个实例运行中";
            LaunchStatusDot.Fill = (Brush)FindResource(_store.IsLaunching ? "BrandOrangeBrush" : "SuccessBrush");
            LaunchButton.IsEnabled = WizardBtn.IsEnabled = !_store.IsLaunching && RuntimeCombo.SelectedItem != null;
        }

        private void RefreshLaunchChoices()
        {
            var settings = _store.Settings;
            var signature = settings.PresetProxyServer + "|" + settings.PresetProxyUsername + "|"
                + string.Join("|", settings.RecentProxyPresets.Select(item => $"{item.Id}:{item.Server}:{item.Remark}:{item.LastUsedAt.Ticks}"));
            if (_choiceSignature == signature && _environmentChoices.SequenceEqual(_store.BrowserEnvironments)) return;
            _choiceSignature = signature;
            _environmentChoices = _store.BrowserEnvironments.ToList();
            var selectedNetwork = NetworkCombo.SelectedItem as NetworkChoice;
            var networks = new List<NetworkChoice>
            {
                new NetworkChoice { Title = "当前代理", Subtitle = settings.PresetProxyServer }
            };
            networks.AddRange(settings.RecentProxyPresets.Where(item =>
                !string.Equals(item.Server, settings.PresetProxyServer, StringComparison.OrdinalIgnoreCase)
                || item.Username != settings.PresetProxyUsername).Select(item => new NetworkChoice {
                    Title = string.IsNullOrWhiteSpace(item.Remark) ? "已保存的代理" : item.Remark,
                    Subtitle = item.Server, Preset = item,
                }));
            _isRebindingNetworkChoices = true;
            try
            {
                NetworkCombo.ItemsSource = networks;
                NetworkCombo.SelectedItem = networks.FirstOrDefault(item => item.Preset?.Id == selectedNetwork?.Preset?.Id) ?? networks[0];
            }
            finally
            {
                _isRebindingNetworkChoices = false;
            }
            UpdateNetworkModeVisuals();

            var environmentId = SelectedEnvironment?.Id;
            EnvironmentCombo.Items.Clear();
            EnvironmentCombo.Items.Add(new ComboBoxItem { Content = "默认启动设置" });
            foreach (var item in _store.BrowserEnvironments)
                EnvironmentCombo.Items.Add(new ComboBoxItem { Content = item.Name, Tag = item });
            EnvironmentCombo.SelectedItem = EnvironmentCombo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => (item.Tag as BrowserEnvironment)?.Id == environmentId) ?? EnvironmentCombo.Items[0];
            ApplyEnvironmentSelection();
        }

        private void ApplyEnvironmentSelection()
        {
            if (EnvironmentCombo == null || RuntimeCombo == null || NetworkCombo == null) return;
            var environment = SelectedEnvironment;
            RuntimeCombo.IsEnabled = environment?.RuntimeId == null;
            var runtimeId = environment?.RuntimeId ?? RuntimeSelectionPolicy.TargetRuntimeId(_store.Settings.DefaultRuntimeID, _store.DefaultRuntime?.Id);
            _isRebindingRuntimeChoices = true;
            try { RuntimeCombo.SelectedItem = _runtimeChoices.FirstOrDefault(choice => choice.Runtime.Id == runtimeId); }
            finally { _isRebindingRuntimeChoices = false; }

            var network = _store.EnvironmentNetworkDescription(environment);
            NetworkModeSelector.Visibility = network == null ? Visibility.Visible : Visibility.Collapsed;
            ProxyPresetFields.Visibility = network == null ? Visibility.Visible : Visibility.Collapsed;
            EnvironmentNetworkBadge.Visibility = network == null ? Visibility.Collapsed : Visibility.Visible;
            EnvironmentNetwork.Text = network ?? "";

            if (EnvironmentContextHint != null)
            {
                if (environment != null)
                {
                    EnvironmentContextHint.Text = $"已启用模板「{environment.Name}」";
                    EnvironmentContextHint.SetResourceReference(TextBlock.ForegroundProperty, "BrandOrangeBrush");
                }
                else
                {
                    EnvironmentContextHint.Text = "独立用户目录 · 自动避让调试端口";
                    EnvironmentContextHint.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
                }
            }
        }

        private void RefreshRuntimeChoices()
        {
            var signature = string.Join("|", _store.Runtimes.Select(runtime =>
                $"{runtime.Id:N}:{runtime.DisplayTitle}:{runtime.VersionLabel}:{runtime.ExecutablePath}"))
                + ":default=" + _store.Settings.DefaultRuntimeID;
            if (_runtimeChoicesSignature == signature) return;

            var selectedId = RuntimeSelectionPolicy.TargetRuntimeId(
                _store.Settings.DefaultRuntimeID,
                _store.DefaultRuntime?.Id);
            var refreshedChoices = new List<RuntimeChoice>();
            foreach (var runtime in _store.Runtimes.OrderByDescending(r => r.Id == _store.Settings.DefaultRuntimeID))
            {
                refreshedChoices.Add(new RuntimeChoice
                {
                    Runtime = runtime,
                    IconSource = BrowserIconSource.FromExecutable(runtime.ExecutablePath),
                });
            }

            _runtimeChoicesSignature = signature;
            _runtimeChoices = refreshedChoices;
            _isRebindingRuntimeChoices = true;
            try
            {
                RuntimeCombo.ItemsSource = refreshedChoices;
                RuntimeCombo.SelectedItem = refreshedChoices.FirstOrDefault(choice => choice.Runtime.Id == selectedId)
                    ?? refreshedChoices.FirstOrDefault();
            }
            finally
            {
                _isRebindingRuntimeChoices = false;
            }
        }

        private void RuntimeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (RuntimeCombo.SelectedItem is RuntimeChoice choice
                && RuntimeSelectionPolicy.ShouldPersistUserSelection(
                    _isRebindingRuntimeChoices,
                    choice.Runtime.Id,
                    _store.Settings.DefaultRuntimeID))
                _store.SelectDefaultRuntime(choice.Runtime);
        }

        private void NetworkCombo_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is ComboBox combo) || !combo.IsEnabled) return;
            var source = e.OriginalSource as DependencyObject;
            if (source != null && ItemsControl.ContainerFromElement(combo, source) is ComboBoxItem)
                return;
            if (!_usePresetProxy)
            {
                SelectNetworkMode(true);
                if (e.GetPosition(combo).X < Math.Max(0, combo.ActualWidth - 32))
                {
                    combo.Focus();
                    e.Handled = true;
                    return;
                }
            }
            combo.Focus();
            combo.IsDropDownOpen = !combo.IsDropDownOpen;
            e.Handled = true;
        }

        private void ChoiceCombo_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is ComboBox combo) || !combo.IsEnabled) return;
            var source = e.OriginalSource as DependencyObject;
            if (source != null && ItemsControl.ContainerFromElement(combo, source) is ComboBoxItem)
                return;
            combo.Focus();
            combo.IsDropDownOpen = !combo.IsDropDownOpen;
            e.Handled = true;
        }

        private void Launch_Click(object sender, RoutedEventArgs e)
        {
            _store.Settings.HomeURL = HomeUrlBox.Text.Trim();
            _store.SaveSettings();
            var network = NetworkCombo.SelectedItem as NetworkChoice;
            if (!_store.LaunchConfigured(_usePresetProxy, network?.Preset, SelectedEnvironment, EngineCombo.Target) && !_store.LaunchWasCancelled)
                ShowLaunchError();
        }

        private void SaveEngineDefault_Click(object sender, RoutedEventArgs e)
        {
            if (EngineCombo.Target is not BrowserEngineTarget target) return;
            _store.SetDefaultBrowserEngine(target);
            if (sender is Button button)
            {
                button.Content = "✓ 已默认";
                button.SetResourceReference(ForegroundProperty, "SuccessBrush");
            }
        }

        private void ShowLaunchError() => MessageBox.Show(
            Window.GetWindow(this),
            _store.ErrorMessage ?? "无法启动浏览器，请检查当前配置。",
            "无法启动实例",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        private void Focus_Click(object sender, RoutedEventArgs e) { if (((FrameworkElement)sender).Tag is BrowserInstance i) _store.Focus(i); }
        private async void Capture_Click(object sender, RoutedEventArgs e)
        {
            if (!(((FrameworkElement)sender).Tag is BrowserInstance i)) return;
            var output = await _store.CaptureAsync(i);
            ShowToast(!string.IsNullOrWhiteSpace(output) ? "页面截图已导出" : (_store.ErrorMessage ?? "截图失败"), string.IsNullOrWhiteSpace(output));
        }
        private async void Stop_Click(object sender, RoutedEventArgs e) { if (((FrameworkElement)sender).Tag is BrowserInstance i) await _store.StopAsync(i); }
        private void Restore_Click(object sender, RoutedEventArgs e) { if (((FrameworkElement)sender).Tag is BrowserInstance i) _store.RestoreHistory(i); }
        private void ClearAll_Click(object sender, RoutedEventArgs e) => _store.RemoveAllHistory();
        private void Wizard_Click(object sender, RoutedEventArgs e)
        {
            var wizard = new CustomLaunchWizard(_store, SelectedEnvironment, EngineCombo.Target) { Owner = Window.GetWindow(this) };
            wizard.ShowDialog();
        }

        private async void ShowToast(string text, bool isError = false)
        {
            var generation = ++_toastGeneration;
            ActionToastText.Text = text;
            ActionToastText.Foreground = (Brush)FindResource(isError ? "DangerBrush" : "SuccessBrush");
            ActionToastBorder.Background = (Brush)FindResource(isError ? "DangerPaleBrush" : "SuccessPaleBrush");
            ActionToastBorder.Visibility = Visibility.Visible;
            await System.Threading.Tasks.Task.Delay(2400);
            if (generation == _toastGeneration && IsLoaded)
                ActionToastBorder.Visibility = Visibility.Collapsed;
        }
    }
}
