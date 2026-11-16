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
        private static readonly object Custom = new object();
        private readonly InstanceStore _store;
        private Guid _draftId;

        public BrowserEnvironmentPage(InstanceStore store)
        {
            _store = store;
            InitializeComponent();
            PopulateOptions();
            Templates.ItemsSource = store.BrowserEnvironments;
            Loaded += (_, __) =>
            {
                ReadChoices();
                if (_draftId == Guid.Empty)
                {
                    if (Templates.Items.Count > 0) Templates.SelectedIndex = 0;
                    else LoadTemplate(null);
                }
                RefreshTemplateList();
                RefreshInstances();
            };
        }

        private void PopulateOptions()
        {
            Add(ModeBox, "固定配置", false);
            Add(ModeBox, "新实例随机生成", true);
            Add(RegionBox, "单独选择语言与时区");
            Add(LanguageBox, "浏览器默认");
            Add(TimeZoneBox, "系统时区");
            Add(TimeZoneBox, "协调世界时 · UTC", "UTC");
            foreach (var region in BrowserEnvironmentSettings.Regions)
            {
                Add(RegionBox, region.Label, region.TimeZone);
                if (!LanguageBox.Items.OfType<ComboBoxItem>().Any(item => Equals(item.Tag, region.Language)))
                    Add(LanguageBox, System.Globalization.CultureInfo.GetCultureInfo(region.Language).NativeName + " · " + region.Language, region.Language);
                Add(TimeZoneBox, region.Label + " · " + region.TimeZone, region.TimeZone);
            }
            Add(LanguageBox, "自定义语言…", Custom);
            Add(TimeZoneBox, "自定义时区…", Custom);
            AddSizes(WindowBox, "浏览器默认", BrowserEnvironmentSettings.Windows);
            AddSizes(ViewportBox, "随窗口变化", BrowserEnvironmentSettings.Viewports);

            Add(PixelRatioBox, "浏览器默认");
            foreach (var ratio in new[] { 1.0, 1.25, 1.5, 2.0, 3.0 }) Add(PixelRatioBox, $"{ratio:0.##}×", ratio);
            Add(ColorSchemeBox, "浏览器默认", BrowserEnvironmentColorScheme.Default);
            Add(ColorSchemeBox, "浅色", BrowserEnvironmentColorScheme.Light);
            Add(ColorSchemeBox, "深色", BrowserEnvironmentColorScheme.Dark);
            Add(MotionBox, "浏览器默认", BrowserEnvironmentMotion.Default);
            Add(MotionBox, "正常动画", BrowserEnvironmentMotion.Normal);
            Add(MotionBox, "减少动画", BrowserEnvironmentMotion.Reduced);
            Add(WebRtcBox, "浏览器默认", BrowserEnvironmentWebRtc.Default);
            Add(WebRtcBox, "仅默认公网接口", BrowserEnvironmentWebRtc.PublicInterfaceOnly);
            Add(WebRtcBox, "禁用非代理 UDP", BrowserEnvironmentWebRtc.DisableNonProxiedUdp);
            Add(CookiesBox, "浏览器默认", BrowserEnvironmentCookies.Default);
            Add(CookiesBox, "阻止第三方 Cookie", BrowserEnvironmentCookies.BlockThirdParty);
            foreach (var permission in new[] { LocationBox, CameraBox, MicrophoneBox })
            {
                Add(permission, "浏览器默认", BrowserEnvironmentPermission.Default);
                Add(permission, "访问时询问", BrowserEnvironmentPermission.Ask);
                Add(permission, "阻止", BrowserEnvironmentPermission.Block);
            }
        }

        private static void Add(ComboBox box, string title, object? value = null) => box.Items.Add(new ComboBoxItem { Content = title, Tag = value });

        private static void AddSizes(ComboBox box, string defaultTitle, (int Width, int Height)[] sizes)
        {
            Add(box, defaultTitle);
            foreach (var size in sizes) Add(box, $"{size.Width} × {size.Height}", size);
            Add(box, "自定义尺寸…", Custom);
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

        private static object? Choice(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag;

        private static void Select(ComboBox box, object? tag, bool customFallback = false) =>
            box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, tag))
                ?? (customFallback ? box.Items[box.Items.Count - 1] : box.Items[0]);

        private void LoadRegionAndDisplay(BrowserEnvironment template)
        {
            RegionBox.SelectedIndex = 0;
            CustomLanguage.Text = template.Language;
            CustomTimeZone.Text = template.TimeZone;
            Select(LanguageBox, string.IsNullOrEmpty(template.Language) ? null : template.Language, true);
            Select(TimeZoneBox, string.IsNullOrEmpty(template.TimeZone) ? null : template.TimeZone, true);
            SetSize(WindowBox, WindowWidthBox, WindowHeightBox, template.WindowWidth, template.WindowHeight);
            SetSize(ViewportBox, ViewportWidthBox, ViewportHeightBox, template.ViewportWidth, template.ViewportHeight);
            var region = BrowserEnvironmentSettings.Regions.FirstOrDefault(item => item.Language == template.Language && item.TimeZone == template.TimeZone);
            Select(RegionBox, region.TimeZone);
        }

        private static void SetSize(ComboBox box, TextBox widthBox, TextBox heightBox, int? width, int? height)
        {
            widthBox.Text = width?.ToString() ?? "";
            heightBox.Text = height?.ToString() ?? "";
            Select(box, width is int w && height is int h ? (object)(w, h) : null, true);
        }

        private void LoadTemplate(BrowserEnvironment? template)
        {
            var value = template ?? new BrowserEnvironment();
            _draftId = value.Id;
            NameBox.Text = value.Name;
            LoadRegionAndDisplay(value);
            Select(ModeBox, value.RandomizeOnLaunch);
            Select(PixelRatioBox, value.PixelRatio);
            Select(ColorSchemeBox, value.ColorScheme);
            Select(MotionBox, value.Motion);
            Select(WebRtcBox, value.WebRtc);
            Select(CookiesBox, value.Cookies);
            Select(LocationBox, value.Location);
            Select(CameraBox, value.Camera);
            Select(MicrophoneBox, value.Microphone);
            Select(RuntimeBox, value.RuntimeId);
            object? network = null;
            if (template?.Network == BrowserEnvironmentNetwork.Direct) network = BrowserEnvironmentNetwork.Direct;
            if (template?.Network == BrowserEnvironmentNetwork.SavedProxy) network = template.ProxyPresetId;
            Select(NetworkBox, network);
            DeleteButton.IsEnabled = template != null;
            SaveStatus.Text = "";
        }

        private void Mode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var auto = Choice(ModeBox) is true;
            RegionalDisplayFields.Visibility = auto ? Visibility.Collapsed : Visibility.Visible;
            RandomHint.Visibility = auto ? Visibility.Visible : Visibility.Collapsed;
            GenerateButton.IsEnabled = !auto;
        }

        private void Region_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Choice(RegionBox) is not string zone) return;
            Select(LanguageBox, BrowserEnvironmentSettings.Regions.First(item => item.TimeZone == zone).Language);
            Select(TimeZoneBox, zone);
        }

        private void CustomChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var choice = (ComboBox)sender;
            var field = choice == LanguageBox ? (UIElement)CustomLanguage : choice == TimeZoneBox ? CustomTimeZone
                : choice == WindowBox ? CustomWindow : CustomViewport;
            field.Visibility = ReferenceEquals(Choice(choice), Custom) ? Visibility.Visible : Visibility.Collapsed;
            if ((choice == LanguageBox || choice == TimeZoneBox) && Choice(RegionBox) is string zone)
            {
                var region = BrowserEnvironmentSettings.Regions.First(item => item.TimeZone == zone);
                if (!(choice == LanguageBox ? Equals(Choice(choice), region.Language) : Equals(Choice(choice), zone)))
                    RegionBox.SelectedIndex = 0;
            }
        }

        private void Generate_Click(object sender, RoutedEventArgs e) =>
            LoadRegionAndDisplay(BrowserEnvironmentSettings.Randomized(new BrowserEnvironment()));

        private void RefreshTemplateList()
        {
            var hasTemplates = _store.BrowserEnvironments.Count > 0;
            Templates.Visibility = hasTemplates ? Visibility.Visible : Visibility.Collapsed;
            TemplatesEmpty.Visibility = hasTemplates ? Visibility.Collapsed : Visibility.Visible;
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

        private static (int?, int?) ReadSize(ComboBox box, TextBox width, TextBox height) => Choice(box) switch
        {
            ValueTuple<int, int> size => (size.Item1, size.Item2),
            var choice when ReferenceEquals(choice, Custom) => (Dimension(width), Dimension(height)),
            _ => (null, null),
        };

        private static string ReadText(ComboBox box, TextBox input) =>
            ReferenceEquals(Choice(box), Custom) ? input.Text.Trim() : Choice(box) as string ?? "";

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var network = Choice(NetworkBox);
                var auto = Choice(ModeBox) is true;
                var (windowWidth, windowHeight) = auto ? (null, null) : ReadSize(WindowBox, WindowWidthBox, WindowHeightBox);
                var (viewportWidth, viewportHeight) = auto ? (null, null) : ReadSize(ViewportBox, ViewportWidthBox, ViewportHeightBox);
                _store.SaveBrowserEnvironment(new BrowserEnvironment
                {
                    Id = _draftId, Name = NameBox.Text, RuntimeId = Choice(RuntimeBox) as Guid?, RandomizeOnLaunch = auto,
                    Language = auto ? "" : ReadText(LanguageBox, CustomLanguage), TimeZone = auto ? "" : ReadText(TimeZoneBox, CustomTimeZone),
                    WindowWidth = windowWidth, WindowHeight = windowHeight, ViewportWidth = viewportWidth, ViewportHeight = viewportHeight,
                    PixelRatio = Choice(PixelRatioBox) as double?, ColorScheme = (BrowserEnvironmentColorScheme)Choice(ColorSchemeBox)!,
                    Motion = (BrowserEnvironmentMotion)Choice(MotionBox)!, WebRtc = (BrowserEnvironmentWebRtc)Choice(WebRtcBox)!,
                    Cookies = (BrowserEnvironmentCookies)Choice(CookiesBox)!, Location = (BrowserEnvironmentPermission)Choice(LocationBox)!,
                    Camera = (BrowserEnvironmentPermission)Choice(CameraBox)!, Microphone = (BrowserEnvironmentPermission)Choice(MicrophoneBox)!,
                    Network = network is Guid ? BrowserEnvironmentNetwork.SavedProxy : network is BrowserEnvironmentNetwork kind ? kind : BrowserEnvironmentNetwork.LaunchChoice,
                    ProxyPresetId = network as Guid?,
                });
                RefreshTemplateList();
                Templates.SelectedItem = _store.BrowserEnvironments.First(item => item.Id == _draftId);
                SaveStatus.Text = "已保存";
            }
            catch (Exception error) { SaveStatus.Text = error.Message; }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            _store.RemoveBrowserEnvironment(_draftId);
            RefreshTemplateList();
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

        private static string PermissionLabel(string? state) => state switch
        {
            "granted" => "已允许", "denied" => "已阻止", "prompt" => "询问", _ => "不可读取",
        };

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
                var color = values.Value<string>("color_scheme") == "dark" ? "深色" : "浅色";
                var motion = values.Value<bool>("reduced_motion") ? "减少动画" : "普通动画";
                Observed.Text = $"{instance.DockBadge} · {values["url"]}\n语言 {values["language"]}　时区 {values["timezone"]}\n"
                    + $"窗口 {values["window_width"]} × {values["window_height"]}　视口 {values["viewport_width"]} × {values["viewport_height"]}\n"
                    + $"设备像素比 {values["pixel_ratio"]}×　{color}　{motion}\n"
                    + $"定位 {PermissionLabel(values.Value<string>("location"))}　摄像头 {PermissionLabel(values.Value<string>("camera"))}　麦克风 {PermissionLabel(values.Value<string>("microphone"))}\n"
                    + $"启动网络 {values["proxy"]}\n{values["user_agent"]}";
                if (values.Value<bool>("environment_connected") == false) Observed.Text += "\n环境连接已断开，请重新启动实例。";
            }
            catch (Exception error) { Observed.Text = error.Message; }
            finally { ReadButton.IsEnabled = true; }
        }
    }
}
