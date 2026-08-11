using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using YTray.Core;
using YTray.Models;

namespace YTray.Views
{
    public partial class CustomLaunchWizard : Window
    {
        private readonly InstanceStore _store;
        private int _step;
        private LaunchSettings _draft;
        private List<Guid> _pluginIDs;

        public CustomLaunchWizard(InstanceStore store)
        {
            InitializeComponent();
            _store = store;
            _draft = new LaunchSettings(store.Settings.DefaultRuntimeID)
            {
                HomeURL = store.Settings.HomeURL,
                DebugPort = store.Settings.DebugPort,
                RestrictWebRTC = store.Settings.RestrictWebRTC,
                DisableNotifications = store.Settings.DisableNotifications,
                IgnoreCertificateErrors = store.Settings.IgnoreCertificateErrors,
                AdditionalFlags = store.Settings.AdditionalFlags,
                DockBadge = store.Settings.DockBadge,
            };
            _pluginIDs = store.Settings.DefaultPluginIDs.ToList();
            ShowStep();
        }

        private void ShowStep()
        {
            ContentArea.Children.Clear();
            switch (_step)
            {
                case 0: ContentArea.Children.Add(BuildRuntimeStep()); break;
                case 1: ContentArea.Children.Add(BuildNetworkStep()); break;
                case 2: ContentArea.Children.Add(BuildPluginStep()); break;
                case 3: ContentArea.Children.Add(BuildReviewStep()); break;
            }
            UpdateStepper();
            PrevBtn.Visibility = _step > 0 ? Visibility.Visible : Visibility.Collapsed;
            NextBtn.Content = _step < 3 ? "下一步" : "启动实例";
        }

