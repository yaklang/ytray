#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YTray.Core;
using YTray.Models;
using YTray.Views;

namespace YTray.Tests
{
    internal static class WidgetLayoutVerification
    {
        internal static void Run(string root)
        {
            using (var store = WidgetReviewCapture.CreateStore(Path.Combine(root, "widget-layout")))
            {
                ThemeManager.SetPreference(AppThemePreference.Dark);
                var widget = new WidgetView(store, new LaunchAtLoginManager(new PreviewLaunchAtLoginBackend()));
                try
                {
                    widget.Show();
                    widget.RefreshAndMeasure();
                    widget.MaxHeight = 1200;
                    widget.UpdateLayout();
                    Assert.AreEqual(390, widget.ActualWidth, 0.01);
                    Assert.AreEqual(746, widget.ActualHeight, 0.01, "2 running + 3 history rows must match the 780 × 1492 macOS reference.");
                    Assert.AreEqual(212, widget.ProxyCard.ActualHeight, 0.01);
                    foreach (var input in new FrameworkElement[] { widget.SchemeCombo, widget.HostBox, widget.PortBox, widget.RemarkBox, widget.UsernameBox, widget.PasswordInput, widget.TargetBox })
                        Assert.AreEqual(16, input.ActualHeight, 0.01, input.Name);
                    Assert.AreEqual(28, widget.SaveBtn.ActualHeight, 0.01);
                    Assert.AreEqual(28, widget.DirectBtn.ActualHeight, 0.01);
                    Assert.AreEqual(32, widget.StartupBtn.ActualHeight, 0.01);
                    Assert.AreEqual(Color.FromRgb(57, 57, 57), ((SolidColorBrush)widget.RootBorder.Background).Color);
                    Assert.AreEqual(Color.FromRgb(30, 30, 30), ((SolidColorBrush)widget.ProxyCard.Background).Color);

                    widget.SchemeCombo.ApplyTemplate();
                    var toggle = Descendants(widget.SchemeCombo).OfType<ToggleButton>().Single();
                    Assert.AreEqual(widget.SchemeCombo.ActualWidth, toggle.ActualWidth, 0.01, "The native toggle must fill the macOS-sized dropdown.");
                    foreach (var button in new[] { widget.CheckBtn, widget.SaveBtn, widget.DirectBtn, widget.ProxyBtn })
                    {
                        var content = (StackPanel)button.Content;
                        var desiredWidth = content.Children.OfType<FrameworkElement>().Sum(child => child.DesiredSize.Width);
                        Assert.IsTrue(desiredWidth <= button.ActualWidth - button.Padding.Left - button.Padding.Right,
                            button.Name + " must not clip its label.");
                    }

                    widget.HostBox.Text = "192.0.2.10";
                    widget.PortBox.Text = "8181";
                    widget.UsernameBox.Text = "review-user";
                    widget.PasswordInput.Password = "fixture-password";
                    widget.TargetBox.Text = "example.com";
                    widget.RefreshAndMeasure();
                    Assert.AreEqual("192.0.2.10", widget.HostBox.Text, "A background instance refresh must not discard a draft.");
                    Assert.AreEqual("fixture-password", widget.PasswordInput.Password);
                    widget.SaveBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.AreEqual("http://192.0.2.10:8181", store.Settings.PresetProxyServer);
                    Assert.AreEqual("review-user", store.Settings.RecentProxyPresets[0].Username);
                    Assert.AreEqual("fixture-password", store.Settings.RecentProxyPresets[0].Password);
                    Assert.AreEqual("example.com", store.Settings.PresetProxyCheckTarget);
                    widget.ProxyHistoryBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var menu = widget.ProxyHistoryBtn.ContextMenu;
                    Assert.IsNotNull(menu);
                    Assert.AreEqual(1, menu!.Items.Count);
                    widget.HostBox.Text = "unsaved.invalid";
                    ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Assert.AreEqual("192.0.2.10", widget.HostBox.Text);
                    menu.IsOpen = false;

                    widget.AdvancedBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    widget.UpdateLayout();
                    Assert.AreEqual(136, widget.ProxyCard.ActualHeight, 0.01);
                    Assert.AreEqual(Visibility.Collapsed, widget.AdvancedPanel.Visibility);
                    widget.AdvancedBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                    // The body scrolls on short/high-DPI screens; launch and manager remain reachable.
                    widget.MaxHeight = 520;
                    widget.UpdateLayout();
                    Assert.IsTrue(widget.ActualHeight <= 520);
                    Assert.IsTrue(widget.InstanceScroll.ScrollableHeight > 0);
                    Assert.IsTrue(widget.InstanceScroll.ViewportHeight > 0);
                    Assert.IsTrue(widget.StartupBtn.TranslatePoint(new Point(0, widget.StartupBtn.ActualHeight), widget).Y <= widget.ActualHeight);
                    widget.MaxHeight = 1200;
                    store.Instances.Clear();
                    widget.RefreshAndMeasure();
                    widget.UpdateLayout();
                    Assert.AreEqual(Visibility.Visible, widget.RunningEmpty.Visibility);
                    Assert.AreEqual(Visibility.Visible, widget.HistoryEmpty.Visibility);
                    Assert.AreEqual(Visibility.Collapsed, widget.RunningList.Visibility);
                    Assert.IsFalse(widget.ClearHistoryBtn.IsEnabled);
                }
                finally { widget.Close(); }
            }
        }

        private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                yield return child;
                foreach (var descendant in Descendants(child)) yield return descendant;
            }
        }
    }
}
