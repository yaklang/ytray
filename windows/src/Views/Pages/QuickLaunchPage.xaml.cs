#nullable enable
using System;
using System.ComponentModel;
using System.Linq;
using System.Collections.Generic;
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

        private sealed class NetworkChoice
        {
            public bool UsesProxy { get; set; }
            public string Title { get; set; } = "";
            public string Subtitle { get; set; } = "";
        }

        private sealed class PluginChoice
        {
            public BrowserPlugin Plugin { get; set; } = null!;
            public ImageSource? IconSource { get; set; }
            public bool IsSelected { get; set; }
        }

        private readonly InstanceStore _store;
        private bool _subscribed;
        private bool _refreshScheduled;
        private string? _runtimeChoicesSignature;
        private string? _pluginChoicesSignature;
        private readonly List<RuntimeChoice> _runtimeChoices = new List<RuntimeChoice>();
        private readonly List<NetworkChoice> _networkChoices = new List<NetworkChoice>();
        private readonly List<PluginChoice> _pluginChoices = new List<PluginChoice>();

        public QuickLaunchPage(InstanceStore store)
        {
            InitializeComponent();
            _store = store;
            _networkChoices.Add(new NetworkChoice { Title = "无代理", Subtitle = "直连网络", UsesProxy = false });
            _networkChoices.Add(new NetworkChoice { Title = "HTTP 代理", UsesProxy = true });
            NetworkCombo.ItemsSource = _networkChoices;
            NetworkCombo.SelectedIndex = 1;
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

        private BrowserInstancePresentation Present(BrowserInstance instance) =>
            new BrowserInstancePresentation(instance, _store.RuntimeFor(instance));

        private void Refresh()
        {
            if (RuntimeCombo == null) return;
            RefreshRuntimeChoices();
            RefreshPluginChoices();
            if (!HomeUrlBox.IsKeyboardFocusWithin) HomeUrlBox.Text = _store.Settings.HomeURL;
            var proxyAddress = (_store.Settings.PresetProxyServer ?? LaunchSettings.DefaultPresetProxyServer)
                .Replace("http://", "").Replace("https://", "");
            _networkChoices[1].Subtitle = proxyAddress;
            NetworkCombo.Items.Refresh();

            var running = _store.RunningInstances.Select(Present).ToList();
            var history = _store.HistoryInstances.Take(3).Select(Present).ToList();
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

        private void RefreshRuntimeChoices()
        {
            var signature = string.Join("|", _store.Runtimes.Select(runtime =>
                $"{runtime.Id:N}:{runtime.DisplayTitle}:{runtime.VersionLabel}:{runtime.ExecutablePath}"))
                + ":default=" + _store.Settings.DefaultRuntimeID;
            if (_runtimeChoicesSignature == signature) return;

            var selectedId = (RuntimeCombo.SelectedItem as RuntimeChoice)?.Runtime.Id
                ?? _store.DefaultRuntime?.Id;
            _runtimeChoicesSignature = signature;
            _runtimeChoices.Clear();
            foreach (var runtime in _store.Runtimes.OrderByDescending(r => r.Id == _store.Settings.DefaultRuntimeID))
            {
                _runtimeChoices.Add(new RuntimeChoice
                {
                    Runtime = runtime,
                    IconSource = BrowserIconSource.FromExecutable(runtime.ExecutablePath),
                });
            }
            RuntimeCombo.ItemsSource = _runtimeChoices;
            RuntimeCombo.SelectedItem = _runtimeChoices.FirstOrDefault(choice => choice.Runtime.Id == selectedId)
                ?? _runtimeChoices.FirstOrDefault();
        }

        private void RuntimeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (RuntimeCombo.SelectedItem is RuntimeChoice choice && _store.Settings.DefaultRuntimeID != choice.Runtime.Id)
                _store.SelectDefaultRuntime(choice.Runtime);
        }

        private void RefreshPluginChoices()
        {
            var signature = string.Join("|", _store.Plugins.Select(plugin =>
                $"{plugin.Id:N}:{plugin.Enabled}:{plugin.Name}:{plugin.Version}:{plugin.IconPath}"))
                + ":selected=" + string.Join(",", _store.Settings.DefaultPluginIDs.OrderBy(id => id));
            if (_pluginChoicesSignature != signature)
            {
                _pluginChoicesSignature = signature;
                var selectedIDs = new HashSet<Guid>(_store.Settings.DefaultPluginIDs);
                _pluginChoices.Clear();
                foreach (var plugin in _store.Plugins.Where(plugin => plugin.Enabled))
                {
                    _pluginChoices.Add(new PluginChoice
                    {
                        Plugin = plugin,
                        IconSource = PluginIconSource.FromPlugin(plugin),
                        IsSelected = selectedIDs.Contains(plugin.Id),
                    });
                }
                PluginChoiceList.ItemsSource = _pluginChoices;
            }

            PluginChoiceEmpty.Visibility = _pluginChoices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            PluginChoiceList.Visibility = _pluginChoices.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            UpdatePluginSelectionLabel();
        }

        private void UpdatePluginSelectionLabel()
        {
            var selected = _pluginChoices.Where(choice => choice.IsSelected).ToList();
            PluginSelectionLabel.Text = selected.Count switch
            {
                0 => "未加载插件",
                1 => selected[0].Plugin.Name,
                _ => $"已选择 {selected.Count} 个插件",
            };
            PluginSelectorButton.ToolTip = selected.Count == 0
                ? "为概述页快捷启动选择本地插件"
                : string.Join(Environment.NewLine, selected.Select(choice => choice.Plugin.Name));
        }

        private void TogglePluginPopup_Click(object sender, RoutedEventArgs e)
        {
            PluginPopup.IsOpen = !PluginPopup.IsOpen;
        }

        private void PluginChoice_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is CheckBox checkBox) || !(checkBox.Tag is PluginChoice choice)) return;
            choice.IsSelected = checkBox.IsChecked == true;
            _store.SetDefaultPlugins(_pluginChoices
                .Where(choice => choice.IsSelected)
                .Select(choice => choice.Plugin.Id));
            UpdatePluginSelectionLabel();
        }

        private void ChoiceCombo_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is ComboBox combo) || !combo.IsEnabled) return;

            // The popup's item clicks still belong to the ComboBox logically. Do not
            // intercept them or the selected value would never be committed.
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
            _store.LaunchConfigured((NetworkCombo.SelectedItem as NetworkChoice)?.UsesProxy == true);
        }

        private void Focus_Click(object sender, RoutedEventArgs e) { if (((FrameworkElement)sender).Tag is BrowserInstance i) _store.Focus(i); }
        private async void Stop_Click(object sender, RoutedEventArgs e) { if (((FrameworkElement)sender).Tag is BrowserInstance i) await _store.StopAsync(i); }
        private void Restore_Click(object sender, RoutedEventArgs e) { if (((FrameworkElement)sender).Tag is BrowserInstance i) _store.RestoreHistory(i); }
        private void ClearAll_Click(object sender, RoutedEventArgs e) => _store.RemoveAllHistory();
        private void Wizard_Click(object sender, RoutedEventArgs e)
        {
            var wizard = new CustomLaunchWizard(_store) { Owner = Window.GetWindow(this) };
            wizard.ShowDialog();
        }
    }
}