        private void UpdateStepper()
        {
            for (int i = 0; i < 4; i++)
            {
                var dot = (System.Windows.Shapes.Ellipse)FindName("Step" + i + "Dot");
                if (dot != null)
                    dot.Fill = (i <= _step)
                        ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF2, 0x8B, 0x44))
                        : System.Windows.Media.Brushes.LightGray;
            }
        }

        private UIElement BuildRuntimeStep()
        {
            var sp = new StackPanel();
            var label = new TextBlock { Text = "选择本次使用的浏览器", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 14) };
            sp.Children.Add(label);
            foreach (var rt in _store.Runtimes)
            {
                var btn = new Button
                {
                    Tag = rt,
                    Content = new TextBlock
                    {
                        Text = $"{rt.DisplayTitle} {rt.VersionLabel} · {rt.Source.Title()}",
                        Padding = new Thickness(14),
                    },
                    Padding = new Thickness(14),
                    Margin = new Thickness(0, 0, 0, 6),
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background = _draft.DefaultRuntimeID == rt.Id ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF2, 0x8B, 0x44)) { Opacity = 0.16 } : System.Windows.Media.Brushes.White,
                };
                btn.Click += (s, e) => { _draft.DefaultRuntimeID = ((BrowserRuntime)((Button)s).Tag).Id; ShowStep(); };
                sp.Children.Add(btn);
            }
            return sp;
        }

        private UIElement BuildNetworkStep()
        {
            var sp = new StackPanel();
            sp.Children.Add(MakeField("启动地址", out var urlBox));
            urlBox.Text = _draft.HomeURL;
            sp.Children.Add(MakeField("调试端口", out var portBox));
            portBox.Text = _draft.DebugPort.ToString();
            sp.Children.Add(MakeField("Dock 角标（留空自动分配 A/B/C…，可填 1–2 个字母）", out var badgeBox));
            badgeBox.Text = _draft.DockBadge;
            var webRtc = new CheckBox { Content = "限制 WebRTC 非代理 UDP 与本地 IP 暴露", IsChecked = _draft.RestrictWebRTC, Margin = new Thickness(0, 8, 0, 4) };
            var notif = new CheckBox { Content = "关闭通知", IsChecked = _draft.DisableNotifications, Margin = new Thickness(0, 0, 0, 4) };
            var cert = new CheckBox { Content = "忽略证书错误", IsChecked = _draft.IgnoreCertificateErrors, Margin = new Thickness(0, 0, 0, 8) };
            sp.Children.Add(webRtc); sp.Children.Add(notif); sp.Children.Add(cert);
            sp.Children.Add(new TextBlock { Text = "附加参数（每行一个）", FontSize = 11, Foreground = System.Windows.Media.Brushes.Gray });
            var flags = new TextBox { AcceptsReturn = true, Height = 90, FontFamily = new System.Windows.Media.FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap, Text = _draft.AdditionalFlags };
            sp.Children.Add(flags);
            // capture on Next
            sp.Tag = new System.Action(() =>
            {
                _draft.HomeURL = urlBox.Text;
                if (int.TryParse(portBox.Text, out int p)) _draft.DebugPort = p;
                _draft.DockBadge = badgeBox.Text;
                _draft.RestrictWebRTC = webRtc.IsChecked == true;
                _draft.DisableNotifications = notif.IsChecked == true;
                _draft.IgnoreCertificateErrors = cert.IsChecked == true;
                _draft.AdditionalFlags = flags.Text;
            });
            return sp;
        }

        private UIElement BuildPluginStep()
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = "选择本次加载的本地插件", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 12) });
            foreach (var p in _store.Plugins.Where(pl => pl.Enabled))
            {
                var cb = new CheckBox
                {
                    Content = $"{p.Name} v{p.Version} · Manifest V{p.ManifestVersion}",
                    IsChecked = _pluginIDs.Contains(p.Id),
                    Tag = p.Id,
                    Margin = new Thickness(0, 0, 0, 6),
                };
                cb.Checked += (s, e) => _pluginIDs.Add((Guid)((CheckBox)s).Tag);
                cb.Unchecked += (s, e) => _pluginIDs.Remove((Guid)((CheckBox)s).Tag);
                sp.Children.Add(cb);
            }
            if (_store.Plugins.Count == 0)
                sp.Children.Add(new TextBlock { Text = "没有本地插件", Foreground = System.Windows.Media.Brushes.Gray });
            return sp;
        }

        private UIElement BuildReviewStep()
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = "确认本次启动配置", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 18) });
            var rt = _store.Runtimes.FirstOrDefault(r => r.Id == _draft.DefaultRuntimeID);
            var grid = new Grid { Margin = new Thickness(18) };
            for (int i = 0; i < 7; i++) grid.RowDefinitions.Add(new RowDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            AddReviewRow(grid, 0, "浏览器", rt != null ? $"{rt.DisplayTitle} {rt.VersionLabel} · {rt.Source.Title()}" : "未选择");
            AddReviewRow(grid, 1, "启动地址", _draft.HomeURL);
            AddReviewRow(grid, 2, "网络", "直连（无代理）");
            AddReviewRow(grid, 3, "调试", $"127.0.0.1:{_draft.DebugPort} 起自动避让");
            AddReviewRow(grid, 4, "Dock 角标", string.IsNullOrEmpty(_draft.DockBadge?.Trim()) ? "自动分配" : _draft.DockBadge.ToUpper());
            AddReviewRow(grid, 5, "WebRTC", _draft.RestrictWebRTC ? "限制" : "不限制");
            AddReviewRow(grid, 6, "插件", $"{_pluginIDs.Count} 个");
            sp.Children.Add(grid);
            return sp;
        }

        private void AddReviewRow(Grid grid, int row, string key, string value)
        {
            grid.AddChild(new TextBlock { Text = key, Foreground = System.Windows.Media.Brushes.Gray }, row, 0);
            grid.AddChild(new TextBlock { Text = value }, row, 1);
        }

        private StackPanel MakeField(string label, out TextBox box)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            sp.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 3) });
            box = new TextBox { MinWidth = 300 };
            sp.Children.Add(box);
            return sp;
        }

        private void Prev_Click(object s, RoutedEventArgs e)
        {
            if (_step > 0) { _step--; ShowStep(); }
        }

        private void Next_Click(object s, RoutedEventArgs e)
        {
            // capture network-step draft
            if (ContentArea.Children[0] is StackPanel net && net.Tag is System.Action capture) capture();
            if (_step < 3) { _step++; ShowStep(); return; }
            _draft.DefaultPluginIDs = _pluginIDs.ToList();
            _store.Launch(LaunchMode.Custom, _draft, _pluginIDs, launchUsesProxy: false);
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object s, RoutedEventArgs e) { DialogResult = false; Close(); }
    }

    internal static class GridExtensions
    {
        public static void AddChild(this Grid g, UIElement child, int row, int col)
        {
            g.Children.Add(child);
            Grid.SetRow(child, row);
            Grid.SetColumn(child, col);
        }
    }
}