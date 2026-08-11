using System.Windows;
using System.Windows.Controls;
using YTray.Core;
using YTray.Models;

namespace YTray.Views.Pages
{
    public partial class InstancesPage : Page
    {
        private readonly InstanceStore _store;

        public InstancesPage(InstanceStore store)
        {
            InitializeComponent();
            _store = store;
            Refresh();
            _store.PropertyChanged += (s, e) => Dispatcher.Invoke(Refresh);
        }

        private void Refresh()
        {
            RunningList.ItemsSource = _store.RunningInstances;
            HistoryList.ItemsSource = _store.HistoryInstances;
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