#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YTray.Models;
using YTray.Views;
using YTray.Views.Pages;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace YTray.Core
{
    /// <summary>
    /// Deterministic, opt-in design review capture. This runs only for
    /// --capture-design-review and never persists its temporary theme changes.
    /// </summary>
    internal static class DesignCaptureService
    {
        internal sealed class CaptureItem
        {
            public string RelativePath { get; set; } = "";
            public string Caption { get; set; } = "";
        }

        private sealed class MainPageCapture
        {
            public string Slug { get; set; } = "";
            public string Caption { get; set; } = "";
            public Func<ManagerView, ListBoxItem> Select { get; set; } = null!;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string? className, string? windowName);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        [DllImport("user32.dll")]
        private static extern bool EndMenu();

        public static async Task<IReadOnlyList<CaptureItem>> CaptureAsync(InstanceStore store, string outputDirectory,
            bool sitePreviewOnly = false)
        {
            if (store == null) throw new ArgumentNullException(nameof(store));
            if (string.IsNullOrWhiteSpace(outputDirectory)) throw new ArgumentException("截图目录不能为空", nameof(outputDirectory));

            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(Path.Combine(outputDirectory, "main"));
            Directory.CreateDirectory(Path.Combine(outputDirectory, "wizard"));
            Directory.CreateDirectory(Path.Combine(outputDirectory, "floating"));

            var captures = new List<CaptureItem>();
            var originalTheme = store.Settings.ThemePreference;
            var originalDockSide = store.Settings.EdgeDockOnLeft;
            try
            {
                if (sitePreviewOnly)
                {
                    await CaptureSitePreviewAsync(store, outputDirectory, captures);
                }
                else
                {
                    await CaptureManagerAsync(store, outputDirectory, captures);
                    await CaptureWizardAsync(store, outputDirectory, captures);
                    await CaptureFloatingAsync(store, outputDirectory, captures);
                    CaptureTrayContextMenu(outputDirectory, captures);
                }
                CreateContactSheet(outputDirectory, captures);
                WriteManifest(outputDirectory, captures);
                File.WriteAllText(Path.Combine(outputDirectory, "capture-complete.txt"),
                    $"YTray design capture completed at {DateTimeOffset.Now:O}{Environment.NewLine}" +
                    $"PNG count: {captures.Count + 1}{Environment.NewLine}");
                return captures;
            }
            finally
            {
                store.Settings.EdgeDockOnLeft = originalDockSide;
                // A process-state timer may save while a preview theme is active. Restore through
                // the normal persistence entry point so design capture cannot leak its temporary
                // theme or dock side into the user's state.json.
                store.SetThemePreference(originalTheme);
            }
        }

        private static async Task CaptureSitePreviewAsync(InstanceStore store, string root,
            List<CaptureItem> captures)
        {
            ApplyPreviewTheme(store, AppThemePreference.Dark);
            var launchAtLogin = new LaunchAtLoginManager(new PreviewLaunchAtLoginBackend());
            var manager = new ManagerView(store, launchAtLogin)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 160,
                Top = 90,
                Topmost = true,
            };
            manager.Show();
            manager.Activate();
            await SettleAsync(manager, 480);

            manager.NavQuick.IsSelected = true;
            await SettleAsync(manager, 360);
            const string overviewRelative = "main/dark-overview-runtime-center.png";
            CaptureWindow(manager, Path.Combine(root, overviewRelative));
            captures.Add(new CaptureItem { RelativePath = overviewRelative, Caption = "运行中心 · 深色" });

            manager.NavInstances.IsSelected = true;
            await SettleAsync(manager, 420);
            if (manager.ContentFrame.Content is InstancesPage instancesPage)
            {
                var candidate = instancesPage.RunningList.Items.Count > 0
                    ? instancesPage.RunningList.Items[0]
                    : (instancesPage.HistoryList.Items.Count > 0 ? instancesPage.HistoryList.Items[0] : null);
                if (candidate != null)
                {
                    if (instancesPage.RunningList.Items.Contains(candidate))
                        instancesPage.RunningList.SelectedItem = candidate;
                    else
                        instancesPage.HistoryList.SelectedItem = candidate;
                    await SettleAsync(manager, 720);
                }
            }
            const string detailRelative = "main/dark-instances-detail.png";
            CaptureWindow(manager, Path.Combine(root, detailRelative));
            captures.Add(new CaptureItem { RelativePath = detailRelative, Caption = "浏览器实例 · 详情 · 深色" });
            manager.Close();
            await Task.Delay(180);

            var widget = new WidgetView(store) { Topmost = true };
            widget.Show();
            widget.Opacity = 1;
            widget.EntranceTransform.Y = 0;
            widget.RefreshAndMeasure();
            PositionWidgetForReview(widget);
            widget.CancelPendingDismiss();
            await SettleAsync(widget, 520);
            const string widgetRelative = "floating/dark-floating-panel.png";
            // Render the transparent WPF surface itself. A desktop screen copy bakes whatever is
            // behind the rounded window into its transparent corners, which creates stray text
            // when the widget is composited over the website hero.
            CaptureWindow(widget, Path.Combine(root, widgetRelative));
            captures.Add(new CaptureItem { RelativePath = widgetRelative, Caption = "普通悬浮面板 · 深色" });
            widget.Close();
            await Task.Delay(120);
        }

        private static async Task CaptureManagerAsync(InstanceStore store, string root, List<CaptureItem> captures)
        {
            var launchAtLogin = new LaunchAtLoginManager(new PreviewLaunchAtLoginBackend());
            var manager = new ManagerView(store, launchAtLogin)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 160,
                Top = 90,
                Topmost = true,
            };
            manager.Show();
            manager.Activate();
            await SettleAsync(manager, 420);

            var pages = new[]
            {
                new MainPageCapture { Slug = "overview-runtime-center", Caption = "运行中心", Select = w => w.NavQuick },
                new MainPageCapture { Slug = "instances", Caption = "浏览器实例", Select = w => w.NavInstances },
                new MainPageCapture { Slug = "browser-sources", Caption = "浏览器来源", Select = w => w.NavRuntimes },
                new MainPageCapture { Slug = "proxy-and-launch", Caption = "代理与启动", Select = w => w.NavLaunch },
                new MainPageCapture { Slug = "plugins", Caption = "本地插件", Select = w => w.NavPlugins },
                new MainPageCapture { Slug = "startup", Caption = "开机启动", Select = w => w.NavStartup },
                new MainPageCapture { Slug = "settings", Caption = "设置", Select = w => w.NavSettings },
            };

            foreach (var theme in new[] { AppThemePreference.Light, AppThemePreference.Dark })
            {
                ApplyPreviewTheme(store, theme);
                await SettleAsync(manager, 250);
                var themeName = theme == AppThemePreference.Dark ? "dark" : "light";

                foreach (var page in pages)
                {
                    page.Select(manager).IsSelected = true;
                    await SettleAsync(manager, page.Slug == "browser-sources" ? 750 : 260);
                    var relative = $"main/{themeName}-{page.Slug}.png";
                    CaptureWindow(manager, Path.Combine(root, relative));
                    captures.Add(new CaptureItem { RelativePath = relative, Caption = $"{page.Caption} · {ThemeCaption(theme)}" });

                    if (page.Slug == "instances"
                        && manager.ContentFrame.Content is InstancesPage instancesPage)
                    {
                        var detailCandidate = instancesPage.RunningList.Items.Count > 0
                            ? instancesPage.RunningList.Items[0]
                            : (instancesPage.HistoryList.Items.Count > 0 ? instancesPage.HistoryList.Items[0] : null);
                        if (detailCandidate != null)
                        {
                            if (instancesPage.RunningList.Items.Contains(detailCandidate))
                                instancesPage.RunningList.SelectedItem = detailCandidate;
                            else
                                instancesPage.HistoryList.SelectedItem = detailCandidate;
                            await SettleAsync(manager, 260);
                            relative = $"main/{themeName}-instances-detail.png";
                            CaptureWindow(manager, Path.Combine(root, relative));
                            captures.Add(new CaptureItem
                            {
                                RelativePath = relative,
                                Caption = $"浏览器实例 · 详情 · {ThemeCaption(theme)}",
                            });
                        }
                    }

                    if (page.Slug == "overview-runtime-center"
                        && manager.ContentFrame.Content is QuickLaunchPage overviewPage
                        && overviewPage.NetworkCombo.Items.Count > 0)
                    {
                        overviewPage.NetworkCombo.IsDropDownOpen = true;
                        await SettleAsync(manager, 260);
                        relative = $"main/{themeName}-overview-network-menu.png";
                        // Popup is a separate native HWND; PrintWindow captures only the owner.
                        // Use the composed screen region here so the expanded menu itself is part
                        // of the interaction review artifact.
                        CaptureScreen(WindowBounds(manager), Path.Combine(root, relative));
                        captures.Add(new CaptureItem
                        {
                            RelativePath = relative,
                            Caption = $"运行中心 · 网络下拉 · {ThemeCaption(theme)}",
                        });
                        overviewPage.NetworkCombo.IsDropDownOpen = false;

                    }

                    if (page.Slug == "browser-sources" && manager.ContentFrame.Content is RuntimePage runtimePage)
                    {
                        await WaitForVersionsAsync(runtimePage);
                        if (runtimePage.VersionCombo.Items.Count > 0)
                        {
                            runtimePage.VersionCombo.IsDropDownOpen = true;
                            await SettleAsync(manager, 320);
                            relative = $"main/{themeName}-browser-sources-version-menu.png";
                            CaptureWindow(manager, Path.Combine(root, relative));
                            captures.Add(new CaptureItem
                            {
                                RelativePath = relative,
                                Caption = $"浏览器来源 · 版本下拉 · {ThemeCaption(theme)}",
                            });
                            runtimePage.VersionCombo.IsDropDownOpen = false;
                        }
                    }
                }

                manager.ThemePopup.IsOpen = true;
                await SettleAsync(manager, 300);
                var popupRelative = $"main/{themeName}-theme-menu.png";
                CaptureWindow(manager, Path.Combine(root, popupRelative));
                captures.Add(new CaptureItem { RelativePath = popupRelative, Caption = $"标题栏主题菜单 · {ThemeCaption(theme)}" });
                manager.ThemePopup.IsOpen = false;
            }

            manager.Close();
            await Task.Delay(180);
        }

        private static async Task WaitForVersionsAsync(RuntimePage page)
        {
            var started = Stopwatch.StartNew();
            while (page.VersionCombo.Items.Count == 0 && started.Elapsed < TimeSpan.FromSeconds(8))
                await SettleAsync(Window.GetWindow(page), 220);
        }

        private static async Task CaptureWizardAsync(InstanceStore store, string root, List<CaptureItem> captures)
        {
            var stepNames = new[]
            {
                new { Slug = "runtime", Caption = "选择浏览器" },
                new { Slug = "launch-parameters", Caption = "启动与调试参数" },
                new { Slug = "plugins", Caption = "选择插件" },
                new { Slug = "review", Caption = "确认配置" },
            };

            foreach (var theme in new[] { AppThemePreference.Light, AppThemePreference.Dark })
            {
                ApplyPreviewTheme(store, theme);
                var wizard = new CustomLaunchWizard(store)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = 300,
                    Top = 130,
                    Topmost = true,
                };
                wizard.Show();
                wizard.Activate();
                await SettleAsync(wizard, 360);
                var themeName = theme == AppThemePreference.Dark ? "dark" : "light";

                for (var index = 0; index < stepNames.Length; index++)
                {
                    var step = stepNames[index];
                    var relative = $"wizard/{themeName}-{index + 1}-{step.Slug}.png";
                    CaptureWindow(wizard, Path.Combine(root, relative));
                    captures.Add(new CaptureItem
                    {
                        RelativePath = relative,
                        Caption = $"自定义启动 {index + 1}/4 · {step.Caption} · {ThemeCaption(theme)}",
                    });
                    if (index < stepNames.Length - 1)
                    {
                        wizard.NextBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await SettleAsync(wizard, 260);
                    }
                }

                wizard.Close();
                await Task.Delay(160);
            }
        }

        private static async Task CaptureFloatingAsync(InstanceStore store, string root, List<CaptureItem> captures)
        {
            store.Settings.EdgeDockOnLeft = false;
            var screen = Forms.Screen.PrimaryScreen.WorkingArea;
            var backdrop = new Window
            {
                Title = "YTray Design Capture Backdrop",
                Width = 650,
                Height = Math.Max(800, screen.Height - 140),
                Left = screen.Right - 650,
                Top = screen.Top + 70,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(42, 43, 47)),
            };
            backdrop.Show();
            await SettleAsync(backdrop, 180);

            var edge = new EdgeDock(store, null) { Topmost = true };
            edge.ShowDock(remember: false);
            await SettleAsync(edge, 320);

            var edgeBounds = Inflate(WindowBounds(edge), 28, 24);
            var edgeRelative = "floating/edge-dock-resting.png";
            CaptureScreen(edgeBounds, Path.Combine(root, edgeRelative));
            captures.Add(new CaptureItem { RelativePath = edgeRelative, Caption = "屏幕右缘吸附条 · 静止" });

            edge.TabStrip.Width = 20;
            edge.DotsPanel.Opacity = 1;
            edge.UpdateLayout();
            await Task.Delay(180);
            edgeRelative = "floating/edge-dock-hover.png";
            CaptureScreen(Inflate(WindowBounds(edge), 28, 24), Path.Combine(root, edgeRelative));
            captures.Add(new CaptureItem { RelativePath = edgeRelative, Caption = "屏幕右缘吸附条 · 悬停展开" });

            var widget = new WidgetView(store) { Topmost = true };
            foreach (var theme in new[] { AppThemePreference.Light, AppThemePreference.Dark })
            {
                ApplyPreviewTheme(store, theme);
                var themeName = theme == AppThemePreference.Dark ? "dark" : "light";

                if (!widget.IsVisible) widget.Show();
                widget.Opacity = 1;
                widget.EntranceTransform.Y = 0;
                widget.RefreshAndMeasure();
                PositionWidgetForReview(widget);
                widget.CancelPendingDismiss();
                await SettleAsync(widget, 320);

                var relative = $"floating/{themeName}-floating-panel.png";
                CaptureScreen(Inflate(WindowBounds(widget), 14, 14), Path.Combine(root, relative));
                captures.Add(new CaptureItem { RelativePath = relative, Caption = $"普通悬浮面板 · {ThemeCaption(theme)}" });

                widget.PositionBeside(edge, onLeft: false);
                widget.CancelPendingDismiss();
                await SettleAsync(widget, 260);
                relative = $"floating/{themeName}-edge-docked-panel.png";
                CaptureScreen(Inflate(Drawing.Rectangle.Union(WindowBounds(widget), WindowBounds(edge)), 14, 14),
                    Path.Combine(root, relative));
                captures.Add(new CaptureItem { RelativePath = relative, Caption = $"屏幕右缘吸附悬浮面板 · {ThemeCaption(theme)}" });
            }

            widget.Close();
            edge.Close();
            backdrop.Close();
            await Task.Delay(180);
        }

        private static void CaptureTrayContextMenu(string root, List<CaptureItem> captures)
        {
            const string relative = "floating/system-tray-context-menu.png";
            using (var anchor = new Forms.Form
            {
                FormBorderStyle = Forms.FormBorderStyle.None,
                ShowInTaskbar = false,
                StartPosition = Forms.FormStartPosition.Manual,
                Location = new Drawing.Point(Forms.Screen.PrimaryScreen.WorkingArea.Right - 420,
                    Forms.Screen.PrimaryScreen.WorkingArea.Bottom - 430),
                Size = new Drawing.Size(2, 2),
                Opacity = 0.01,
            })
            using (var menu = new Forms.ContextMenu())
            using (var timer = new Forms.Timer { Interval = 320 })
            {
                menu.MenuItems.Add("无代理启动", (s, e) => { });
                menu.MenuItems.Add("使用 HTTP 代理启动", (s, e) => { });
                menu.MenuItems.Add("显示小组件", (s, e) => { });
                menu.MenuItems.Add("全部管理", (s, e) => { });
                menu.MenuItems.Add("关闭开机启动…", (s, e) => { });
                menu.MenuItems.Add("-");
                menu.MenuItems.Add("显示边缘小组件", (s, e) => { });
                menu.MenuItems.Add("-");
                menu.MenuItems.Add("退出 YTray", (s, e) => { });

                var captured = false;
                timer.Tick += (s, e) =>
                {
                    var popup = FindWindow("#32768", null);
                    if (popup != IntPtr.Zero)
                    {
                        GetWindowThreadProcessId(popup, out var processId);
                        if (processId == (uint)Process.GetCurrentProcess().Id && GetWindowRect(popup, out var rect))
                        {
                            CaptureScreen(Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom),
                                Path.Combine(root, relative));
                            captured = true;
                        }
                    }
                    timer.Stop();
                    // ContextMenu.Show owns a native modal menu loop. EndMenu is
                    // deterministic on headless runners; simulated Escape can be
                    // delivered to another foreground window and leave CI hung.
                    EndMenu();
                };

                anchor.Show();
                timer.Start();
                menu.Show(anchor, Drawing.Point.Empty);
                timer.Stop();
                anchor.Hide();
                if (!captured)
                    throw new InvalidOperationException("系统托盘菜单截图失败");
            }

            captures.Add(new CaptureItem { RelativePath = relative, Caption = "系统托盘 · 右键菜单" });
        }

        private static void ApplyPreviewTheme(InstanceStore store, AppThemePreference preference)
        {
            store.Settings.ThemePreference = preference;
            ThemeManager.SetPreference(preference);
        }

        private static string ThemeCaption(AppThemePreference preference) =>
            preference == AppThemePreference.Dark ? "深色" : "浅色";

        private static void PositionWidgetForReview(WidgetView widget)
        {
            var screen = Forms.Screen.PrimaryScreen.WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(widget);
            widget.Left = screen.Right / dpi.DpiScaleX - widget.ActualWidth - 120;
            widget.Top = screen.Top / dpi.DpiScaleY + 120;
        }

        private static async Task SettleAsync(Window window, int delayMilliseconds)
        {
            if (window == null) return;
            await window.Dispatcher.InvokeAsync(() =>
            {
                window.UpdateLayout();
                var handle = new WindowInteropHelper(window).Handle;
                if (handle != IntPtr.Zero) YTray.Native.Win32.SetForegroundWindow(handle);
            }, DispatcherPriority.Render);
            await Task.Delay(delayMilliseconds);
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        }

        private static Drawing.Rectangle WindowBounds(Window window)
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect))
                throw new InvalidOperationException("无法获取窗口截图区域：" + window.Title);
            return Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        }

        private static Drawing.Rectangle Inflate(Drawing.Rectangle rect, int horizontal, int vertical)
        {
            rect.Inflate(horizontal, vertical);
            return Drawing.Rectangle.Intersect(rect, Forms.SystemInformation.VirtualScreen);
        }

        private static void CaptureWindow(Window window, string outputPath)
        {
            if (CaptureWpfVisual(window, outputPath)) return;

            var bounds = WindowBounds(window);
            var handle = new WindowInteropHelper(window).Handle;
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            using (var bitmap = new Drawing.Bitmap(bounds.Width, bounds.Height,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (var graphics = Drawing.Graphics.FromImage(bitmap))
            {
                var hdc = graphics.GetHdc();
                bool rendered;
                try
                {
                    // PW_RENDERFULLCONTENT renders the real top-level window even when another
                    // window temporarily covers it or the interactive desktop changes while an
                    // automated design review is running. Fall back to screen copy on systems
                    // where the flag is unavailable.
                    rendered = PrintWindow(handle, hdc, 2);
                }
                finally
                {
                    graphics.ReleaseHdc(hdc);
                }
                // Some custom-chrome WPF windows report PrintWindow success while returning a
                // fully black bitmap. Treat that as a failed capture and use the composed desktop.
                if (!rendered || IsNearlyBlack(bitmap))
                {
                    CaptureScreen(bounds, outputPath);
                    return;
                }
                bitmap.Save(outputPath, ImageFormat.Png);
            }
        }

        private static bool CaptureWpfVisual(Window window, string outputPath)
        {
            try
            {
                window.UpdateLayout();
                var dpi = VisualTreeHelper.GetDpi(window);
                var pixelWidth = Math.Max(1, (int)Math.Round(window.ActualWidth * dpi.DpiScaleX));
                var pixelHeight = Math.Max(1, (int)Math.Round(window.ActualHeight * dpi.DpiScaleY));
                var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight,
                    96d * dpi.DpiScaleX, 96d * dpi.DpiScaleY, PixelFormats.Pbgra32);
                bitmap.Render(window);
                bitmap.Freeze();

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(outputPath)) encoder.Save(stream);
                return true;
            }
            catch (Exception ex)
            {
                CrashGuard.Record("design-capture-wpf-visual", ex);
                return false;
            }
        }

        private static bool IsNearlyBlack(Drawing.Bitmap bitmap)
        {
            long brightness = 0;
            var samples = 0;
            var stepX = Math.Max(1, bitmap.Width / 20);
            var stepY = Math.Max(1, bitmap.Height / 14);
            for (var y = stepY / 2; y < bitmap.Height; y += stepY)
            {
                for (var x = stepX / 2; x < bitmap.Width; x += stepX)
                {
                    var color = bitmap.GetPixel(x, y);
                    brightness += color.R + color.G + color.B;
                    samples++;
                }
            }
            return samples == 0 || brightness / (samples * 3d) < 4d;
        }

        private static void CaptureScreen(Drawing.Rectangle bounds, string outputPath)
        {
            bounds = Drawing.Rectangle.Intersect(bounds, Forms.SystemInformation.VirtualScreen);
            if (bounds.Width <= 0 || bounds.Height <= 0)
                throw new InvalidOperationException("截图区域无效：" + bounds);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            using (var bitmap = new Drawing.Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (var graphics = Drawing.Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, Drawing.CopyPixelOperation.SourceCopy);
                bitmap.Save(outputPath, ImageFormat.Png);
            }
        }

        private static void CreateContactSheet(string root, IReadOnlyList<CaptureItem> captures)
        {
            const int columns = 4;
            const int cellWidth = 390;
            const int cellHeight = 270;
            const int imageWidth = 354;
            const int imageHeight = 212;
            var rows = (int)Math.Ceiling(captures.Count / (double)columns);
            using (var sheet = new Drawing.Bitmap(columns * cellWidth, rows * cellHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (var graphics = Drawing.Graphics.FromImage(sheet))
            using (var labelFont = new Drawing.Font("Microsoft YaHei UI", 11, Drawing.FontStyle.Regular, Drawing.GraphicsUnit.Pixel))
            using (var labelBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(232, 232, 234)))
            using (var cellBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(31, 32, 36)))
            using (var borderPen = new Drawing.Pen(Drawing.Color.FromArgb(60, 62, 68)))
            {
                graphics.Clear(Drawing.Color.FromArgb(20, 21, 24));
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                for (var index = 0; index < captures.Count; index++)
                {
                    var column = index % columns;
                    var row = index / columns;
                    var cell = new Drawing.Rectangle(column * cellWidth + 8, row * cellHeight + 8, cellWidth - 16, cellHeight - 16);
                    graphics.FillRectangle(cellBrush, cell);
                    graphics.DrawRectangle(borderPen, cell);

                    using (var source = Drawing.Image.FromFile(Path.Combine(root, captures[index].RelativePath)))
                    {
                        var scale = Math.Min(imageWidth / (double)source.Width, imageHeight / (double)source.Height);
                        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
                        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
                        var destination = new Drawing.Rectangle(
                            cell.Left + (cell.Width - width) / 2,
                            cell.Top + 12 + (imageHeight - height) / 2,
                            width,
                            height);
                        graphics.DrawImage(source, destination);
                    }

                    var number = (index + 1).ToString("00");
                    graphics.DrawString($"{number}  {captures[index].Caption}", labelFont, labelBrush,
                        new Drawing.RectangleF(cell.Left + 12, cell.Bottom - 34, cell.Width - 24, 24));
                }

                sheet.Save(Path.Combine(root, "00-overview-contact-sheet.png"), ImageFormat.Png);
            }
        }

        private static void WriteManifest(string root, IReadOnlyList<CaptureItem> captures)
        {
            var lines = new List<string>
            {
                "# YTray 设计评审截图",
                "",
                $"生成时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}",
                "",
                "说明：截图使用当前真实浏览器、实例和插件数据；浅色/深色仅用于本次渲染，没有写回用户主题设置。",
                "",
                "## 总览",
                "",
                "- [总览联系表](00-overview-contact-sheet.png)",
                "",
                "## 原始截图",
                "",
            };
            for (var index = 0; index < captures.Count; index++)
                lines.Add($"{index + 1}. [{captures[index].Caption}]({captures[index].RelativePath.Replace('\\', '/')})");
            lines.Add("");
            lines.Add($"共 {captures.Count} 张原始截图，另含 1 张总览联系表。");
            File.WriteAllLines(Path.Combine(root, "README.md"), lines);
        }
    }
}
