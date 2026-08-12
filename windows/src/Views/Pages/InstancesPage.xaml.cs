using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using YTray.Core;
using YTray.Models;

namespace YTray.Views.Pages
{
    public partial class InstancesPage : Page
    {
        private readonly InstanceStore _store;
        private bool _subscribed;

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
            if (!IsLoaded) return;
            if (Dispatcher.CheckAccess()) Refresh();
            else Dispatcher.BeginInvoke(new Action(Refresh), DispatcherPriority.Background);
        }

        private void Refresh()
        {
            var running = _store.RunningInstances;
            var history = _store.HistoryInstances;
            RunningList.ItemsSource = running;
            HistoryList.ItemsSource = history;
            RunningCountLabel.Text = $"{running.Count} 运行中";
            HistoryCountLabel.Text = $"{history.Count} 历史";
            RunningEmpty.Visibility = running.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            HistoryEmpty.Visibility = history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Focus_Click(object s, RoutedEventArgs e)
        {
            if (((FrameworkElement)s).Tag is BrowserInstance i) _store.Focus(i);
        }

        private void Capture_Click(object s, RoutedEventArgs e)
        {
            if (((FrameworkElement)s).Tag is BrowserInstance i) _ = _store.CaptureAsync(i);
        }
        private void Reveal_Click(object s, RoutedEventArgs e)
        {
            if (((FrameworkElement)s).Tag is BrowserInstance i) _store.RevealProfile(i);
        }
        private void Stop_Click(object s, RoutedEventArgs e)
        {
            if (((FrameworkElement)s).Tag is BrowserInstance i) _store.Stop(i);
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
    }
}
