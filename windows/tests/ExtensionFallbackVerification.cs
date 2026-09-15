#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YTray.Core;
using YTray.Models;
using YTray.Views;

namespace YTray.Tests
{
    // Runs in AppearanceTests' single WPF Application. Set YTRAY_TEST_CHROME to opt into
    // actual browser launches; all dialogs/cancellation paths are also exercised in CI.
    internal static class ExtensionFallbackVerification
    {
        internal static void Run(string root)
        {
            using var store = new InstanceStore(Path.Combine(root, "fallback"), false, false);
            var executable = Environment.GetEnvironmentVariable("YTRAY_TEST_CHROME");
            var output = Environment.GetEnvironmentVariable("YTRAY_TEST_CAPTURE");
            if (!string.IsNullOrEmpty(output)) DiagnosticLog.Initialize(output!);
            var runtime = new BrowserRuntime
            {
                Name = "Google Chrome", BrowserKind = BrowserKind.Chrome,
                ExecutablePath = executable ?? "unused.exe", Source = RuntimeSource.System,
            };
            var plugin = new BrowserPlugin { Name = "yakit-browser-agent", Enabled = true, Path = Path.Combine(root, "extension") };
            store.Runtimes.Add(runtime); store.Plugins.Add(plugin);
            store.Settings.DefaultRuntimeID = runtime.Id;
            store.Settings.DefaultPluginIDs.Add(plugin.Id);
            store.Settings.HomeURL = "data:text/html," + Uri.EscapeDataString("<title>YTray fallback verified</title><h1>Chrome without plugins</h1>");
            store.Settings.DebugPort = 20777;
            store.Settings.PresetProxyHost = "127.0.0.1";
            store.Settings.PresetProxyPort = 8083;
            var widget = new WidgetView(store);
            var accept = false;
            var prompts = 0;
            store.ConfirmLaunchWithoutPlugins = prompt =>
            {
                prompts++;
                Exception? failure = null;
                widget.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var dialog = Application.Current.Windows.OfType<ExtensionLaunchDialog>().Single();
                    try
                    {
                        Assert.IsTrue(widget.IsVisible, "Opening a dialog must not auto-hide its widget owner");
                        Assert.AreEqual(widget, dialog.Owner);
                        Assert.AreEqual(prompt.CanSkipPlugins, dialog.ContinueButton.Visibility == Visibility.Visible);
                        Assert.AreEqual(((SolidColorBrush)dialog.FindResource("TextPrimaryBrush")).Color,
                            ((SolidColorBrush)dialog.MessageText.Foreground).Color);
                        Assert.IsTrue(dialog.MessageText.ActualHeight > 100, "Full explanation must wrap, not truncate");
                        Capture(dialog, output, (ThemeManager.IsDark ? "dark" : "light") + (prompt.CanSkipPlugins ? "-confirmation" : "-proxy-auth"));
                    }
                    catch (Exception ex) { failure = ex; }
                    finally
                    {
                        var button = accept && prompt.CanSkipPlugins ? dialog.ContinueButton : dialog.CancelButton;
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                }), DispatcherPriority.ApplicationIdle);
                var result = ExtensionLaunchDialog.Confirm(prompt);
                if (failure != null) throw failure;
                Assert.AreEqual(accept && prompt.CanSkipPlugins, result);
                return result;
            };
            try
            {
                foreach (var theme in new[] { AppThemePreference.Light, AppThemePreference.Dark })
                {
                    ThemeManager.SetPreference(theme);
                    widget.Show(); widget.Activate(); widget.RefreshAndMeasure();
                    widget.DirectBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.IsTrue(store.LaunchWasCancelled);
                    Assert.IsNull(store.ErrorMessage);
                    Assert.AreEqual(0, store.Instances.Count);
                    Assert.IsTrue(widget.IsVisible);
                    Assert.IsTrue(widget.IsEnabled);
                    Assert.IsFalse(widget.InstanceActionStatus.Text.Contains("失败"));

                    store.Settings.PresetProxyUsername = "test-user";
                    widget.ProxyBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.IsTrue(store.LaunchWasCancelled);
                    Assert.IsNull(store.ErrorMessage);
                    store.Settings.PresetProxyUsername = "";
                }
                Assert.AreEqual(4, prompts);
                if (string.IsNullOrEmpty(executable)) return;
                Assert.IsTrue(File.Exists(executable), "YTRAY_TEST_CHROME must point to a real Chrome executable");
                accept = true;
                foreach (var mode in new[] { "direct", "proxy", "custom", "history" })
                {
                    widget.Show(); widget.Activate(); widget.RefreshAndMeasure();
                    var before = prompts;
                    if (mode == "direct") widget.DirectBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else if (mode == "proxy") widget.ProxyBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else if (mode == "custom") Assert.IsTrue(store.Launch(LaunchMode.Custom, store.Settings.Clone()));
                    else
                    {
                        var history = store.HistoryInstances.First();
                        history.PluginIDs.Add(plugin.Id);
                        history.SettingsSnapshot!.DefaultPluginIDs.Add(plugin.Id);
                        store.RestoreHistory(history);
                    }
                    Assert.AreEqual(before + 1, prompts, mode + ": must confirm before launching");
                    Assert.IsNull(store.ErrorMessage, mode + ": " + store.ErrorMessage);
                    PumpUntil(() => !store.IsLaunching, TimeSpan.FromSeconds(40));
                    Assert.IsNull(store.ErrorMessage, mode + ": " + store.ErrorMessage);
                    var instance = store.RunningInstances.Single();
                    Assert.AreEqual(0, instance.PluginIDs.Count);
                    Assert.AreEqual(0, instance.SettingsSnapshot!.DefaultPluginIDs.Count);
                    Assert.AreEqual(mode == "proxy" ? "http://127.0.0.1:8083" : "", instance.SettingsSnapshot.ProxyServer);
                    Assert.AreEqual(1, store.Settings.DefaultPluginIDs.Count);
                    Assert.IsTrue(plugin.Enabled);
                    var titleTask = ScreenshotService.CurrentPageTitleAsync(instance.DebugPort, 10);
                    PumpUntil(() => titleTask.IsCompleted, TimeSpan.FromSeconds(15));
                    Assert.AreEqual("YTray fallback verified", titleTask.GetAwaiter().GetResult());
                    var screenshot = ScreenshotService.CaptureAsync(instance.DebugPort, instance.Id, output ?? root);
                    PumpUntil(() => screenshot.IsCompleted, TimeSpan.FromSeconds(15));
                    Assert.IsTrue(File.Exists(screenshot.GetAwaiter().GetResult()));
                    var stop = store.StopAsync(instance);
                    PumpUntil(() => stop.IsCompleted, TimeSpan.FromSeconds(15));
                    Assert.IsTrue(stop.GetAwaiter().GetResult());
                    Assert.AreEqual(0, store.RunningInstances.Count);
                    if (!string.IsNullOrEmpty(output)) File.AppendAllText(Path.Combine(output, "chrome-fallback.txt"), mode + ": confirmed, CDP ready, page/screenshot verified, plugins omitted, defaults retained\n");
                }
                store.SaveSettings();
                using var reload = new InstanceStore(store.ApplicationDirectory, false, false);
                Assert.IsTrue(reload.Plugins.Single().Enabled);
                CollectionAssert.AreEqual(new[] { plugin.Id }, reload.Settings.DefaultPluginIDs);
            }
            finally
            {
                foreach (var instance in store.RunningInstances.ToArray())
                {
                    var stop = store.StopAsync(instance);
                    PumpUntil(() => stop.IsCompleted, TimeSpan.FromSeconds(15));
                }
                widget.Close();
            }
        }

        private static void PumpUntil(Func<bool> ready, TimeSpan timeout)
        {
            var timer = Stopwatch.StartNew();
            while (!ready())
            {
                Assert.IsTrue(timer.Elapsed < timeout, "Browser operation timed out");
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                Thread.Sleep(10);
            }
        }

        private static void Capture(Window window, string? output, string name)
        {
            if (string.IsNullOrEmpty(output)) return;
            Directory.CreateDirectory(output);
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, name + ".png"));
            encoder.Save(stream);
        }
    }
}
