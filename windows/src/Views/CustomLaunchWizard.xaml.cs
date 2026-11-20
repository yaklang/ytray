#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using YTray.Core;
using YTray.Models;
using YTray.Native;

namespace YTray.Views
{
    public partial class CustomLaunchWizard : Window
    {
        private readonly InstanceStore _store;
        private int _step;
        private LaunchSettings _draft;
        private List<Guid> _pluginIDs;
        private bool _usePresetProxy;

        public CustomLaunchWizard(InstanceStore store, BrowserEnvironment? environment = null, BrowserEngineTarget? engineTarget = null)
        {
            InitializeComponent();
            WindowChromeHelper.Attach(this);
            _store = store;
            _draft = new LaunchSettings(store.Settings.DefaultRuntimeID)
            {
                HomeURL = store.Settings.HomeURL,
                Environment = environment?.Clone(),
                EngineTarget = engineTarget?.Clone() ?? store.DefaultBrowserEngine,
                DebugPort = store.Settings.DebugPort,
                RestrictWebRTC = store.Settings.RestrictWebRTC,
                DisableNotifications = store.Settings.DisableNotifications,
                IgnoreCertificateErrors = store.Settings.IgnoreCertificateErrors,
                UseTestType = store.Settings.UseTestType,
                ColorizeBrowserInstances = store.Settings.ColorizeBrowserInstances,
                AdditionalFlags = store.Settings.AdditionalFlags,
                ProfileRootPath = store.Settings.ProfileRootPath,
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

        private static TextBlock MakeWizardFieldLabel(string text)
        {
            var label = new TextBlock
            {
                Text = text,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6),
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            return label;
        }

        private UIElement BuildRuntimeStep()
        {
            var sp = new StackPanel();
            var contextGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            contextGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            contextGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            contextGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var envPanel = new StackPanel();
            envPanel.Children.Add(MakeWizardFieldLabel("环境模板"));
            var environment = new ComboBox { Height = 36 };
            AutomationProperties.SetName(environment, "浏览器环境");
            environment.Items.Add(new ComboBoxItem { Content = "默认启动设置" });
            foreach (var item in _store.BrowserEnvironments)
                environment.Items.Add(new ComboBoxItem { Content = item.Name, Tag = item });
            environment.SelectedItem = environment.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => (item.Tag as BrowserEnvironment)?.Id == _draft.Environment?.Id) ?? environment.Items[0];
            environment.SelectionChanged += (_, __) =>
            {
                _draft.Environment = ((environment.SelectedItem as ComboBoxItem)?.Tag as BrowserEnvironment)?.Clone();
                Dispatcher.BeginInvoke(new Action(() => { if (_step == 0) ShowStep(); }));
            };
            envPanel.Children.Add(environment);
            contextGrid.Children.Add(envPanel);

            var enginePanel = new StackPanel();
            enginePanel.Children.Add(MakeWizardFieldLabel("接入应用"));
            var engine = new BrowserEnginePicker { Height = 36 };
            AutomationProperties.SetName(engine, "接入应用");
            engine.Configure(_draft.EngineTarget);
            engine.SelectionChanged += (_, __) =>
            {
                if (engine.Target is BrowserEngineTarget selected) _draft.EngineTarget = selected;
            };
            enginePanel.Children.Add(engine);
            Grid.SetColumn(enginePanel, 2);
            contextGrid.Children.Add(enginePanel);
            sp.Children.Add(contextGrid);

            var label = MakeWizardFieldLabel("选择本次使用的浏览器");
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

                if ((_draft.Environment?.RuntimeId ?? _draft.DefaultRuntimeID) == rt.Id)
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
                    IsEnabled = _draft.Environment?.RuntimeId == null,
                    Content = content,
                    Padding = new Thickness(12, 9, 12, 9),
                    Margin = new Thickness(0, 0, 0, 8),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                };
                btn.Style = (Style)FindResource("YTrayButton");
                btn.SetResourceReference(Control.BackgroundProperty,
                    (_draft.Environment?.RuntimeId ?? _draft.DefaultRuntimeID) == rt.Id ? "BrandPaleBrush" : "SurfaceBrush");
                btn.SetResourceReference(Control.BorderBrushProperty,
                    (_draft.Environment?.RuntimeId ?? _draft.DefaultRuntimeID) == rt.Id ? "BrandOrangeBrush" : "HairlineBrush");
                btn.Click += (s, e) => { _draft.DefaultRuntimeID = ((BrowserRuntime)((Button)s).Tag).Id; ShowStep(); };
                sp.Children.Add(btn);
            }
            sp.Children.Add(BuildProfileRootChoice());
            return sp;
        }

