#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using YTray.Core;
using YTray.Models;
using YTray.Views.Pages;

namespace YTray.Views
{
    public partial class ManagerView : Window
    {
        private readonly InstanceStore _store;
        private readonly Dictionary<string, Page> _pageCache = new Dictionary<string, Page>();
        private bool _loaded;
        private bool _sidebarRefreshScheduled;
        private string? _currentPageTag;

        public ManagerView(InstanceStore store)
        {
            InitializeComponent();
            _store = store;
            Loaded += OnLoaded;
            Closed += OnClosed;
            StateChanged += (s, e) =>
                MaximizeIcon.Data = Geometry.Parse(WindowState == WindowState.Maximized
                    ? "M 2.5 0.5 L 9.5 0.5 L 9.5 7.5 M 0.5 2.5 L 7.5 2.5 L 7.5 9.5 L 0.5 9.5 Z"
                    : "M 0.5 0.5 L 9.5 0.5 L 9.5 9.5 L 0.5 9.5 Z");
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!_loaded)
            {
                _loaded = true;
                NavQuick.IsSelected = true;
                ShowPage("quick");
                _store.PropertyChanged += OnStorePropertyChanged;
                ThemeManager.ThemeChanged += OnThemeChanged;
            }
            RefreshSidebarStatus();
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _store.PropertyChanged -= OnStorePropertyChanged;
            ThemeManager.ThemeChanged -= OnThemeChanged;
            ContentFrame.Content = null;
            _pageCache.Clear();
        }

        private void OnThemeChanged(object sender, EventArgs e) => RefreshSidebarStatus();

        private void OnStorePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!IsLoaded) return;
            if (_sidebarRefreshScheduled) return;
            _sidebarRefreshScheduled = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _sidebarRefreshScheduled = false;
                if (IsLoaded) RefreshSidebarStatus();
            }), DispatcherPriority.Background);
        }

        private void RefreshSidebarStatus()
        {
            var count = _store.RunningInstances.Count;
            SidebarStatus.Text = count == 0 ? "暂无运行中实例" : $"{count} 个实例运行中";
            RunningDot.Opacity = count == 0 ? 0.35 : 1;
            RefreshThemeControls();
        }

        private void RefreshThemeControls()
        {
            if (ThemeButtonIcon == null) return;
            var preference = _store.Settings.ThemePreference;
            ThemeButtonIcon.Data = (Geometry)FindResource(preference == AppThemePreference.System
                ? "ThemeSystemIcon"
                : preference == AppThemePreference.Dark ? "ThemeDarkIcon" : "ThemeLightIcon");
            ThemeButton.ToolTip = preference == AppThemePreference.System
                ? $"外观主题 · 跟随系统（{(ThemeManager.IsDark ? "深色" : "浅色")}）"
                : $"外观主题 · {(preference == AppThemePreference.Dark ? "深色" : "浅色")}";

            SystemThemeCheck.Visibility = preference == AppThemePreference.System ? Visibility.Visible : Visibility.Hidden;
            LightThemeCheck.Visibility = preference == AppThemePreference.Light ? Visibility.Visible : Visibility.Hidden;
            DarkThemeCheck.Visibility = preference == AppThemePreference.Dark ? Visibility.Visible : Visibility.Hidden;
            ApplyThemeChoiceBackground(SystemThemeButton, preference == AppThemePreference.System);
            ApplyThemeChoiceBackground(LightThemeButton, preference == AppThemePreference.Light);
            ApplyThemeChoiceBackground(DarkThemeButton, preference == AppThemePreference.Dark);
        }

        private static void ApplyThemeChoiceBackground(Button button, bool selected)
        {
            button.SetResourceReference(BackgroundProperty, selected ? "BrandPaleBrush" : "SurfaceRaisedBrush");
        }

        private void ThemeButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshThemeControls();
            ThemePopup.IsOpen = !ThemePopup.IsOpen;
        }

        private void ThemeChoice_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button) || !(button.Tag is string raw)
                || !Enum.TryParse(raw, out AppThemePreference preference)) return;
            _store.SetThemePreference(preference);
            ThemePopup.IsOpen = false;
            RefreshSidebarStatus();
        }

        private void Nav_Selected(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            if (sender is ListBoxItem item && item.Tag is string tag) ShowPage(tag);
        }

        private void ShowPage(string tag)
        {
            tag = string.IsNullOrWhiteSpace(tag) ? "quick" : tag;
            if (_currentPageTag == tag && ContentFrame.Content != null) return;
            if (!_pageCache.TryGetValue(tag, out var page))
            {
                switch (tag)
                {
                    case "runtimes": page = new RuntimePage(_store); break;
                    case "settings": page = new SettingsPage(_store); break;
                    case "instances": page = new InstancesPage(_store); break;
                    case "plugins": page = new PluginsPage(_store); break;
                    default: page = new QuickLaunchPage(_store); tag = "quick"; break;
                }
                _pageCache[tag] = page;
            }
            _currentPageTag = tag;
            ContentFrame.Content = page;
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void Maximize_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
