#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using YTray.Core;
using YTray.Core.BrowserUse;
using YTray.Models;

namespace YTray.Tests
{
    [TestClass]
    public class BrowserEnvironmentTests
    {
        [TestMethod]
        public void EnvironmentPersistsAndKeepsItsSavedProxy()
        {
            var directory = Path.Combine(Path.GetTempPath(), "ytray-environment-" + Guid.NewGuid().ToString("N"));
            try
            {
                Guid templateId;
                Guid proxyId;
                using (var store = new InstanceStore(directory, false, false))
                {
                    store.Settings.PresetProxyHost = "first.proxy.test";
                    store.Settings.PresetProxyPort = 8121;
                    store.RememberPresetProxy();
                    var proxy = store.Settings.RecentProxyPresets[0];
                    proxyId = proxy.Id;
                    var environment = new BrowserEnvironment
                    {
                        Name = "French",
                        Language = "fr-FR",
                        TimeZone = "Asia/Tokyo",
                        Network = BrowserEnvironmentNetwork.SavedProxy,
                        ProxyPresetId = proxyId,
                    };
                    store.SaveBrowserEnvironment(environment);
                    templateId = environment.Id;
                    store.RememberPresetProxy();
                    Assert.AreEqual(proxyId, store.Settings.RecentProxyPresets[0].Id);
                    for (var index = 0; index < 6; index++)
                    {
                        store.Settings.PresetProxyHost = "next.proxy.test";
                        store.Settings.PresetProxyPort = 8200 + index;
                        store.RememberPresetProxy();
                    }
                    var saved = store.Settings.RecentProxyPresets.Single(item => item.Id == proxyId);
                    var launch = store.QuickLaunchConfiguration(usePresetProxy: true, saved)!;
                    Assert.AreEqual(proxy.Server, launch.ProxyServer);
                    Assert.AreEqual("http://next.proxy.test:8205", store.Settings.PresetProxyServer);
                    environment.Language = "en-US";
                    Assert.AreEqual("fr-FR", store.BrowserEnvironments.Single().Language);
                }
                using var restored = new InstanceStore(directory, false, false);
                var stored = restored.BrowserEnvironments.Single();
                Assert.AreEqual(templateId, stored.Id);
                Assert.AreEqual("fr-FR", stored.Language);
                Assert.AreEqual(proxyId, stored.ProxyPresetId);
                Assert.IsTrue(restored.Settings.RecentProxyPresets.Any(item => item.Id == proxyId));
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }

        [TestMethod]
        public void EnvironmentIsAcceptedForNewInstancesOnly()
        {
            var environment = Guid.NewGuid().ToString("D");
            var instance = Guid.NewGuid().ToString("D");
            var args = new JObject { ["url"] = "https://example.test/", ["environment_id"] = environment };
            Assert.AreEqual("open", BrowserUseTools.Parse(new JObject { ["name"] = "browser_open", ["arguments"] = args }).Method);
            args["instance_id"] = instance;
            var error = Assert.ThrowsExactly<BrowserUseException>(() =>
                BrowserUseTools.Parse(new JObject { ["name"] = "browser_open", ["arguments"] = args }));
            Assert.AreEqual("invalid_arguments", error.Code);
        }

        [TestMethod]
        public async Task StartupCleanupTargetsOnlyDeclaredExtensionOptions()
        {
            var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ytray-extension-options-" + Guid.NewGuid())).FullName;
            try
            {
                File.WriteAllText(Path.Combine(directory, "manifest.json"), "{\"options_ui\":{\"page\":\"settings.html\"}}");
                var id = ExtensionInstaller.ChromiumExtensionId(directory, null);
                var open = new JArray
                {
                    new JObject { ["targetId"] = "options", ["type"] = "page", ["url"] = $"chrome-extension://{id}/settings.html?install" },
                    new JObject { ["targetId"] = "embedded", ["type"] = "page", ["url"] = $"chrome://extensions/?options={id}" },
                    new JObject { ["targetId"] = "bootstrap", ["type"] = "page", ["url"] = $"chrome-extension://{id}/ytray-bootstrap.html" },
                    new JObject { ["targetId"] = "external", ["type"] = "page", ["url"] = "https://example.test/settings.html" },
                    new JObject { ["targetId"] = "manager", ["type"] = "page", ["url"] = "chrome://extensions/" },
                };
                var closed = new List<string>();
                await BrowserExtensionService.CloseInstallOptionsAsync(new[] { directory }, (method, args) =>
                {
                    if (method == "Target.getTargets") return Task.FromResult(new JObject { ["targetInfos"] = open.DeepClone() });
                    Assert.AreEqual("Target.closeTarget", method);
                    var targetId = (string)args["targetId"]!;
                    closed.Add(targetId);
                    open.OfType<JObject>().Single(item => (string?)item["targetId"] == targetId).Remove();
                    return Task.FromResult(new JObject { ["success"] = true });
                }, CancellationToken.None);
                CollectionAssert.AreEquivalent(new[] { "options", "embedded" }, closed);
            }
            finally { Directory.Delete(directory, true); }
        }
    }
}
