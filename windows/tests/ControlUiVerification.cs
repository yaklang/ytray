#nullable enable
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YTray.Core;
using YTray.Models;
using YTray.Views;
using YTray.Views.Pages;

namespace YTray.Tests
{
    internal static class ControlUiVerification
    {
        internal static void Run(InstanceStore store)
        {
            var page = new SystemControlPage(store);
            var host = new Window { Content = page, Width = 900, Height = 680, Title = "Control UI regression" };
            host.Show();
            var mcp = new SystemMcpWindow(store.SystemControl) { Owner = host, Width = 680, Height = 480 }; mcp.Show();
            try {
                var overview = new McpPage(store);
                host.Content = overview; host.UpdateLayout();
                Assert.IsTrue(overview.CopyConfigButton.IsEnabled);
                Assert.IsFalse(overview.CopyComputerButton.IsEnabled);
                Assert.IsFalse(overview.CopyBothButton.IsEnabled);
                overview.ComputerToggle.IsChecked = true;
                Assert.IsFalse(store.SystemControl.IsEnabled, "Displaying the MCP page must not authorize computer control");
                host.Content = page; host.UpdateLayout();
                foreach (var theme in new[] { AppThemePreference.Dark, AppThemePreference.Light, AppThemePreference.Dark }) {
                    ThemeManager.SetPreference(theme);
                    mcp.Dispatcher.Invoke(() => { host.UpdateLayout(); mcp.UpdateLayout(); }, DispatcherPriority.ApplicationIdle);
                    Assert.AreEqual(VerticalAlignment.Top, mcp.SchemaTextBox.VerticalContentAlignment);
                    Assert.IsTrue(mcp.SchemaTextBox.ActualHeight >= 60, "Minimum MCP window clipped the parameter area");
                    Assert.IsFalse(mcp.CopyConfigButton.IsEnabled, "Stopped sessions must not copy a credential");
                    Assert.IsTrue(page.ToggleControlButton.TranslatePoint(new Point(0, page.ToggleControlButton.ActualHeight), host).Y < host.ActualHeight, "Master switch scrolled outside the visible area");
                    Assert.AreEqual(36, page.ToggleControlButton.ActualHeight, 0.5);
                    Assert.IsNull(page.FindName("DurationBox")); Assert.IsNull(page.FindName("ApplicationsList")); Assert.IsNull(page.FindName("ReadOnlyCheck"));
                    var item = (ListBoxItem)mcp.ToolList.ItemContainerGenerator.ContainerFromIndex(0);
                    Assert.IsNotNull(item);
                    var foreground = ((SolidColorBrush)item.Foreground).Color;
                    var background = ((SolidColorBrush)item.Background).Color;
                    if (background.A == 0) background = ((SolidColorBrush)mcp.ToolList.Background).Color;
                    var ratio = (Math.Max(Luminance(foreground), Luminance(background)) + 0.05) / (Math.Min(Luminance(foreground), Luminance(background)) + 0.05);
                    Assert.IsTrue(ratio >= 4.5, "Rendered tool list contrast is unreadable: " + ratio);
                }
            } finally { mcp.Close(); host.Close(); }
        }
        private static double Luminance(Color c)
        {
            double Linear(byte b) { var value = b / 255.0; return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4); }
            return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
        }
    }
}
