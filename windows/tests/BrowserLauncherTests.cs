using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YTray.Core;
using YTray.Models;

namespace YTray.Tests
{
    [TestClass]
    public class BrowserLauncherTests
    {
        [TestMethod]
        public void DockBadgeSequenceAndValidation()
        {
            Assert.AreEqual("A", DockBadgeLabel.DefaultLabel(1));
            Assert.AreEqual("Z", DockBadgeLabel.DefaultLabel(26));
            Assert.AreEqual("AA", DockBadgeLabel.DefaultLabel(27));
            Assert.AreEqual("AB", DockBadgeLabel.DefaultLabel(28));
            Assert.AreEqual("AZ", DockBadgeLabel.DefaultLabel(52));
            Assert.AreEqual("BA", DockBadgeLabel.DefaultLabel(53));
            Assert.AreEqual("ZZ", DockBadgeLabel.DefaultLabel(702));
            Assert.AreEqual("AB", DockBadgeLabel.Normalize(" ab "));
            Assert.ThrowsException<YTrayException>(() => DockBadgeLabel.Normalize(""));
            Assert.ThrowsException<YTrayException>(() => DockBadgeLabel.Normalize("ABC"));
            Assert.ThrowsException<YTrayException>(() => DockBadgeLabel.Normalize("A1"));
            Assert.ThrowsException<YTrayException>(() => DockBadgeLabel.Normalize("中"));
        }

        [TestMethod]
        public void BrowserKindInference()
        {
            Assert.AreEqual(BrowserKind.ChromeForTesting, BrowserKindExtensions.Infer("Google Chrome for Testing", "/tmp/chrome"));
            Assert.AreEqual(BrowserKind.Edge, BrowserKindExtensions.Infer("Microsoft Edge", @"C:\Applications\Microsoft Edge.app"));
            Assert.AreEqual(BrowserKind.Edge, BrowserKindExtensions.Infer("Microsoft Edge Beta", @"C:\Applications\Microsoft Edge Beta.app"));
            Assert.AreEqual(BrowserKind.ChromeBeta, BrowserKindExtensions.Infer("Google Chrome Beta", "/tmp/chrome"));
            Assert.AreEqual(BrowserKind.ChromeCanary, BrowserKindExtensions.Infer("Google Chrome Canary", "/tmp/chrome"));
            Assert.AreEqual(BrowserKind.Chromium, BrowserKindExtensions.Infer("Chromium", "/tmp/chromium"));
            Assert.AreEqual(BrowserKind.Chrome, BrowserKindExtensions.Infer("Google Chrome", "/tmp/chrome"));
        }

        [TestMethod]
        public void HttpProxyNormalization()
        {
            Assert.AreEqual("http://127.0.0.1:8083", HTTPProxyAddress.Normalize("127.0.0.1:8083"));
            Assert.AreEqual("https://proxy.example:8443", HTTPProxyAddress.Normalize(" HTTPS://proxy.example:8443/ "));
            Assert.ThrowsException<YTrayException>(() => HTTPProxyAddress.Normalize(""));
            Assert.ThrowsException<YTrayException>(() => HTTPProxyAddress.Normalize("socks5://127.0.0.1:1080"));
            Assert.AreEqual("https://proxy.example:8443", HTTPProxyAddress.Build(ProxyScheme.Https, "proxy.example", 8443));
            var ep = HTTPProxyAddress.Split("http://[::1]:8083");
            Assert.AreEqual(ProxyScheme.Http, ep.Scheme);
            Assert.AreEqual("::1", ep.Host);
            Assert.AreEqual(8083, ep.Port);
        }

