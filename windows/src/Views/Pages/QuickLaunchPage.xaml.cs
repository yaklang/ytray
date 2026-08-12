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
    public partial class QuickLaunchPage : Page
    {
        private readonly InstanceStore _store;
        private bool _subscribed;
        private bool _refreshScheduled;

        public QuickLaunchPage(InstanceStore store)
        {
            InitializeComponent();
            _store = store;
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
            var selected = RuntimeCombo.SelectedItem as BrowserRuntime ?? _store.DefaultRuntime;
            RuntimeCombo.ItemsSource = _store.Runtimes.OrderByDescending(r => r.Id == _store.Settings.DefaultRuntimeID).ToList();
            RuntimeCombo.SelectedItem = selected == null ? _store.DefaultRuntime : _store.Runtimes.FirstOrDefault(r => r.Id == selected.Id);
            if (!HomeUrlBox.IsKeyboardFocusWithin) HomeUrlBox.Text = _store.Settings.HomeURL;
            ProxySummaryLabel.Text = (_store.Settings.PresetProxyServer ?? LaunchSettings.DefaultPresetProxyServer).Replace("http://", "").Replace("https://", "");

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

        private void RuntimeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (RuntimeCombo.SelectedItem is BrowserRuntime runtime && _store.Settings.DefaultRuntimeID != runtime.Id)
                _store.SelectDefaultRuntime(runtime);
        }

        private void Launch_Click(object sender, RoutedEventArgs e)
        {
            _store.Settings.HomeURL = HomeUrlBox.Text.Trim();
            _store.SaveSettings();
            _store.LaunchConfigured(NetworkCombo.SelectedIndex == 1);
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
