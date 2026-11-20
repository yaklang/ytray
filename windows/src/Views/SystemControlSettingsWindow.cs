#nullable enable
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using YTray.Core;
using YTray.Native;
using YTray.Views.Pages;

namespace YTray.Views
{
    internal sealed class SystemControlSettingsWindow : Window
    {
        internal SystemControlSettingsWindow(InstanceStore store, bool mcp = false)
        {
            Title = mcp ? "YTray · MCP 网关服务" : "YTray · 电脑控制工作台";
            Width = mcp ? 920 : 860;
            Height = mcp ? 740 : 700;
            MinWidth = 620;
            MinHeight = 540;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            Content = mcp ? (object)new McpPage(store) : new SystemControlPage(store);
            SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
            SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            WindowChromeHelper.Attach(this);
        }

        internal void ShowFor(Window anchor)
        {
            if (!IsVisible)
            {
                var work = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(anchor).Handle).WorkingArea;
                var scale = PresentationSource.FromVisual(anchor)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
                var origin = scale.Transform(new Point(work.Left, work.Top));
                var limit = scale.Transform(new Point(work.Right, work.Bottom));
                MinWidth = System.Math.Min(MinWidth, limit.X - origin.X - 24);
                MinHeight = System.Math.Min(MinHeight, limit.Y - origin.Y - 24);
                Width = System.Math.Min(Width, limit.X - origin.X - 24);
                Height = System.Math.Min(Height, limit.Y - origin.Y - 24);
                Left = origin.X + (limit.X - origin.X - Width) / 2;
                Top = origin.Y + (limit.Y - origin.Y - Height) / 2;
                Show();
            }
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }
    }
}