        [TestMethod]
        public void ProxyProbeBuildsBasicAuthAndInterpretsResponses()
        {
            var req = ProxyConnectivityChecker.ProbeRequest(new Uri("https://example.com/"), "yak", "secret");
            var text = System.Text.Encoding.UTF8.GetString(req);
            Assert.IsTrue(text.StartsWith("CONNECT example.com:443 HTTP/1.1"));
            Assert.IsTrue(text.Contains("Proxy-Authorization: Basic eWFrOnNlY3JldA=="));

            var ok = ProxyConnectivityChecker.InterpretResponse(System.Text.Encoding.UTF8.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n"), true);
            Assert.IsTrue(ok.IsSuccess);
            Assert.IsTrue(ok.Message.Contains("代理和认证可用"));

            var fail = ProxyConnectivityChecker.InterpretResponse(System.Text.Encoding.UTF8.GetBytes("HTTP/1.1 407 Proxy Authentication Required\r\n\r\n"), true);
            Assert.IsFalse(fail.IsSuccess);
            Assert.IsTrue(fail.Message.Contains("认证失败"));
        }

        [TestMethod]
        public void QuickArgumentsKeepInstanceIsolatedAndDebugLocal()
        {
            var settings = new LaunchSettings { ProxyServer = "http://127.0.0.1:8080" };
            var args = BrowserLauncher.BuildArguments(LaunchMode.Quick, settings, @"C:\tmp\profile", 9333, new List<BrowserPlugin>());
            Assert.IsTrue(args.Contains(@"--user-data-dir=C:\tmp\profile"));
            Assert.IsTrue(args.Contains("--remote-debugging-address=127.0.0.1"));
            Assert.IsTrue(args.Contains("--remote-debugging-port=9333"));
            Assert.IsTrue(args.Contains("--remote-allow-origins=http://127.0.0.1:9333"));
            Assert.IsTrue(args.Contains("--no-first-run"));
            Assert.IsTrue(args.Contains("--no-default-browser-check"));
            Assert.IsTrue(args.Contains("--proxy-server=http://127.0.0.1:8080"));
            Assert.IsFalse(args.Contains("--no-proxy-server"));
            Assert.IsTrue(args.Contains("--force-webrtc-ip-handling-policy=disable_non_proxied_udp"));
            Assert.IsTrue(args.Contains("--ignore-certificate-errors"));
        }

        [TestMethod]
        public void NoProxyAndHttpProxyLaunchesAreExclusive()
        {
            var direct = new LaunchSettings { ProxyServer = "" };
            var directArgs = BrowserLauncher.BuildArguments(LaunchMode.Quick, direct, "/tmp/direct", 9336, new List<BrowserPlugin>());
            Assert.IsTrue(directArgs.Contains("--no-proxy-server"));
            Assert.IsFalse(directArgs.Any(a => a.StartsWith("--proxy-server=")));

            var proxy = new LaunchSettings { ProxyServer = "http://127.0.0.1:8083" };
            var proxyArgs = BrowserLauncher.BuildArguments(LaunchMode.Quick, proxy, "/tmp/proxy", 9337, new List<BrowserPlugin>());
            Assert.IsTrue(proxyArgs.Contains("--proxy-server=http://127.0.0.1:8083"));
            Assert.IsFalse(proxyArgs.Contains("--no-proxy-server"));
        }

        [TestMethod]
        public void IsolatedModeDoesNotCopyProxyPluginsOrExtraFlags()
        {
            var settings = new LaunchSettings { ProxyServer = "socks5://127.0.0.1:1080", AdditionalFlags = "--incognito" };
            var plugin = new BrowserPlugin { Name = "T", Version = "1", Path = "/tmp/plugin", ManifestVersion = 3 };
            var args = BrowserLauncher.BuildArguments(LaunchMode.Isolated, settings, "/tmp/profile", 9444, new List<BrowserPlugin> { plugin });
            Assert.IsFalse(args.Any(a => a.StartsWith("--proxy-server")));
            Assert.IsFalse(args.Any(a => a.StartsWith("--load-extension")));
            Assert.IsFalse(args.Contains("--incognito"));
            Assert.IsTrue(args.Contains("--user-data-dir=/tmp/profile"));
            Assert.IsTrue(args.Contains("--ignore-certificate-errors"));
        }

        [TestMethod]
        public void CustomFlagsCannotOverrideIsolationBoundary()
        {
            var settings = new LaunchSettings { AdditionalFlags = "--user-data-dir=/tmp/shared" };
            Assert.ThrowsException<YTrayException>(() =>
                BrowserLauncher.BuildArguments(LaunchMode.Custom, settings, "/tmp/isolated", 9555, new List<BrowserPlugin>()));

            foreach (var flag in new[]
            {
                "--proxy-server=http://127.0.0.1:9999",
                "--no-proxy-server",
                "--proxy-pac-url=http://127.0.0.1/proxy.pac",
                "--proxy-auto-detect",
                "--proxy-bypass-list=localhost",
            })
            {
                settings.AdditionalFlags = flag;
                Assert.ThrowsException<YTrayException>(() =>
                    BrowserLauncher.BuildArguments(LaunchMode.Custom, settings, "/tmp/isolated", 9555, new List<BrowserPlugin>()),
                    "代理边界不应被附加参数覆盖：" + flag);
            }
        }

        [TestMethod]
        public void UnpackedPluginsUseBothChromeFlags()
        {
            var settings = new LaunchSettings();
            var plugin = new BrowserPlugin { Name = "Local", Version = "1.0", Path = "/tmp/local-extension", ManifestVersion = 3 };
            var args = BrowserLauncher.BuildArguments(LaunchMode.Quick, settings, "/tmp/profile", 9666, new List<BrowserPlugin> { plugin });
            Assert.IsTrue(args.Contains("--load-extension=/tmp/local-extension"));
            Assert.IsTrue(args.Contains("--disable-extensions-except=/tmp/local-extension"));
        }

        [TestMethod]
        public void ChromeForTestingBannerDisabledOnlyForTestingRuntime()
        {
            var settings = new LaunchSettings();
            var testing = BrowserLauncher.BuildArguments(LaunchMode.Quick, settings, "/tmp/cft", 9334, new List<BrowserPlugin>(), BrowserKind.ChromeForTesting);
            Assert.IsTrue(testing.Contains("--disable-infobars"));
            var regular = BrowserLauncher.BuildArguments(LaunchMode.Quick, settings, "/tmp/chrome", 9335, new List<BrowserPlugin>(), BrowserKind.Chrome);
            Assert.IsFalse(regular.Contains("--disable-infobars"));
        }

        [TestMethod]
        public void RestoreUsesExistingProfileAndRestoresSession()
        {
            var settings = new LaunchSettings { HomeURL = "https://should-not-open.example/" };
            var args = BrowserLauncher.BuildArguments(LaunchMode.Quick, settings, "/tmp/existing", 9445, new List<BrowserPlugin>(), restoreLastSession: true);
            Assert.IsTrue(args.Contains("--user-data-dir=/tmp/existing"));
            Assert.IsTrue(args.Contains("--restore-last-session"));
            Assert.IsFalse(args.Contains("https://should-not-open.example/"));
        }

        [TestMethod]
        public void OfficialChromeAndTestingExtensionCapabilities()
        {
            Assert.IsFalse(BrowserLauncher.SupportsCommandLineExtensions(BrowserKind.Chrome));
            Assert.IsFalse(BrowserLauncher.SupportsCommandLineExtensions(BrowserKind.ChromeBeta));
            Assert.IsFalse(BrowserLauncher.SupportsCommandLineExtensions(BrowserKind.ChromeCanary));
            Assert.IsTrue(BrowserLauncher.SupportsCommandLineExtensions(BrowserKind.ChromeForTesting));
            Assert.IsTrue(BrowserLauncher.SupportsCommandLineExtensions(BrowserKind.Chromium));
            Assert.IsTrue(BrowserLauncher.SupportsCommandLineExtensions(BrowserKind.Edge));
        }

        [TestMethod]
        public void AumidProfileIdRuleMatchesChromiumFormat()
        {
            // Default profile -> empty profile_id
            var defaultProfile = Path.Combine(AumidResolver.DefaultChromeUserDataDir, "Default");
            Assert.AreEqual("", AumidResolver.ComputeProfileId(defaultProfile));

            // Custom user-data-dir -> parentbasename.profilebasename, alnum+dot only
            var profileA = Path.Combine(@"C:\YTray\InstA", "Default");
            Assert.AreEqual("InstA.Default", AumidResolver.ComputeProfileId(profileA));
            Assert.AreEqual("Chrome.InstA.Default", AumidResolver.BuildExpectedAumid("Chrome", AumidResolver.ComputeProfileId(profileA)));

            // Spaces stripped: "Profile 1" -> "Profile1"
            var profile1 = Path.Combine(@"C:\YTray\InstB", "Profile 1");
            Assert.AreEqual("InstB.Profile1", AumidResolver.ComputeProfileId(profile1));
        }

        [TestMethod]
        public void ProxyReportAggregatesSuccessAcrossTargets()
        {
            var report = new ProxyCheckReport
            {
                Details = new List<ProxyCheckDetail>
                {
                    new ProxyCheckDetail { Target = "https://example.com/", IsSuccess = false, Message = "超时" },
                    new ProxyCheckDetail { Target = "https://baidu.com/", IsSuccess = true, Message = "检测成功" },
                    new ProxyCheckDetail { Target = "https://google.com/", IsSuccess = false, Message = "连接失败" },
                },
            };
            Assert.IsTrue(report.IsSuccess);
            Assert.AreEqual(1, report.SuccessCount);
            Assert.IsTrue(report.Message.Contains("1/3"));
        }
    }
}