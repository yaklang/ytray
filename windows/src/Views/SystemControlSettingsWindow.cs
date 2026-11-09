#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using YTray.Core;
using YTray.Views.Pages;

namespace YTray.Views
{
    internal sealed class SystemControlSettingsWindow : Window
    {
        internal SystemControlSettingsWindow(InstanceStore store, bool mcp = false)
        {
            Title = mcp ? "YTray · MCP" : "YTray · 电脑控制设置";
            Width = mcp ? 820 : 660; Height = mcp ? 720 : 660; MinWidth = 590; MinHeight = 540;
            Content = mcp ? (object)new McpPage(store) : new SystemControlPage(store);
            SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
            SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            SourceInitialized += (_, __) => ApplyTitleBar();
            ThemeManager.ThemeChanged += ThemeChanged;
            Closed += (_, __) => ThemeManager.ThemeChanged -= ThemeChanged;
        }
        internal void ShowFor(Window anchor)
        {
            if (!IsVisible) {
                var work = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(anchor).Handle).WorkingArea;
                var scale = PresentationSource.FromVisual(anchor)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
                var origin = scale.Transform(new Point(work.Left, work.Top)); var limit = scale.Transform(new Point(work.Right, work.Bottom));
                MinWidth = Math.Min(MinWidth, limit.X - origin.X - 24); MinHeight = Math.Min(MinHeight, limit.Y - origin.Y - 24);
                Width = Math.Min(Width, limit.X - origin.X - 24); Height = Math.Min(Height, limit.Y - origin.Y - 24);
                Left = origin.X + (limit.X - origin.X - Width) / 2; Top = origin.Y + (limit.Y - origin.Y - Height) / 2;
                Show();
            }
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }
        private void ThemeChanged(object? sender, EventArgs e) => ApplyTitleBar();
        private void ApplyTitleBar()
        {
            var handle = new WindowInteropHelper(this).Handle; if (handle == IntPtr.Zero) return;
            var dark = ThemeManager.IsDark ? 1 : 0; _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        }
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    }
}
