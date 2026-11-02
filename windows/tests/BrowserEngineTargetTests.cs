using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using YTray.Core;
using YTray.Models;

namespace YTray.Tests
{
    [TestClass]
    public class BrowserEngineTargetTests
    {
        private static BrowserEngineTarget Target(string process = "process-a", string frontend = "memfit") => new BrowserEngineTarget
        {
            EngineIdentityId = "stable-key", EngineInstanceId = process, FrontendId = frontend,
            FrontendName = frontend == "memfit" ? "Memfit AI" : "Yakit", Endpoint = "ws://127.0.0.1:14333/extension",
        };

        [TestMethod]
        public void RememberedFrontendNeverFallsBackToAnotherApplication()
        {
            var saved = Target();
            var other = Target("other-process", "yakit");
            var selected = BrowserEngineDiscovery.ResolveRemembered(saved, new[] { other });
            Assert.AreEqual("memfit", selected.FrontendId);
            Assert.AreEqual("process-a", selected.EngineInstanceId);
            var restart = Target("process-b");
            Assert.AreSame(restart, BrowserEngineDiscovery.ResolveRemembered(saved, new[] { other, restart }));
            Assert.IsNull(BrowserEngineDiscovery.ResolveRemembered(saved, new[] { restart, Target("process-c") }));
        }

        [TestMethod]
        public void InstanceSnapshotsPersistIndependentTargetChoices()
        {
            var settings = new LaunchSettings { EngineTarget = Target() };
            var copy = settings.Clone();
            copy.EngineTarget.FrontendName = "another";
            Assert.AreEqual("Memfit AI", settings.EngineTarget.FrontendName);
            var restored = JsonConvert.DeserializeObject<LaunchSettings>(JsonConvert.SerializeObject(settings));
            Assert.AreEqual("stable-key", restored.EngineTarget.EngineIdentityId);
            Assert.AreEqual("memfit", restored.EngineTarget.FrontendId);
            var url = BrowserLauncher.ManagedBrowserBootstrapURL("extension-id", Guid.NewGuid(), "A", "https://example.test", false,
                engineTarget: settings.EngineTarget);
            StringAssert.Contains(Uri.UnescapeDataString(url), "\"engineIdentityId\":\"stable-key\"");
            StringAssert.Contains(Uri.UnescapeDataString(url), "\"frontendId\":\"memfit\"");
        }

        [TestMethod]
        public void MissingDiscoveryKeepsLegacyConnectionAndExplicitOptOutRemainsSeparate()
        {
            var legacy = BrowserEngineDiscovery.ResolveForLaunch(null, new BrowserEngineTarget[0], false);
            Assert.AreEqual("legacy", legacy.Mode);
            var restoredLegacy = BrowserEngineDiscovery.ResolveForLaunch(null, new[] { Target() }, true);
            Assert.AreEqual("legacy", restoredLegacy.Mode);
            var savedLegacy = BrowserEngineDiscovery.ResolveForLaunch(legacy, new[] { Target() }, false);
            Assert.AreEqual("legacy", savedLegacy.Mode);
            var optedOut = BrowserEngineDiscovery.ResolveForLaunch(new BrowserEngineTarget { Mode = "none" }, new[] { Target() }, false);
            Assert.AreEqual("none", optedOut.Mode);

            var url = BrowserLauncher.ManagedBrowserBootstrapURL("extension-id", Guid.NewGuid(), "A", "https://example.test", false);
            StringAssert.Contains(Uri.UnescapeDataString(url), "\"mode\":\"legacy\"");
        }

        [TestMethod]
        public void RememberedTargetNeverFallsBackToLegacyWhenDiscoveryDisappears()
        {
            var saved = Target();
            var resolved = BrowserEngineDiscovery.ResolveForLaunch(saved, new BrowserEngineTarget[0], true);
            Assert.AreEqual("engine", resolved.Mode);
            Assert.AreEqual("stable-key", resolved.EngineIdentityId);
            Assert.AreEqual("process-a", resolved.EngineInstanceId);
            Assert.AreEqual("memfit", resolved.FrontendId);
            Assert.IsNull(BrowserEngineDiscovery.ResolveForLaunch(null, new[] { Target(), Target("other", "yakit") }, false));
        }

        [TestMethod]
        public void NativeDiscoveryUsesTheProbedPortInsteadOfAnAdvertisedRedirect()
        {
            var target = BrowserEngineDiscovery.Parse("{\"type\":\"engine\",\"protocolVersion\":3,\"engineIdentityId\":\"key\",\"engineInstanceId\":\"process\",\"frontendId\":\"memfit\",\"frontendName\":\"Memfit AI\",\"endpoint\":\"ws://remote.example/extension\"}", 14333);
            Assert.AreEqual("ws://127.0.0.1:14333/extension", target.Endpoint);
            Assert.AreEqual("Memfit AI", target.FrontendName);
        }
    }
}
