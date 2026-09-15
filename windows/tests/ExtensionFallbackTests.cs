#nullable enable
using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YTray.Core;
using YTray.Models;

namespace YTray.Tests
{
    [TestClass]
    public class ExtensionFallbackTests
    {
        [DataTestMethod]
        [DataRow(BrowserKind.Chrome, LaunchMode.Quick)]
        [DataRow(BrowserKind.ChromeBeta, LaunchMode.Custom)]
        [DataRow(BrowserKind.ChromeCanary, LaunchMode.Quick)]
        public void CancelDoesNotCreateProfileOrChangeDefaults(BrowserKind kind, LaunchMode mode)
        {
            var path = Path.Combine(Path.GetTempPath(), "ytray-fallback-" + Guid.NewGuid());
            try
            {
                using var store = new InstanceStore(path, false, false);
                var runtime = new BrowserRuntime { BrowserKind = kind, Name = "Chrome", ExecutablePath = "unused.exe" };
                var plugin = new BrowserPlugin { Name = "Built-in", Enabled = true };
                store.Runtimes.Add(runtime); store.Plugins.Add(plugin);
                store.Settings.DefaultRuntimeID = runtime.Id;
                store.Settings.DefaultPluginIDs.Add(plugin.Id);
                var asked = 0;
                store.ConfirmLaunchWithoutPlugins = prompt =>
                {
                    asked++;
                    Assert.IsTrue(prompt.CanSkipPlugins);
                    Assert.IsTrue(store.IsLaunching);
                    Assert.IsTrue(store.IsConfirmingLaunch);
                    Assert.IsFalse(store.Launch(mode), "Reentrant launches must not open another prompt");
                    return false;
                };
                Assert.IsFalse(store.Launch(mode));
                Assert.AreEqual(1, asked);
                Assert.IsTrue(store.LaunchWasCancelled);
                Assert.IsFalse(store.IsLaunching);
                Assert.IsFalse(store.IsConfirmingLaunch);
                Assert.IsNull(store.ErrorMessage);
                Assert.IsNull(store.LaunchToken);
                Assert.AreEqual(0, store.Instances.Count);
                Assert.IsFalse(Directory.Exists(Path.Combine(path, "Profiles")));
                CollectionAssert.AreEqual(new[] { plugin.Id }, store.Settings.DefaultPluginIDs);
                Assert.IsTrue(plugin.Enabled);

                store.Settings.PresetProxyHost = "";
                Assert.IsFalse(store.LaunchConfigured(true));
                Assert.IsFalse(store.LaunchWasCancelled, "A later configuration error must not be hidden by cancellation");
                Assert.IsNotNull(store.ErrorMessage);
            }
            finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
        }

        [DataTestMethod]
        [DataRow("username", "")]
        [DataRow("", "password")]
        [DataRow("username", "password")]
        public void CannotSkipProxyAuthentication(string username, string password)
        {
            var path = Path.Combine(Path.GetTempPath(), "ytray-proxy-fallback-" + Guid.NewGuid());
            try
            {
                using var store = new InstanceStore(path, false, false);
                var runtime = new BrowserRuntime { BrowserKind = BrowserKind.Chrome, Name = "Chrome" };
                store.Runtimes.Add(runtime);
                store.Settings.DefaultRuntimeID = runtime.Id;
                store.Settings.ProxyUsername = username;
                store.Settings.ProxyPassword = password;
                store.Settings.ProxyServer = "http://127.0.0.1:8083";
                store.ConfirmLaunchWithoutPlugins = prompt =>
                {
                    Assert.IsFalse(prompt.CanSkipPlugins);
                    StringAssert.Contains(prompt.Message, "代理认证");
                    return true; // Even an erroneous UI acceptance must not bypass authentication.
                };
                Assert.IsFalse(store.Launch(LaunchMode.Custom));
                Assert.IsNotNull(store.ErrorMessage);
                StringAssert.Contains(store.ErrorMessage!, "认证信息");
                Assert.AreEqual(username, store.Settings.ProxyUsername);
                Assert.AreEqual(password, store.Settings.ProxyPassword);
                Assert.AreEqual(0, store.Instances.Count);
            }
            finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
        }

        [DataTestMethod]
        [DataRow(BrowserKind.ChromeForTesting, LaunchMode.Quick, true)]
        [DataRow(BrowserKind.Chromium, LaunchMode.Custom, true)]
        [DataRow(BrowserKind.Edge, LaunchMode.Quick, true)]
        [DataRow(BrowserKind.Chrome, LaunchMode.Isolated, true)]
        [DataRow(BrowserKind.Chrome, LaunchMode.Quick, false)]
        public void CompatibleLaunchDoesNotAskToSkip(BrowserKind kind, LaunchMode mode, bool withPlugin)
        {
            var path = Path.Combine(Path.GetTempPath(), "ytray-compatible-" + Guid.NewGuid());
            try
            {
                using var store = new InstanceStore(path, false, false);
                var runtime = new BrowserRuntime { BrowserKind = kind, Name = "Browser", ExecutablePath = "missing.exe" };
                store.Runtimes.Add(runtime);
                store.Settings.DefaultRuntimeID = runtime.Id;
                var plugin = new BrowserPlugin { Enabled = true };
                if (withPlugin) { store.Plugins.Add(plugin); store.Settings.DefaultPluginIDs.Add(plugin.Id); }
                store.ConfirmLaunchWithoutPlugins = _ => throw new AssertFailedException("Unexpected compatibility prompt");
                Assert.IsFalse(store.Launch(mode));
                // Missing executable fails after preflight, without a plugin confirmation.
                Assert.IsNotNull(store.ErrorMessage);
                Assert.IsFalse(store.ErrorMessage!.Contains("Unexpected compatibility prompt"));
                Assert.IsFalse(store.LaunchWasCancelled);
            }
            finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
        }
    }
}
