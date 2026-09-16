#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Newtonsoft.Json;
using YTray.Models;
using YTray.Views;

namespace YTray.Core
{
    /// <summary>Local, deterministic WPF renders of the widget. Never loads user profiles or starts browsers.</summary>
    internal static class WidgetReviewCapture
    {
        internal static InstanceStore CreateStore(string directory)
        {
            var store = new InstanceStore(directory, discoverSystemBrowsers: false, runMaintenance: false);
            store.Instances.Clear();
            store.Runtimes.Clear();
            var runtime = new BrowserRuntime
            {
                Name = "Google Chrome", Version = "151.0.7922.172", BrowserKind = BrowserKind.Chrome,
                Source = RuntimeSource.System, ExecutablePath = "",
            };
            store.Runtimes.Add(runtime);
            store.Settings.DefaultRuntimeID = runtime.Id;
            store.Settings.PresetProxyHost = "127.0.0.1";
            store.Settings.PresetProxyScheme = ProxyScheme.Http;
            store.Settings.PresetProxyPort = 8083;
            store.Settings.PresetProxyUsername = store.Settings.PresetProxyPassword = store.Settings.PresetProxyRemark = "";
            store.Settings.PresetProxyCheckTarget = "";
            store.Settings.RecentProxyPresets.Clear();
            store.IsProxyAdvancedExpanded = true;
            var titles = new[] { "页面内容自动缩略图", "页面内容自动缩略图", "登录页调试", "项目文档", "代理设置" };
            var colors = new[] { "#FF9F0A", "#0A84FF", "#32D74B", "#BF5AF2", "#64D2E3" };
            for (var index = 0; index < titles.Length; index++)
            {
                var path = Path.Combine(directory, "preview-" + index + ".png");
                DrawThumbnail(path, titles[index], colors[index]);
                store.Instances.Add(new BrowserInstance
                {
                    Name = index < 2 ? "运行任务 " + (index + 1) : "历史任务 " + (index - 1),
                    DockBadge = DockBadgeLabel.DefaultLabel(index < 2 ? index + 1 : index - 1),
                    RuntimeID = runtime.Id, RuntimeName = runtime.Name, RuntimeVersion = runtime.Version,
                    RuntimeKind = runtime.Kind, RuntimeSource = runtime.Source,
                    ProcessID = 9527 + index, DebugPort = 9222 + index,
                    Status = index < 2 ? InstanceStatus.Running : InstanceStatus.Stopped,
                    LastPageTitle = titles[index], LastPageURL = "https://example.com/",
                    ThumbnailPath = path, ThumbnailUpdatedAt = DateTime.Now,
                    ProfilePath = Path.Combine(directory, "Profiles", index.ToString()),
                });
            }
            return store;
        }

        public static async Task RunAsync(string directory, bool preview)
        {
            directory = Path.GetFullPath(directory);
            Directory.CreateDirectory(directory);
            var store = CreateStore(Path.Combine(directory, "fixture-state"));
            ThemeManager.Initialize(AppThemePreference.Dark);
            var widget = new WidgetView(store, new LaunchAtLoginManager(new PreviewLaunchAtLoginBackend()))
            {
                Left = 80, Top = 40, ShowActivated = true, ShowInTaskbar = preview,
            };
            widget.Show();
            widget.PinBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await SettleAsync(widget, preview);
            // Unpin visually for the reference; the renderer executes before any focus change.
            widget.PinBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Render(widget, Path.Combine(directory, "widget-dark-expanded.png"));
            File.WriteAllText(Path.Combine(directory, "metrics.json"), JsonConvert.SerializeObject(new
            {
                width = widget.ActualWidth, height = widget.ActualHeight,
                proxyHeight = widget.ProxyCard.ActualHeight,
                inputHeight = widget.HostBox.ActualHeight, dropdownHeight = widget.SchemeCombo.ActualHeight,
                buttonHeight = widget.SaveBtn.ActualHeight, footerHeight = widget.StartupBtn.ActualHeight,
                runningRows = widget.RunningList.Items.Count, historyRows = widget.HistoryList.Items.Count,
                scale = 2, renderer = "WPF RenderTargetBitmap",
            }, Formatting.Indented));
            if (preview)
            {
                widget.PinBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                widget.Show();
                widget.Activate();
                widget.Closed += (s, e) => { store.Dispose(); Application.Current.Shutdown(); };
                widget.IsVisibleChanged += (s, e) => { if (!widget.IsVisible) widget.Close(); };
                return;
            }
            try
            {
                widget.AdvancedBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await SettleAsync(widget);
                Render(widget, Path.Combine(directory, "widget-dark-collapsed.png"));
                widget.AdvancedBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ThemeManager.SetPreference(AppThemePreference.Light);
                await SettleAsync(widget);
                Render(widget, Path.Combine(directory, "widget-light-expanded.png"));
                ThemeManager.SetPreference(AppThemePreference.Dark);
                store.Instances.Clear();
                await SettleAsync(widget);
                Render(widget, Path.Combine(directory, "widget-dark-empty.png"));
                File.WriteAllText(Path.Combine(directory, "capture-complete.txt"), "Native WPF widget capture completed.");
            }
            finally { widget.Close(); store.Dispose(); }
        }

        private static async Task SettleAsync(WidgetView widget, bool preview = false)
        {
            widget.CancelPendingDismiss();
            widget.RefreshAndMeasure();
            await Task.Delay(250);
            await widget.Dispatcher.InvokeAsync(() =>
            {
                widget.RefreshAndMeasure();
                if (!preview) PrepareFullCapture(widget);
            }, DispatcherPriority.ApplicationIdle);
        }

        internal static void PrepareFullCapture(WidgetView widget)
        {
            // CI desktops may be only 768 px tall. Render the complete fixture even when
            // part of the HWND is offscreen; interactive widgets still respect the work area.
            widget.MaxHeight = double.PositiveInfinity;
            widget.UpdateLayout();
        }

        internal static void Render(WidgetView widget, string path)
        {
            widget.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Round(widget.ActualWidth * 2),
                (int)Math.Round(widget.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
            bitmap.Render(widget);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
        }

        private static void DrawThumbnail(string path, string title, string color)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var accent = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(57, 57, 57)), null, new Rect(0, 0, 152, 96), 10, 10);
                dc.DrawText(new FormattedText("YTray 使用说明", CultureInfo.GetCultureInfo("zh-CN"),
                    FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 4, Brushes.White, 1), new Point(7, 5));
                dc.DrawRectangle(accent, null, new Rect(1, 24, 150, 63));
                dc.DrawRoundedRectangle(Brushes.White, null, new Rect(7, 33, 138, 46), 3, 3);
                dc.DrawText(new FormattedText(title, CultureInfo.GetCultureInfo("zh-CN"),
                    FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 5, Brushes.DimGray, 1), new Point(37, 53));
            }
            var bitmap = new RenderTargetBitmap(304, 192, 192, 192, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
        }
    }
}
