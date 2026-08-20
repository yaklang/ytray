#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
            NextBtn.Width = _step < 3 ? 80 : 88;
        }

        private void UpdateStepper()
        {
            for (int i = 0; i < 4; i++)
            {
                var reached = i <= _step;
                var completed = i < _step;
                if (FindName("Step" + i + "Dot") is Border dot)
                {
                    dot.SetResourceReference(Border.BackgroundProperty,
                        reached ? "BrandOrangeBrush" : "SurfaceMutedBrush");
                    dot.SetResourceReference(Border.BorderBrushProperty,
                        reached ? "BrandOrangeBrush" : "HairlineBrush");
                }
                if (FindName("Step" + i + "Number") is TextBlock number)
                {
                    number.Visibility = completed ? Visibility.Collapsed : Visibility.Visible;
                    if (reached) number.Foreground = Brushes.White;
                    else number.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
                }
                if (FindName("Step" + i + "Check") is System.Windows.Shapes.Path check)
                    check.Visibility = completed ? Visibility.Visible : Visibility.Collapsed;
                if (FindName("Step" + i + "Title") is TextBlock title)
                {
                    title.FontWeight = reached ? FontWeights.SemiBold : FontWeights.Normal;
                    title.SetResourceReference(TextBlock.ForegroundProperty,
                        reached ? "TextPrimaryBrush" : "TextSecondaryBrush");
                }
                if (i < 3 && FindName("Step" + i + "Connector") is Rectangle connector)
                    connector.SetResourceReference(Shape.FillProperty,
                        completed ? "BrandOrangeBrush" : "HairlineBrush");
            }
        }

        private UIElement BuildRuntimeStep()
        {
            var sp = new StackPanel();
            var label = new TextBlock { Text = "选择本次使用的浏览器", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 14) };
            sp.Children.Add(label);
            foreach (var rt in _store.Runtimes)
            {
                var content = new Grid();
                content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
                content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var iconBorder = new Border
                {
                    Width = 34,
                    Height = 34,
                    CornerRadius = new CornerRadius(9),
                    BorderThickness = new Thickness(1),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new Image
                    {
                        Source = BrowserIconSource.FromExecutable(rt.ExecutablePath),
                        Width = 25,
                        Height = 25,
                        Stretch = Stretch.Uniform,
                    },
                };
                iconBorder.SetResourceReference(Border.BackgroundProperty, "SurfaceMutedBrush");
                iconBorder.SetResourceReference(Border.BorderBrushProperty, "HairlineBrush");
                content.Children.Add(iconBorder);

                var detail = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                detail.Children.Add(new TextBlock
                {
                    Text = rt.DisplayTitle,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 12,
                });
                var meta = new TextBlock
                {
                    Text = $"{rt.VersionLabel} · {rt.Source.Title()}",
                    FontSize = 9.5,
                    Margin = new Thickness(0, 2, 0, 0),
                };
                meta.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                detail.Children.Add(meta);
                Grid.SetColumn(detail, 1);
                content.Children.Add(detail);

                if (_draft.DefaultRuntimeID == rt.Id)
                {
                    var selected = new TextBlock
                    {
                        Text = "当前选择",
                        FontSize = 9.5,
                        FontWeight = FontWeights.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(12, 0, 2, 0),
                    };
                    selected.SetResourceReference(TextBlock.ForegroundProperty, "BrandOrangeBrush");
                    Grid.SetColumn(selected, 2);
                    content.Children.Add(selected);
                }
                var btn = new Button
                {
                    Tag = rt,
                    Content = content,
                    Padding = new Thickness(12, 9, 12, 9),
                    Margin = new Thickness(0, 0, 0, 6),
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                };
                btn.Style = (Style)FindResource("YTrayButton");
                btn.SetResourceReference(Control.BackgroundProperty,
                    _draft.DefaultRuntimeID == rt.Id ? "BrandPaleBrush" : "SurfaceBrush");
                btn.SetResourceReference(Control.BorderBrushProperty,
                    _draft.DefaultRuntimeID == rt.Id ? "BrandOrangeBrush" : "HairlineBrush");
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
            var flagsLabel = new TextBlock { Text = "附加参数（每行一个）", FontSize = 11 };
            flagsLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            sp.Children.Add(flagsLabel);
            var flags = new TextBox
            {
                AcceptsReturn = true,
                Height = 104,
                FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
                TextWrapping = TextWrapping.NoWrap,
                Text = _draft.AdditionalFlags,
                VerticalContentAlignment = VerticalAlignment.Top,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                TextAlignment = TextAlignment.Left,
                Padding = new Thickness(11, 9, 11, 9),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
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
            {
                var empty = new TextBlock { Text = "没有可用的本地插件" };
                empty.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                sp.Children.Add(empty);
            }
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
            var dockBadge = _draft.DockBadge?.Trim();
            AddReviewRow(grid, 4, "Dock 角标", string.IsNullOrEmpty(dockBadge) ? "自动分配" : dockBadge!.ToUpperInvariant());
            AddReviewRow(grid, 5, "WebRTC", _draft.RestrictWebRTC ? "限制" : "不限制");
            AddReviewRow(grid, 6, "插件", $"{_pluginIDs.Count} 个");
            sp.Children.Add(grid);
            return sp;
        }

        private void AddReviewRow(Grid grid, int row, string key, string value)
        {
            var keyLabel = new TextBlock { Text = key, Margin = new Thickness(0, 4, 0, 4) };
            keyLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            grid.AddChild(keyLabel, row, 0);
            grid.AddChild(new TextBlock { Text = value, Margin = new Thickness(0, 4, 0, 4) }, row, 1);
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
