using System;
using System.Windows;
using System.Windows.Controls;
using YTray.Core;
using YTray.Views.Pages;

namespace YTray.Views
{
    public partial class ManagerView : Window
    {
        private readonly InstanceStore _store;
        private bool _loaded;

        public ManagerView(InstanceStore store)
        {
            InitializeComponent();
            _store = store;
            // Don't call ShowPage during InitializeComponent — wait for Loaded event.
            _store.PropertyChanged += (s, e) => Dispatcher.Invoke(() => { /* pages refresh themselves */ });
            Loaded += (s, e) =>
            {
                if (!_loaded)
                {
                    _loaded = true;
                    NavQuick.IsSelected = true;
                    ShowPage("quick");
                }
            };
        }

        private void Nav_Selected(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return; // ignore events fired during InitializeComponent
            if (sender is ListBoxItem item && item.Tag is string tag) ShowPage(tag);
        }

        private void ShowPage(string tag)
        {
            Page page;
            switch (tag)
            {
                case "runtimes": page = new RuntimePage(_store); break;
                case "settings": page = new SettingsPage(_store); break;
                case "instances": page = new InstancesPage(_store); break;
                case "plugins": page = new PluginsPage(_store); break;
                default: page = new QuickLaunchPage(_store); break;
            }
            ContentFrame.Content = page;
        }
    }
}