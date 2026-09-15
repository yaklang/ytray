#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using YTray.Core;
using YTray.Models;
using YTray.Views;

namespace YTray.Tests
{
    internal static class ExtensionFallbackVerification
    {
        internal static void Run(string root)
        {
            var output = Environment.GetEnvironmentVariable("YTRAY_TEST_CAPTURE");
            var runtime = new BrowserRuntime { Name = "Google Chrome", BrowserKind = BrowserKind.Chrome,
                ExecutablePath = Environment.GetEnvironmentVariable("YTRAY_TEST_CHROME") ?? "unused.exe", Version = "152.0.7977.83" };
            foreach (var theme in new[] { AppThemePreference.Light, AppThemePreference.Dark })
            {
                ThemeManager.SetPreference(theme);
                Assert.IsFalse(Ask(new ExtensionLaunchPrompt(runtime, new LaunchSettings(), 2, "插件目录中的 manifest.json 无效。"), false, output));
                Assert.IsTrue(Ask(new ExtensionLaunchPrompt(runtime, new LaunchSettings(), 2, "插件目录中的 manifest.json 无效。"), true, output));
                Assert.IsFalse(Ask(new ExtensionLaunchPrompt(runtime, new LaunchSettings { ProxyUsername = "test-user" }, 2, "插件接口不可用。"), false, output));
                Assert.IsFalse(Ask(new ExtensionLaunchPrompt(runtime, new LaunchSettings(), 2,
                    string.Join("\n", Enumerable.Repeat("测试长错误：插件目录不可访问，请检查插件安装。", 100))), false, output));
            }
            using var store = new InstanceStore(Path.Combine(root, "extensions"), false, false);
            store.Runtimes.Add(runtime);
            store.Settings.DefaultRuntimeID = runtime.Id;
            store.Settings.DebugPort = 20777;
            var extension = Path.Combine(root, "Probe 插件");
            Directory.CreateDirectory(extension);
            File.WriteAllText(Path.Combine(extension, "manifest.json"), new JObject
            {
                ["manifest_version"] = 3, ["name"] = "YTray integration probe", ["version"] = "1.0",
                ["permissions"] = new JArray("storage"),
                ["background"] = new JObject { ["service_worker"] = "worker.js" },
                ["content_scripts"] = new JArray(new JObject { ["matches"] = new JArray("http://*/*"),
                    ["js"] = new JArray("content.js"), ["run_at"] = "document_end" })
            }.ToString());
            File.WriteAllText(Path.Combine(extension, "worker.js"), "chrome.runtime.onInstalled.addListener(() => chrome.storage.local.set({verified:true}));");
            File.WriteAllText(Path.Combine(extension, "content.js"), "document.documentElement.setAttribute('data-ytray-probe','active');");
            store.AddPlugin(extension);
            store.Settings.HomeURL = "https://example.com";
            // Exercise the actual wizard: ordinary Chrome no longer disables plugin checkboxes.
            var wizard = new CustomLaunchWizard(store);
            wizard.Show();
            wizard.NextBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            wizard.NextBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var checks = Descendants(wizard.ContentArea).OfType<CheckBox>().ToArray();
            Assert.AreEqual(1, checks.Length);
            Assert.IsTrue(checks[0].IsEnabled && checks[0].IsChecked == true);
            wizard.Close();
            if (Environment.GetEnvironmentVariable("YTRAY_TEST_CHROME") == null) return;
            Assert.IsTrue(File.Exists(runtime.ExecutablePath));
            if (output != null) DiagnosticLog.Initialize(output);
            Assert.IsTrue(ExtensionInstaller.TryInstallBundled(store.ApplicationDirectory, out var bundled, out var version));
            store.AddPlugin(bundled);
            Assert.AreEqual(2, store.Plugins.Count);
            using var server = new ExtensionTestServer();
            store.Settings.HomeURL = server.DirectURL;
            store.Settings.PresetProxyHost = "127.0.0.1";
            store.Settings.PresetProxyPort = server.Port;
            var widget = new WidgetView(store);
            var prompts = 0;
            var accept = false;
            store.ConfirmLaunchWithoutPlugins = prompt =>
            {
                prompts++;
                Assert.IsTrue(store.IsLaunching && store.IsConfirmingLaunch);
                Assert.IsFalse(store.LaunchConfigured(false), "A confirmation must block a second launch");
                widget.Show(); widget.Activate(); widget.RefreshAndMeasure();
                var result = Ask(prompt, accept, output);
                Assert.IsTrue(widget.IsVisible, "The confirmation must not dismiss its widget owner");
                return result;
            };
            try
            {
                foreach (var mode in new[] { "direct", "proxy-auth", "custom", "history" })
                {
                    server.RequireAuthentication = mode == "proxy-auth";
                    store.Settings.HomeURL = server.RequireAuthentication ? server.ProxiedURL : server.DirectURL;
                    store.Settings.PresetProxyUsername = server.RequireAuthentication ? ExtensionTestServer.Username : "";
                    store.Settings.PresetProxyPassword = server.RequireAuthentication ? ExtensionTestServer.Password : "";
                    widget.Show(); widget.Activate(); widget.RefreshAndMeasure();
                    if (mode == "direct") widget.DirectBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else if (mode == "proxy-auth") widget.ProxyBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else if (mode == "custom")
                    {
                        var launchWizard = new CustomLaunchWizard(store);
                        launchWizard.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            for (var step = 0; step < 4; step++) launchWizard.NextBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        }), DispatcherPriority.ApplicationIdle);
                        Assert.IsTrue(launchWizard.ShowDialog() == true);
                    }
                    else store.RestoreHistory(store.HistoryInstances.First());
                    PumpUntil(() => !store.IsLaunching, TimeSpan.FromSeconds(45));
                    Assert.IsNull(store.ErrorMessage, store.ErrorMessage);
                    Assert.AreEqual(0, prompts, "A working Chrome must not ask to skip plugins");
                    var instance = store.RunningInstances.Single();
                    Assert.AreEqual(2, instance.PluginIDs.Count);
                    VerifyBrowser(instance, store, output, mode);
                    if (mode == "proxy-auth")
                    {
                        Assert.IsTrue(server.Challenges > 0 && server.AuthenticatedRequests > 0, "The real proxy must issue 407 and receive correct credentials");
                        Assert.AreEqual("http://127.0.0.1:" + server.Port, instance.SettingsSnapshot!.ProxyServer);
                    }
                    Assert.IsTrue(Await(store.StopAsync(instance)));
                    Record(output, mode + ": extension list, content script, built-in bootstrap, CDP page and screenshot verified");
                }

