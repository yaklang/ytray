#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YTray.Models;
using YTray.Views;

namespace YTray.Core.SystemControl
{
    internal static class ControlReviewCapture
    {
        internal static async Task RunAsync(string output, bool preview)
        {
            Directory.CreateDirectory(output);
            var store = new InstanceStore(Path.Combine(Path.GetFullPath(output), "fixture-state"), false, false);
            ThemeManager.Initialize(AppThemePreference.Dark);
            var manager = new ManagerView(store, new LaunchAtLoginManager(new PreviewLaunchAtLoginBackend()));
            manager.Title = "YTray · 电脑控制预览";
            manager.Show(); manager.NavigateTo("system");
            if (preview) { manager.Closed += (_, __) => { store.Dispose(); Application.Current.Shutdown(); }; return; }
            try {
                await manager.Dispatcher.InvokeAsync(() => manager.UpdateLayout(), DispatcherPriority.ApplicationIdle);
                Save(manager, Path.Combine(output, "system-control-dark.png"));
                ThemeManager.SetPreference(AppThemePreference.Light); manager.UpdateLayout(); Save(manager, Path.Combine(output, "system-control-light.png"));
                manager.NavigateTo("mcp");
                await manager.Dispatcher.InvokeAsync(() => manager.UpdateLayout(), DispatcherPriority.ApplicationIdle);
                Save(manager, Path.Combine(output, "mcp-overview-light.png"));
                ThemeManager.SetPreference(AppThemePreference.Dark); manager.UpdateLayout(); Save(manager, Path.Combine(output, "mcp-overview-dark.png"));
                manager.NavigateTo("system");
                ThemeManager.SetPreference(AppThemePreference.Light);
                var configuration = new SystemMcpWindow(store.SystemControl) { Owner = manager }; configuration.Show();
                await configuration.Dispatcher.InvokeAsync(() => configuration.UpdateLayout(), DispatcherPriority.ApplicationIdle);
                Save(configuration, Path.Combine(output, "system-mcp-light.png"));
                ThemeManager.SetPreference(AppThemePreference.Dark); configuration.UpdateLayout(); Save(configuration, Path.Combine(output, "system-mcp-dark.png"));
                configuration.Width = 680; configuration.Height = 480;
                await configuration.Dispatcher.InvokeAsync(() => configuration.UpdateLayout(), DispatcherPriority.ApplicationIdle);
                Save(configuration, Path.Combine(output, "system-mcp-minimum.png")); configuration.Close();
            } finally { manager.Close(); store.Dispose(); ThemeManager.Shutdown(); Application.Current.Shutdown(); }
        }
        internal static void Save(Window window, string path)
        {
            var dpi = VisualTreeHelper.GetDpi(window);
            var content = window.Content as FrameworkElement;
            var height = content == null ? window.ActualHeight : Math.Min(window.ActualHeight, content.ActualHeight + content.Margin.Top + content.Margin.Bottom);
            var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(height * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            image.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(path); encoder.Save(file);
        }
    }
}
