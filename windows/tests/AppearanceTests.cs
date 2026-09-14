#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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
    [TestClass]
    public class AppearanceTests
    {
        [TestMethod]
        public void ExistingWidgetControlsAndSettingsFollowThemeChanges()
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "ytray-appearance-" + Guid.NewGuid());
                App? application = null;
                WidgetView? widget = null;
                Window? settingsWindow = null;
                InstanceStore? store = null;
                try
                {
                    Application.ResourceAssembly = typeof(App).Assembly;
                    application = new App(initializeServices: false) { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    application.InitializeComponent();
                    ThemeManager.Initialize(AppThemePreference.Light);
                    store = new InstanceStore(directory, false, false);
                    store.Settings.ColorizeBrowserInstances = false;
                    foreach (var badge in new[] { "A", "B", "C", "D" })
                        store.Instances.Add(new BrowserInstance { Name = "Profile " + badge, DockBadge = badge });
                    widget = new WidgetView(store);
                    widget.Show();
                    widget.RefreshAndMeasure();
                    var settings = new SettingsPage(store);
                    settingsWindow = new Window { Content = settings, Width = 1268, Height = 800 };
                    settingsWindow.Show();
                    Assert.IsFalse(settings.ColorizeCheck.IsChecked == true);
                    settings.ColorizeCheck.IsChecked = true;
                    var save = Descendants(settings).OfType<Button>().Single(b => Equals(b.Content, "保存设置"));
                    save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.IsTrue(store.Settings.ColorizeBrowserInstances);

                    // Reuse the same open controls, including materialized templates and a popup.
                    foreach (var theme in new[] { AppThemePreference.Dark, AppThemePreference.Light, AppThemePreference.Dark, AppThemePreference.System })
                    {
                        ThemeManager.SetPreference(theme);
                        widget.CancelPendingDismiss();
                        widget.SchemeCombo.IsDropDownOpen = true;
                        widget.Dispatcher.Invoke(() => widget.UpdateLayout(), DispatcherPriority.ApplicationIdle);
                        var expected = ((SolidColorBrush)widget.FindResource("WidgetTextPrimaryBrush")).Color;
                        foreach (var focus in Descendants(widget).OfType<Button>().Where(b => Equals(b.ToolTip, "聚焦窗口")))
                            Assert.AreEqual(expected, ((SolidColorBrush)focus.Foreground).Color, "Focus icon must follow the live theme");
                        Assert.AreEqual(expected, ((SolidColorBrush)widget.RemarkBox.Foreground).Color);
                        Assert.AreEqual(((SolidColorBrush)widget.FindResource("WidgetBorderBrush")).Color,
                            ((SolidColorBrush)widget.RemarkBox.BorderBrush).Color);
                        foreach (var row in widget.RunningList.Items.Cast<BrowserInstancePresentation>())
                            Assert.AreEqual(BrowserIdentityColor.Brush(row.DockBadge).Color, row.IdentityBrush.Color);
                        widget.SchemeCombo.IsDropDownOpen = false;
                    }
                    Assert.AreEqual(4, Descendants(widget).OfType<Button>().Count(b => Equals(b.ToolTip, "聚焦窗口")));
                }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    settingsWindow?.Close(); widget?.Close(); store?.Dispose();
                    ThemeManager.Shutdown(); application?.Shutdown();
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "WPF theme regression test timed out");
            if (failure != null) throw new AssertFailedException(failure.ToString());
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                yield return child;
                foreach (var descendant in Descendants(child)) yield return descendant;
            }
        }

        [TestMethod]
        public void FrozenFloatingResourcesAreReplacedOnThemeChange()
        {
            var brush = new SolidColorBrush(Colors.White); brush.Freeze();
            var gradient = new LinearGradientBrush(Colors.White, Colors.White, 90); gradient.Freeze();
            var resources = new ResourceDictionary { ["WidgetTextPrimaryBrush"] = brush, ["WidgetSurfaceBrush"] = gradient };
            ThemeManager.ApplyLocalPalette(resources, true);
            Assert.AreNotSame(brush, resources["WidgetTextPrimaryBrush"]);
            Assert.AreNotSame(gradient, resources["WidgetSurfaceBrush"]);
            Assert.AreEqual((Color)ColorConverter.ConvertFromString("#FFE5E6E8"), ((SolidColorBrush)resources["WidgetTextPrimaryBrush"]).Color);
        }
    }
}