        private UIElement BuildProfileRootChoice()
        {
            var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = "本次实例数据", FontWeight = FontWeights.SemiBold, FontSize = 11.5 });
            var path = new TextBlock
            {
                Text = _store.ResolveProfileRoot(_draft),
                FontSize = 10,
                Margin = new Thickness(0, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = _store.ResolveProfileRoot(_draft),
            };
            path.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            text.Children.Add(path);
            text.Children.Add(new TextBlock
            {
                Text = "YTray 会在该父目录下创建独立子目录",
                FontSize = 9.5,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = (Brush)FindResource("TextTertiaryBrush"),
            });
            grid.Children.Add(text);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (!string.Equals(_draft.ProfileRootPath ?? "", _store.Settings.ProfileRootPath ?? "",
                    StringComparison.OrdinalIgnoreCase))
            {
                var reset = new Button { Content = "使用默认", Height = 32, Margin = new Thickness(0, 0, 7, 0) };
                reset.Style = (Style)FindResource("YTrayGhostButton");
                reset.Click += (s, e) => { _draft.ProfileRootPath = _store.Settings.ProfileRootPath ?? ""; ShowStep(); };
                actions.Children.Add(reset);
            }
            var choose = new Button { Content = "选择目录", Height = 32 };
            choose.Style = (Style)FindResource("YTrayButton");
            choose.Click += (s, e) => ChooseLaunchProfileRoot();
            actions.Children.Add(choose);
            Grid.SetColumn(actions, 1);
            grid.Children.Add(actions);
            var border = new Border
            {
                Child = grid,
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 9, 10, 9),
            };
            border.SetResourceReference(Border.BackgroundProperty, "SurfaceMutedBrush");
            border.SetResourceReference(Border.BorderBrushProperty, "HairlineBrush");
            return border;
        }

        private void ChooseLaunchProfileRoot()
        {
            try
            {
                var path = FolderPicker.PickSingle(this,
                    "选择本次浏览器实例数据的父目录（YTray 会创建独立子目录）",
                    _store.ResolveProfileRoot(_draft));
                if (path == null) return;
                if (!_store.TryPrepareProfileRoot(path, out var normalizedPath, out var error))
                {
                    MessageBox.Show(this, error, "无法使用数据目录", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                _draft.ProfileRootPath = normalizedPath;
            }
            catch (Exception ex)
            {
                CrashGuard.Record("pick-launch-profile-root", ex);
                MessageBox.Show(this, "无法打开目录选择器：" + ex.Message,
                    "YTray", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ShowStep();
        }

        private UIElement BuildNetworkStep()
        {
            var sp = new StackPanel();
            var proxyAddress = (_store.Settings.PresetProxyServer ?? LaunchSettings.DefaultPresetProxyServer)
                .Replace("http://", "").Replace("https://", "");
            sp.Children.Add(new TextBlock
            {
                Text = "网络路径",
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                Margin = new Thickness(0, 0, 0, 6),
            });

            var networkChoices = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            networkChoices.ColumnDefinitions.Add(new ColumnDefinition());
            networkChoices.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            networkChoices.ColumnDefinitions.Add(new ColumnDefinition());
            var direct = MakeNetworkChoiceCard("直连无代理", "直接连接互联网或内网目标", !_usePresetProxy);
            var proxy = MakeNetworkChoiceCard("使用预设 HTTP 代理", proxyAddress, _usePresetProxy);
            void SyncCards()
            {
                ApplyChoiceCardStyle(direct, !_usePresetProxy);
                ApplyChoiceCardStyle(proxy, _usePresetProxy);
            }
            direct.Click += (s, e) => { _usePresetProxy = false; SyncCards(); };
            proxy.Click += (s, e) => { _usePresetProxy = true; SyncCards(); };
            SyncCards();
            networkChoices.Children.Add(direct);
            Grid.SetColumn(proxy, 2);
            networkChoices.Children.Add(proxy);
            var environmentNetwork = _store.EnvironmentNetworkDescription(_draft.Environment);
            if (environmentNetwork == null) sp.Children.Add(networkChoices);
            else sp.Children.Add(new TextBlock { Text = environmentNetwork, Margin = new Thickness(0, 0, 0, 12) });
            sp.Children.Add(MakeField("起始地址", out var urlBox));
            urlBox.Text = _draft.HomeURL;

            var rowGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
            var portField = MakeField("调试端口（占用自动递增）", out var portBox);
            portField.Margin = new Thickness(0);
            portBox.Text = _draft.DebugPort.ToString();
            var badgeField = MakeField("身份角标（留空自动分配 A/B/C…）", out var badgeBox);
            badgeField.Margin = new Thickness(0);
            badgeBox.Text = _draft.DockBadge;
            rowGrid.Children.Add(portField);
            Grid.SetColumn(badgeField, 2);
            rowGrid.Children.Add(badgeField);
            sp.Children.Add(rowGrid);

            var checkGrid = new Grid { Margin = new Thickness(0, 4, 0, 10) };
            checkGrid.ColumnDefinitions.Add(new ColumnDefinition());
            checkGrid.ColumnDefinitions.Add(new ColumnDefinition());
            checkGrid.RowDefinitions.Add(new RowDefinition());
            checkGrid.RowDefinitions.Add(new RowDefinition());
            var webRtc = new CheckBox { Content = "限制 WebRTC 暴露真实 IP", IsChecked = _draft.RestrictWebRTC, Margin = new Thickness(0, 0, 10, 8) };
            var cert = new CheckBox { Content = "忽略 HTTPS 证书错误", IsChecked = _draft.IgnoreCertificateErrors, Margin = new Thickness(0, 0, 0, 8) };
            var notif = new CheckBox { Content = "屏蔽桌面通知弹窗", IsChecked = _draft.DisableNotifications, Margin = new Thickness(0, 0, 10, 0) };
            var testType = new CheckBox { Content = "测试模式 (--test-type)", IsChecked = _draft.UseTestType };
            checkGrid.AddChild(webRtc, 0, 0);
            checkGrid.AddChild(cert, 0, 1);
            checkGrid.AddChild(notif, 1, 0);
            checkGrid.AddChild(testType, 1, 1);
            sp.Children.Add(checkGrid);

            var flagsLabel = new TextBlock { Text = "附加命令行参数（每行一个）", FontSize = 11.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5) };
            flagsLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            sp.Children.Add(flagsLabel);
            var flags = new TextBox
            {
                AcceptsReturn = true,
                Height = 82,
                FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
                TextWrapping = TextWrapping.NoWrap,
                Text = _draft.AdditionalFlags,
                VerticalContentAlignment = VerticalAlignment.Top,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                TextAlignment = TextAlignment.Left,
                Padding = new Thickness(11, 8, 11, 8),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            flags.SetResourceReference(Control.BackgroundProperty, "SurfaceMutedBrush");
            sp.Children.Add(flags);

            sp.Tag = new Func<bool>(() =>
            {
                _draft.HomeURL = urlBox.Text;
                if (int.TryParse(portBox.Text, out int p)) _draft.DebugPort = p;
                _draft.DockBadge = badgeBox.Text;
                _draft.RestrictWebRTC = webRtc.IsChecked == true;
                _draft.DisableNotifications = notif.IsChecked == true;
                _draft.IgnoreCertificateErrors = cert.IsChecked == true;
                _draft.UseTestType = testType.IsChecked == true;
                _draft.AdditionalFlags = flags.Text;
                if (!ApplySelectedNetwork()) return false;
                return true;
            });
            return sp;
        }

        private Button MakeNetworkChoiceCard(string title, string subtitle, bool selected)
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 12.5 });
            var secondary = new TextBlock { Text = subtitle, FontSize = 10.5, Margin = new Thickness(0, 3, 0, 0) };
            secondary.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            text.Children.Add(secondary);
            return new Button
            {
                Content = text,
                Style = (Style)FindResource("YTrayButton"),
                Padding = new Thickness(13, 9, 13, 9),
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
        }

        private void ApplyChoiceCardStyle(Button card, bool selected)
        {
            card.SetResourceReference(Control.BackgroundProperty, selected ? "BrandPaleBrush" : "SurfaceMutedBrush");
            card.SetResourceReference(Control.BorderBrushProperty, selected ? "BrandOrangeBrush" : "HairlineBrush");
        }

        private bool ApplySelectedNetwork()
        {
            if (!_usePresetProxy || _store.EnvironmentNetworkDescription(_draft.Environment) != null)
            {
                _draft.ProxyServer = "";
                _draft.ProxyUsername = "";
                _draft.ProxyPassword = "";
                return true;
            }

            try
            {
                _draft.ProxyServer = HTTPProxyAddress.Build(
                    _store.Settings.PresetProxyScheme,
                    _store.Settings.PresetProxyHost,
                    _store.Settings.PresetProxyPort);
                _draft.ProxyUsername = _store.Settings.PresetProxyUsername;
                _draft.ProxyPassword = _store.Settings.PresetProxyPassword;
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "HTTP 代理配置无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        private UIElement BuildPluginStep()
        {
            var sp = new StackPanel();
            var runtime = _store.Runtimes.FirstOrDefault(r => r.Id == _draft.DefaultRuntimeID);
            var supportsPlugins = runtime != null;
            sp.Children.Add(new TextBlock { Text = "选择本次加载的本地插件", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 12) });
            if (runtime != null && !BrowserLauncher.SupportsCommandLineExtensions(runtime.Kind))
            {
                var notice = new TextBlock
                {
                    Text = $"{runtime.DisplayTitle} 将在启动后验证插件加载状态，并尝试通过浏览器扩展接口补载。仅在加载失败时询问是否跳过插件。",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11,
                };
                notice.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                var noticeBorder = new Border
                {
                    Child = notice,
                    CornerRadius = new CornerRadius(8),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(12, 9, 12, 9),
                    Margin = new Thickness(0, 0, 0, 12),
                };
                noticeBorder.SetResourceReference(Border.BackgroundProperty, "SurfaceMutedBrush");
                noticeBorder.SetResourceReference(Border.BorderBrushProperty, "HairlineBrush");
                sp.Children.Add(noticeBorder);
            }
            foreach (var p in _store.Plugins.Where(pl => pl.Enabled))
            {
                var cb = new CheckBox
                {
                    Content = $"{p.Name} v{p.Version} · Manifest V{p.ManifestVersion}",
                    IsChecked = supportsPlugins && _pluginIDs.Contains(p.Id),
                    IsEnabled = supportsPlugins,
                    Tag = p.Id,
                    Margin = new Thickness(0, 0, 0, 6),
                };
                cb.Checked += (s, e) => _pluginIDs.Add((Guid)((CheckBox)s).Tag);
                cb.Unchecked += (s, e) => _pluginIDs.Remove((Guid)((CheckBox)s).Tag);
                sp.Children.Add(cb);
            }
            if (!_store.Plugins.Any(plugin => plugin.Enabled))
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
            var rt = _store.Runtimes.FirstOrDefault(r => r.Id == (_draft.Environment?.RuntimeId ?? _draft.DefaultRuntimeID));
            var grid = new Grid { Margin = new Thickness(18) };
            for (int i = 0; i < 11; i++) grid.RowDefinitions.Add(new RowDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            AddReviewRow(grid, 0, "浏览器", rt != null ? $"{rt.DisplayTitle} {rt.VersionLabel} · {rt.Source.Title()}" : "未选择");
            AddReviewRow(grid, 1, "启动地址", _draft.HomeURL);
            AddReviewRow(grid, 2, "网络", _store.EnvironmentNetworkDescription(_draft.Environment) ?? (_usePresetProxy
                ? $"HTTP 代理 · {_draft.ProxyServer.Replace("http://", "").Replace("https://", "")}"
                : "直连（无代理）"));
            AddReviewRow(grid, 3, "调试", $"127.0.0.1:{_draft.DebugPort} 起自动避让");
            var dockBadge = _draft.DockBadge?.Trim();
            AddReviewRow(grid, 4, "Dock 角标", string.IsNullOrEmpty(dockBadge) ? "自动分配" : dockBadge!.ToUpperInvariant());
            AddReviewRow(grid, 5, "WebRTC", _draft.RestrictWebRTC ? "限制" : "不限制");
            AddReviewRow(grid, 6, "插件", $"{_pluginIDs.Count} 个（启动时验证）");
            AddReviewRow(grid, 7, "Profile 主题", _draft.ColorizeBrowserInstances ? "按角标分配独立主题色" : "使用浏览器默认主题");
            AddReviewRow(grid, 8, "实例数据", _store.ResolveProfileRoot(_draft));
            AddReviewRow(grid, 9, "浏览器环境", _draft.Environment?.Name ?? "默认启动设置");
            AddReviewRow(grid, 10, "接入应用", _draft.EngineTarget?.DisplayName ?? "自动选择");
            sp.Children.Add(grid);
            return sp;
        }

        private void AddReviewRow(Grid grid, int row, string key, string value)
        {
            var keyLabel = new TextBlock { Text = key, Margin = new Thickness(0, 5, 0, 5), FontSize = 12.5 };
            keyLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            grid.AddChild(keyLabel, row, 0);
            var valLabel = new TextBlock
            {
                Text = value,
                Margin = new Thickness(0, 5, 0, 5),
                FontSize = 12.5,
                FontWeight = FontWeights.Medium,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = value,
            };
            grid.AddChild(valLabel, row, 1);
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
            if (ContentArea.Children[0] is FrameworkElement content && content.Tag is Func<bool> capture && !capture())
                return;
            if (_step < 3) { _step++; ShowStep(); return; }
            var runtime = _store.Runtimes.FirstOrDefault(r => r.Id == _draft.DefaultRuntimeID);
            var effectivePluginIDs = _pluginIDs.ToList();
            _draft.DefaultPluginIDs = effectivePluginIDs;
            if (!_store.Launch(LaunchMode.Custom, _draft, effectivePluginIDs, launchUsesProxy: _usePresetProxy))
            {
                if (_store.LaunchWasCancelled) return;
                MessageBox.Show(this,
                    _store.ErrorMessage ?? "无法启动浏览器，请检查当前配置。",
                    "无法启动实例",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
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
