#nullable enable
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using YTray.Core;
using YTray.Models;

namespace YTray.Views.Pages
{
    public partial class BrowserEnvironmentPage : Page
    {
        private readonly InstanceStore _store;
        private Guid _draftId;

        public BrowserEnvironmentPage(InstanceStore store)
        {
            _store = store;
            InitializeComponent();
            Templates.ItemsSource = store.BrowserEnvironments;
            Loaded += (_, __) =>
            {
                ReadChoices();
                if (_draftId == Guid.Empty) LoadTemplate(null);
                RefreshInstances();
            };
        }

        private void ReadChoices()
        {
            var runtimeId = (RuntimeBox.SelectedItem as ComboBoxItem)?.Tag;
            RuntimeBox.Items.Clear();
            RuntimeBox.Items.Add(new ComboBoxItem { Content = "启动时选择" });
            foreach (var runtime in _store.Runtimes)
                RuntimeBox.Items.Add(new ComboBoxItem { Content = runtime.DisplayTitle + " · " + runtime.VersionLabel, Tag = runtime.Id });
            Select(RuntimeBox, runtimeId);

            var network = (NetworkBox.SelectedItem as ComboBoxItem)?.Tag;
            NetworkBox.Items.Clear();
            NetworkBox.Items.Add(new ComboBoxItem { Content = "沿用启动时的网络选项" });
            NetworkBox.Items.Add(new ComboBoxItem { Content = "直连", Tag = BrowserEnvironmentNetwork.Direct });
            foreach (var proxy in _store.Settings.RecentProxyPresets)
                NetworkBox.Items.Add(new ComboBoxItem { Content = string.IsNullOrWhiteSpace(proxy.Remark) ? proxy.Server : proxy.Remark + " · " + proxy.Server, Tag = proxy.Id });
            Select(NetworkBox, network);
        }

        private static void Select(ComboBox box, object? tag) =>
            box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, tag)) ?? box.Items[0];

        private void LoadTemplate(BrowserEnvironment? template)
        {
            _draftId = template?.Id ?? Guid.NewGuid();
            NameBox.Text = template?.Name ?? "";
            LanguageBox.Text = template?.Language ?? "";
            TimeZoneBox.Text = template?.TimeZone ?? "";
            WindowWidthBox.Text = template?.WindowWidth?.ToString() ?? "";
            WindowHeightBox.Text = template?.WindowHeight?.ToString() ?? "";
            ViewportWidthBox.Text = template?.ViewportWidth?.ToString() ?? "";
            ViewportHeightBox.Text = template?.ViewportHeight?.ToString() ?? "";
            Select(RuntimeBox, template?.RuntimeId);
            object? network = null;
            if (template?.Network == BrowserEnvironmentNetwork.Direct) network = BrowserEnvironmentNetwork.Direct;
            if (template?.Network == BrowserEnvironmentNetwork.SavedProxy) network = template.ProxyPresetId;
            Select(NetworkBox, network);
            DeleteButton.IsEnabled = template != null;
            SaveStatus.Text = "";
        }

        private void Templates_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (RuntimeBox.Items.Count == 0 || Templates.SelectedItem is not BrowserEnvironment selected) return;
            LoadTemplate(selected);
        }

        private void New_Click(object sender, RoutedEventArgs e)
        {
            Templates.SelectedItem = null;
            ReadChoices();
            LoadTemplate(null);
            NameBox.Focus();
        }

        private static int? Dimension(TextBox box)
        {
            var text = box.Text.Trim();
            if (text.Length == 0) return null;
            if (!int.TryParse(text, out var value)) throw new InvalidOperationException("宽和高请输入整数。");
            return value;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var network = (NetworkBox.SelectedItem as ComboBoxItem)?.Tag;
                _store.SaveBrowserEnvironment(new BrowserEnvironment
                {
                    Id = _draftId, Name = NameBox.Text, RuntimeId = (RuntimeBox.SelectedItem as ComboBoxItem)?.Tag as Guid?,
                    Language = LanguageBox.Text.Trim(), TimeZone = TimeZoneBox.Text.Trim(),
                    WindowWidth = Dimension(WindowWidthBox), WindowHeight = Dimension(WindowHeightBox),
                    ViewportWidth = Dimension(ViewportWidthBox), ViewportHeight = Dimension(ViewportHeightBox),
                    Network = network is Guid ? BrowserEnvironmentNetwork.SavedProxy : network is BrowserEnvironmentNetwork kind ? kind : BrowserEnvironmentNetwork.LaunchChoice,
                    ProxyPresetId = network as Guid?,
                });
                Templates.SelectedItem = _store.BrowserEnvironments.First(item => item.Id == _draftId);
                SaveStatus.Text = "已保存";
            }
            catch (Exception error) { SaveStatus.Text = error.Message; }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            _store.RemoveBrowserEnvironment(_draftId);
            New_Click(sender, e);
        }

        private void InstanceBox_DropDownOpened(object? sender, EventArgs e) => RefreshInstances();

        private void RefreshInstances()
        {
            var selected = (InstanceBox.SelectedItem as ComboBoxItem)?.Tag;
            InstanceBox.Items.Clear();
            foreach (var instance in _store.RunningInstances)
                InstanceBox.Items.Add(new ComboBoxItem { Content = instance.DockBadge + " · " + instance.RuntimeName + " · " + (instance.SettingsSnapshot?.Environment?.Name ?? "默认环境"), Tag = instance });
            InstanceBox.SelectedItem = InstanceBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => ReferenceEquals(item.Tag, selected));
            if (InstanceBox.SelectedIndex < 0 && InstanceBox.Items.Count > 0) InstanceBox.SelectedIndex = 0;
        }

        private async void Read_Click(object sender, RoutedEventArgs e)
        {
            if ((InstanceBox.SelectedItem as ComboBoxItem)?.Tag is not BrowserInstance instance)
            {
                Observed.Text = "请先启动一个浏览器实例。";
                return;
            }
            ReadButton.IsEnabled = false;
            try
            {
                var values = await _store.ReadBrowserEnvironmentAsync(instance);
                Observed.Text = $"{instance.DockBadge} · {values["url"]}\n语言 {values["language"]}　时区 {values["timezone"]}\n"
                    + $"窗口 {values["window_width"]} × {values["window_height"]}　视口 {values["viewport_width"]} × {values["viewport_height"]}\n"
                    + $"启动网络 {values["proxy"]}\n{values["user_agent"]}";
                if (values.Value<bool>("environment_connected") == false) Observed.Text += "\n环境连接已断开，请重新启动实例。";
            }
            catch (Exception error) { Observed.Text = error.Message; }
            finally { ReadButton.IsEnabled = true; }
        }
    }
}
