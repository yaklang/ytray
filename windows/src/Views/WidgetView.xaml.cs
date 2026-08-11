using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using YTray.Core;
using YTray.Models;

namespace YTray.Views
{
    public partial class WidgetView : Window
    {
        private readonly InstanceStore _store;
        public event EventHandler OpenManagerRequested;
        private bool _refreshScheduled;

        public WidgetView(InstanceStore store)
        {
            InitializeComponent();
            _store = store;
            Refresh();
            // Use BeginInvoke (async) instead of Invoke (sync) to avoid Dispatcher deadlock.
            // Coalesce multiple notifications into a single refresh to prevent event storms.
            _store.PropertyChanged += OnStorePropertyChanged;
        }

        private void OnStorePropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (_refreshScheduled) return;
            _refreshScheduled = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _refreshScheduled = false;
                if (IsVisible) Refresh();
            }), DispatcherPriority.Background);
        }

        public void PositionNearCursor()
        {
            var screen = System.Windows.Forms.Cursor.Position;
            Left = screen.X - ActualWidth / 2;
            Top = screen.Y - 40;
            var wa = System.Windows.Forms.Screen.FromPoint(screen).WorkingArea;
            if (Left < wa.Left) Left = wa.Left;
            if (Left + ActualWidth > wa.Right) Left = wa.Right - ActualWidth;
            if (Top + ActualHeight > wa.Bottom) Top = wa.Bottom - ActualHeight - 20;
        }

        public void PositionBeside(double anchorX, double anchorY, bool onLeft)
        {
            Left = onLeft ? anchorX + 8 : anchorX - ActualWidth - 8;
            Top = anchorY - ActualHeight / 2;
            var wa = SystemParameters.WorkArea;
            if (Left < wa.Left) Left = wa.Left + 4;
            if (Left + ActualWidth > wa.Right) Left = wa.Right - ActualWidth - 4;
            if (Top < wa.Top) Top = wa.Top + 4;
            if (Top + ActualHeight > wa.Bottom) Top = wa.Bottom - ActualHeight - 4;
        }

        private void Refresh()
        {
            if (!IsLoaded) return;
            RunningList.ItemsSource = _store.RunningInstances;
            HistoryList.ItemsSource = _store.HistoryInstances;
            RunningCountLabel.Text = _store.RunningInstances.Count.ToString();
            HistoryCountLabel.Text = _store.HistoryInstances.Count.ToString();
            var rt = _store.DefaultRuntime;
            DefaultRuntimeLabel.Text = rt != null ? $"默认 · {rt.DisplayTitle} {rt.VersionLabel}" : "未选择默认浏览器";
            if (SchemeCombo.Items.Count > 0)
                SchemeCombo.SelectedIndex = (int)_store.Settings.PresetProxyScheme;
            HostBox.Text = _store.Settings.PresetProxyHost;
            PortBox.Text = _store.Settings.PresetProxyPort.ToString();
            RemarkBox.Text = _store.Settings.PresetProxyRemark;
            ProxyStatus.Text = _store.ProxyCheckMessage;
            DirectBtn.IsEnabled = ProxyBtn.IsEnabled = !_store.IsLaunching;
        }

        private void DirectLaunch_Click(object s, RoutedEventArgs e) => _store.LaunchConfigured(false);
        private void ProxyLaunch_Click(object s, RoutedEventArgs e) => _store.LaunchConfigured(true);
        private void OpenManager_Click(object s, RoutedEventArgs e) => OpenManagerRequested?.Invoke(this, e);
        private void Close_Click(object s, RoutedEventArgs e) => Hide();

        private void Focus_Click(object s, RoutedEventArgs e)
        {
            if (((FrameworkElement)s).Tag is BrowserInstance i) _store.Focus(i);
        }
        private void Capture_Click(object s, RoutedEventArgs e)
        {
            if (((FrameworkElement)s).Tag is BrowserInstance i) _ = _store.CaptureAsync(i);
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
        private void Check_Click(object s, RoutedEventArgs e) => _ = _store.CheckPresetProxyAsync();
        private void Save_Click(object s, RoutedEventArgs e) => _store.RememberPresetProxy();
        private void ProxyHistory_Click(object s, RoutedEventArgs e) { /* menu popup - left as future enhancement */ }

        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
        }
    }
}