#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using YTray.Core;
using YTray.Models;

namespace YTray.Tests
{
    [TestClass]
    public class ExtensionFallbackTests
    {
        private const string ID = "abcdefghijklmnopabcdefghijklmnop";
        private static readonly string PluginPath = Path.GetFullPath("plugin");
        private static JObject List(bool enabled = true) => new JObject { ["extensions"] = new JArray(
            new JObject { ["id"] = ID, ["path"] = PluginPath, ["enabled"] = enabled }) };

        [TestMethod]
        public async Task ExistingEnabledPluginDoesNotGetReloaded()
        {
            var calls = 0;
            await BrowserExtensionService.EnsureLoadedAsync(new[] { PluginPath }, false, (method, args) =>
            {
                calls++; Assert.AreEqual("Extensions.getExtensions", method);
                return Task.FromResult(List());
            });
            Assert.AreEqual(1, calls);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task MissingOrDisabledPluginsUseNewApiAndVerifyEnabledState(bool disabled)
        {
            var calls = 0;
            await BrowserExtensionService.EnsureLoadedAsync(new[] { PluginPath, PluginPath }, false, (method, args) =>
            {
                calls++;
                if (calls == 1) return Task.FromResult(disabled ? List(false) : new JObject { ["extensions"] = new JArray() });
                if (calls == 2)
                {
                    Assert.AreEqual("Extensions.loadUnpacked", method);
                    Assert.AreEqual(PluginPath, args["path"]!.Value<string>());
                    return Task.FromResult(new JObject { ["id"] = ID });
                }
                return Task.FromResult(List());
            });
            Assert.AreEqual(3, calls);
        }

        [DataTestMethod]
        [DataRow("invalid-id")]
        [DataRow("disabled")]
        [DataRow("wrong-path")]
        [DataRow("malformed-manifest")]
        public async Task LoadFailureCannotBeReportedAsSuccess(string failure)
        {
            var calls = 0;
            await Assert.ThrowsExceptionAsync<ExtensionLoadingException>(() => BrowserExtensionService.EnsureLoadedAsync(new[] { PluginPath }, false, (method, args) =>
            {
                calls++;
                if (calls == 1) return Task.FromResult(new JObject { ["extensions"] = new JArray() });
                if (calls == 2)
                {
                    if (failure == "malformed-manifest") throw new DevToolsCommandException(-32602, "Invalid manifest");
                    return Task.FromResult(new JObject { ["id"] = failure == "invalid-id" ? "bad" : ID });
                }
                var list = List(failure != "disabled");
                if (failure == "wrong-path") list["extensions"]![0]!["path"] = Path.GetFullPath("different-plugin");
                return Task.FromResult(list);
            }));
        }

        [TestMethod]
        public async Task OlderApiWithoutListingCanStillLoadAndReturnAnId()
        {
            await BrowserExtensionService.EnsureLoadedAsync(new[] { PluginPath }, false, (method, args) =>
            {
                if (method == "Extensions.getExtensions") throw new DevToolsCommandException(-32601, "Unknown method");
                return Task.FromResult(new JObject { ["id"] = ID });
            });
        }

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task UnavailableApiOnlyRetainsLegacyWhenSupported(bool legacy)
        {
            var task = BrowserExtensionService.EnsureLoadedAsync(new[] { PluginPath }, legacy,
                (method, args) => throw new DevToolsCommandException(-32601, "Unknown method"));
            if (legacy) await task;
            else await Assert.ThrowsExceptionAsync<ExtensionLoadingException>(() => task);
        }

        [TestMethod]
        public async Task ObservedMissingPluginCannotSilentlyFallBackToLegacy()
        {
            await Assert.ThrowsExceptionAsync<ExtensionLoadingException>(() => BrowserExtensionService.EnsureLoadedAsync(new[] { PluginPath }, true,
                (method, args) => method == "Extensions.getExtensions"
                    ? Task.FromResult(new JObject { ["extensions"] = new JArray() })
                    : throw new DevToolsCommandException(-32601, "Unknown method")));
        }

        [DataTestMethod]
        [DataRow("136.0.1", true)]
        [DataRow("137.0.1", false)]
        [DataRow("152.0.7977.83", false)]
        [DataRow("", false)]
        public void LegacyChromeVersionBoundary(string version, bool supported)
        {
            Assert.AreEqual(supported, BrowserExtensionService.LegacyLoadingExpected(new BrowserRuntime
                { BrowserKind = BrowserKind.Chrome, Version = version }));
        }

        [DataTestMethod]
        [DataRow("user", "")]
        [DataRow("", "password")]
        [DataRow("user", "password")]
        public void FailedProxyAuthenticationCannotBeSkipped(string username, string password)
        {
            var prompt = new ExtensionLaunchPrompt(new BrowserRuntime(), new LaunchSettings
                { ProxyUsername = username, ProxyPassword = password }, 1, "Manifest invalid");
            Assert.IsFalse(prompt.CanSkipPlugins);
            StringAssert.Contains(prompt.Message, "Manifest invalid");
            StringAssert.Contains(prompt.Message, "代理认证");
        }
    }
}