                // A malformed third plugin forces failure after two valid plugins already loaded.
                var badPath = Path.Combine(root, "invalid-plugin");
                Directory.CreateDirectory(badPath);
                File.WriteAllText(Path.Combine(badPath, "manifest.json"), "invalid json");
                var bad = new BrowserPlugin { Name = "Invalid test plugin", Path = badPath, Enabled = true };
                store.Plugins.Add(bad); store.Settings.DefaultPluginIDs.Add(bad.Id);
                var historyCount = store.HistoryInstances.Count;
                widget.Show(); widget.Activate(); widget.RefreshAndMeasure();
                widget.DirectBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => !store.IsLaunching, TimeSpan.FromSeconds(45));
                Assert.IsTrue(store.LaunchWasCancelled);
                Assert.IsNull(store.ErrorMessage);
                Assert.AreEqual(0, store.RunningInstances.Count);
                Assert.AreEqual(historyCount, store.HistoryInstances.Count);
                Assert.AreEqual(1, prompts);
                Record(output, "failed partial load: cancellation closed the incomplete browser without adding history");

                accept = true;
                store.Settings.DockBadge = "B";
                Assert.IsTrue(store.LaunchConfigured(false));
                PumpUntil(() => !store.IsLaunching, TimeSpan.FromSeconds(45));
                Assert.IsNull(store.ErrorMessage, store.ErrorMessage);
                var plain = store.RunningInstances.Single();
                Assert.AreEqual(0, plain.PluginIDs.Count);
                Assert.AreEqual(0, plain.SettingsSnapshot!.DefaultPluginIDs.Count);
                StringAssert.Contains(plain.SettingsSnapshot.AdditionalFlags, "--disable-extensions");
                var extensions = Await(Inspect(plain.DebugPort));
                Assert.IsFalse(extensions.OfType<JObject>().Any(item => item["enabled"]?.Value<bool>() == true));
                Assert.IsTrue(Await(store.StopAsync(plain)));
                Assert.AreEqual(2, prompts);
                Assert.AreEqual(3, store.Settings.DefaultPluginIDs.Count);
                Record(output, "failed partial load: confirmed retry succeeded with no plugins and unchanged defaults");

                // Restore the previous real plugin profile, fail partway, then retry the same profile.
                var old = store.HistoryInstances.First(i => i.PluginIDs.Count == 2);
                old.PluginIDs.Add(bad.Id);
                var profile = old.ProfilePath;
                store.RestoreHistory(old);
                PumpUntil(() => !store.IsLaunching, TimeSpan.FromSeconds(45));
                Assert.IsNull(store.ErrorMessage, store.ErrorMessage);
                var restored = store.RunningInstances.Single();
                Assert.AreEqual(profile, restored.ProfilePath);
                Assert.AreEqual(0, restored.PluginIDs.Count);
                Assert.IsFalse(Await(Inspect(restored.DebugPort)).OfType<JObject>().Any(item => item["enabled"]?.Value<bool>() == true));
                Assert.IsTrue(Await(store.StopAsync(restored)));
                Record(output, "restored profile fallback: same profile preserved, old plugins disabled for this launch");

                // Failure with proxy credentials offers no skip; no target-page request can escape.
                accept = false;
                server.RequireAuthentication = true;
                store.Settings.HomeURL = server.ProxiedURL;
                store.Settings.PresetProxyUsername = ExtensionTestServer.Username;
                store.Settings.PresetProxyPassword = ExtensionTestServer.Password;
                var beforeRequests = server.AuthenticatedRequests;
                Assert.IsTrue(store.LaunchConfigured(true));
                PumpUntil(() => !store.IsLaunching, TimeSpan.FromSeconds(45));
                Assert.IsTrue(store.LaunchWasCancelled);
                Assert.AreEqual(0, store.RunningInstances.Count);
                Assert.AreEqual(beforeRequests, server.AuthenticatedRequests);
                store.SaveSettings();
                using var reload = new InstanceStore(store.ApplicationDirectory, false, false);
                Assert.AreEqual(3, reload.Settings.DefaultPluginIDs.Count);
                Assert.IsTrue(reload.Plugins.All(p => p.Enabled));
                Record(output, "proxy-auth failure: target navigation blocked, no unauthenticated fallback; defaults persisted");
            }
            finally
            {
                if (output != null)
                    foreach (var log in Directory.GetFiles(Path.Combine(store.ApplicationDirectory, "Logs"), "*.log"))
                        File.Copy(log, Path.Combine(output, "browser-" + Path.GetFileName(log)), true);
                foreach (var instance in store.RunningInstances.ToArray()) Await(store.StopAsync(instance));
                widget.Close();
            }
        }

        private static void VerifyBrowser(BrowserInstance instance, InstanceStore store, string? output, string mode)
        {
            var installed = Await(Inspect(instance.DebugPort));
            foreach (var plugin in store.Plugins)
                Assert.IsTrue(installed.OfType<JObject>().Any(e => e["id"]?.Value<string>() == ExtensionInstaller.ChromiumExtensionId(plugin)
                    && e["enabled"]?.Value<bool>() == true), plugin.Name + " must be enabled");
            string? title = null;
            PumpUntil(() => (title = Await(ScreenshotService.CurrentPageTitleAsync(instance.DebugPort))) == ExtensionTestServer.Title, TimeSpan.FromSeconds(15));
            var page = Await(PageEvaluate(instance.DebugPort, "document.documentElement.getAttribute('data-ytray-probe')"));
            Assert.AreEqual("active", page["result"]?["value"]?.Value<string>());
            var screenshot = Await(ScreenshotService.CaptureAsync(instance.DebugPort, instance.Id, output ?? store.ApplicationDirectory));
            Assert.IsTrue(File.Exists(screenshot));
            // The managed bootstrap must store the instance identity before redirecting the tab.
            var managed = store.Plugins.Single(p => p.Name == ExtensionInstaller.ExtensionName);
            var identity = Await(PageEvaluate(instance.DebugPort, "chrome.storage.local.get(null)", ExtensionInstaller.ChromiumExtensionId(managed)));
            StringAssert.Contains(identity["result"]!["value"]!.ToString(), instance.Id.ToString());
        }

        internal static async Task<JArray> Inspect(int port)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var client = new BrowserDevTools();
            await client.ConnectAsync(port, cts.Token);
            return (JArray)(await client.SendAsync("Extensions.getExtensions", new JObject(), cts.Token))["extensions"]!;
        }

        private static async Task<JObject> PageEvaluate(int port, string expression, string? extensionId = null)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            var pages = JArray.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list"));
            var page = pages.OfType<JObject>().First(p => extensionId == null
                ? p["type"]?.Value<string>() == "page" && p["title"]?.Value<string>() == ExtensionTestServer.Title
                : p["type"]?.Value<string>() == "service_worker" && p["url"]!.Value<string>()!.StartsWith("chrome-extension://" + extensionId + "/", StringComparison.Ordinal));
            var reply = await ScreenshotService.WebSocketExchangeAsync(new Uri(page["webSocketDebuggerUrl"]!.Value<string>()!),
                new JObject { ["id"] = 1, ["method"] = "Runtime.evaluate", ["params"] = new JObject { ["expression"] = expression, ["returnByValue"] = true, ["awaitPromise"] = true } });
            return (JObject)reply.Single(p => p["id"]?.Value<int>() == 1)["result"]!;
        }

        private static bool Ask(ExtensionLaunchPrompt prompt, bool accept, string? output)
        {
            Exception? error = null;
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                var dialog = Application.Current.Windows.OfType<ExtensionLaunchDialog>().Single();
                try
                {
                    Assert.AreEqual(prompt.CanSkipPlugins, dialog.ContinueButton.Visibility == Visibility.Visible);
                    Assert.AreEqual(((SolidColorBrush)dialog.FindResource("TextPrimaryBrush")).Color, ((SolidColorBrush)dialog.MessageText.Foreground).Color);
                    Assert.IsTrue(dialog.MessageText.ActualHeight > 100);
                    if (prompt.Message.Length > 2000)
                    {
                        Assert.IsTrue(dialog.MessageScroll.ScrollableHeight > 0);
                        Assert.IsTrue(dialog.ActualHeight <= SystemParameters.WorkArea.Height);
                        Assert.IsTrue(dialog.ContinueButton.IsVisible);
                    }
                    Capture(dialog, output, (ThemeManager.IsDark ? "dark" : "light")
                        + (prompt.Message.Length > 2000 ? "-long-error" : prompt.CanSkipPlugins ? "-confirmation" : "-proxy-auth"));
                }
                catch (Exception ex) { error = ex; }
                finally { (accept && prompt.CanSkipPlugins ? dialog.ContinueButton : dialog.CancelButton).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
            }), DispatcherPriority.ApplicationIdle);
            var result = ExtensionLaunchDialog.Confirm(prompt);
            if (error != null) throw error;
            return result;
        }

        private static T Await<T>(Task<T> task) { PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(20)); return task.GetAwaiter().GetResult(); }
        private static void PumpUntil(Func<bool> ready, TimeSpan timeout)
        {
            var timer = Stopwatch.StartNew();
            while (!ready())
            {
                Assert.IsTrue(timer.Elapsed < timeout, "Browser operation timed out");
                var frame = new DispatcherFrame();
                // Modal dialogs preserve the initiating dispatch priority for async continuations.
                // Pump even ApplicationIdle work, rather than starving a wizard's startup task.
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.SystemIdle, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame); Thread.Sleep(10);
            }
        }
        private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i); yield return child;
                foreach (var next in Descendants(child)) yield return next;
            }
        }
        private static void Record(string? output, string text)
        {
            if (output != null) { Directory.CreateDirectory(output); File.AppendAllText(Path.Combine(output, "extension-api-smoke.txt"), text + "\n"); }
        }
        private static void Capture(Window window, string? output, string name)
        {
            if (output == null) return;
            Directory.CreateDirectory(output); window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, name + ".png")); encoder.Save(stream);
        }
    }
}
