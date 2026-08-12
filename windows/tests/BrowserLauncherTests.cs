using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YTray.Core;
using YTray.Models;
using YTray.Native;

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
        public void InstanceAumidIsStableUniqueAndUsesCanonicalIconPropertyKey()
        {
            var id = Guid.Parse("492bec6a-f532-4d92-b6ef-4213dd1239ff");
            var same = AumidResolver.BuildInstanceAumid(BrowserKind.Chrome, "A", id);
            Assert.AreEqual("YTray.Chrome.InstA.492bec6af5324d92b6ef4213dd1239ff", same);
            Assert.AreEqual(same, AumidResolver.BuildInstanceAumid(BrowserKind.Chrome, "A", id));
            Assert.AreNotEqual(same, AumidResolver.BuildInstanceAumid(BrowserKind.Chrome, "B", id));
            Assert.IsTrue(same.Length <= 128);
            Assert.IsFalse(same.Contains(" "));

            // propkey.h: RelaunchIconResource = PID 3; RelaunchDisplayNameResource = PID 4.
            Assert.AreEqual(3, Win32.PKEY_AppUserModel_RelaunchIconResource.pid);
            Assert.AreEqual(4, Win32.PKEY_AppUserModel_RelaunchDisplayNameResource.pid);
        }

        [TestMethod]
        public void PropVariantUsesComAllocatorAndClearsSafely()
        {
            var value = PROPVARIANT.FromString("YTray.Chrome.InstA.test");
            Assert.AreEqual("YTray.Chrome.InstA.test", value.AsString());
            value.Dispose();
            Assert.AreEqual(PROPVARIANT.VT_EMPTY, value.vt);
            Assert.AreEqual(IntPtr.Zero, value.pwszVal);
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

        [TestMethod]
        public void StatePersistenceCreatesFirstStateFileAndKeepsEdgeDockPreferences()
        {
            var directory = Path.Combine(Path.GetTempPath(), "YTrayTests", Guid.NewGuid().ToString("N"));
            try
            {
                var state = new PersistedState
                {
                    Settings = new LaunchSettings
                    {
                        EdgeDockEnabled = false,
                        EdgeDockOnLeft = true,
                        EdgeDockYPercent = 37,
                        ThemePreference = AppThemePreference.Dark,
                    },
                };

                StatePersistence.Save(directory, state);

                Assert.IsTrue(File.Exists(StatePersistence.StatePath(directory)));
                var loaded = StatePersistence.Load(directory);
                Assert.IsNotNull(loaded);
                Assert.IsFalse(loaded.Settings.EdgeDockEnabled);
                Assert.IsTrue(loaded.Settings.EdgeDockOnLeft);
                Assert.AreEqual(37, loaded.Settings.EdgeDockYPercent);
                Assert.AreEqual(AppThemePreference.Dark, loaded.Settings.ThemePreference);
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [TestMethod]
        public void StatePersistenceNormalizesNullCollectionsFromLegacyOrDamagedJson()
        {
            var directory = Path.Combine(Path.GetTempPath(), "YTrayNullStateTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(StatePersistence.StatePath(directory),
                    "{\"Runtimes\":null,\"Plugins\":null,\"Instances\":null,\"Settings\":null}");
                var loaded = StatePersistence.Load(directory);
                Assert.IsNotNull(loaded);
                Assert.IsNotNull(loaded.Runtimes);
                Assert.IsNotNull(loaded.Plugins);
                Assert.IsNotNull(loaded.Instances);
                Assert.IsNotNull(loaded.Settings);
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [TestMethod]
        public void LaunchSettingsCloneDoesNotAliasMutableCollectionsOrSecrets()
        {
            var pluginId = Guid.NewGuid();
            var presetId = Guid.NewGuid();
            var original = new LaunchSettings
            {
                ProxyPassword = "secret",
                DefaultPluginIDs = new List<Guid> { pluginId },
                RecentProxyPresets = new List<ProxyPreset>
                {
                    new ProxyPreset("http://127.0.0.1:8080", "local", "user", "pass") { Id = presetId },
                },
            };

            var clone = original.Clone();
            clone.DefaultPluginIDs.Clear();
            clone.RecentProxyPresets[0].Remark = "changed";
            clone.ProxyPassword = "changed";

            Assert.AreEqual(1, original.DefaultPluginIDs.Count);
            Assert.AreEqual("local", original.RecentProxyPresets[0].Remark);
            Assert.AreEqual(presetId, clone.RecentProxyPresets[0].Id);
            Assert.AreEqual("secret", original.ProxyPassword);
        }

        [TestMethod]
        public void MirrorVersionDisplaysItsVersionAndFiltersForCurrentWindowsArchitecture()
        {
            var compatible = new MirrorVersion
            {
                Version = "150.0.7339.2",
                Artifacts = new List<MirrorArtifact>
                {
                    new MirrorArtifact { OS = "windows", Arch = RuntimeInstaller.Architecture, Format = "zip" },
                },
            };
            var incompatible = new MirrorVersion
            {
                Version = "149.0.7000.1",
                Artifacts = new List<MirrorArtifact>
                {
                    new MirrorArtifact { OS = "linux", Arch = "x64", Format = "zip" },
                },
            };

            Assert.AreEqual("150.0.7339.2", compatible.ToString());
            Assert.IsTrue(RuntimeInstaller.IsCompatible(compatible));
            Assert.IsFalse(RuntimeInstaller.IsCompatible(incompatible));
        }

        [TestMethod]
        public void InstallProgressFormatsBytesForUserFacingStatus()
        {
            Assert.AreEqual("512 B", RuntimeInstaller.FormatBytes(512));
            Assert.AreEqual("1.5 KB", RuntimeInstaller.FormatBytes(1536));
            Assert.AreEqual("2.0 MB", RuntimeInstaller.FormatBytes(2L * 1024 * 1024));
        }

        [TestMethod]
        public void ThemePaletteMutatesLiveBrushesAndUsesGrayDarkBorders()
        {
            var background = new SolidColorBrush(Colors.White);
            var hairline = new SolidColorBrush(Colors.White);
            var windowBorder = new SolidColorBrush(Colors.White);
            var resources = new ResourceDictionary
            {
                ["AppBackgroundColor"] = Colors.White,
                ["AppBackgroundBrush"] = background,
                ["HairlineColor"] = Colors.White,
                ["HairlineBrush"] = hairline,
                ["WindowBorderColor"] = Colors.White,
                ["WindowBorderBrush"] = windowBorder,
            };

            ThemeManager.ApplyPalette(resources, dark: true);

            Assert.AreSame(background, resources["AppBackgroundBrush"]);
            Assert.AreEqual((Color)ColorConverter.ConvertFromString("#17181A"), background.Color);
            Assert.AreEqual((Color)ColorConverter.ConvertFromString("#2B2D32"), hairline.Color);
            Assert.AreEqual((Color)ColorConverter.ConvertFromString("#303238"), windowBorder.Color);
            Assert.IsTrue(hairline.Color.R < 64 && hairline.Color.G < 64 && hairline.Color.B < 64,
                "Dark-theme separators must stay gray instead of becoming white.");

            var floatingBorder = new SolidColorBrush(Colors.White);
            var floatingHairline = new SolidColorBrush(Colors.White);
            var floatingSurface = new LinearGradientBrush(Colors.White, Colors.White, 90);
            var floatingResources = new ResourceDictionary
            {
                ["WidgetBorderBrush"] = floatingBorder,
                ["WidgetHairlineBrush"] = floatingHairline,
                ["WidgetSurfaceBrush"] = floatingSurface,
            };
            ThemeManager.ApplyLocalPalette(floatingResources, dark: true);
            Assert.AreEqual((Color)ColorConverter.ConvertFromString("#FF3A3C42"), floatingBorder.Color);
            Assert.AreEqual((Color)ColorConverter.ConvertFromString("#FF2C2E33"), floatingHairline.Color);
            Assert.AreEqual((Color)ColorConverter.ConvertFromString("#FF212226"), floatingSurface.GradientStops[0].Color);
            Assert.AreEqual((Color)ColorConverter.ConvertFromString("#FF1A1B1E"), floatingSurface.GradientStops[1].Color);

            ThemeManager.ApplyPalette(resources, dark: false);
            ThemeManager.ApplyLocalPalette(floatingResources, dark: false);

            Assert.AreSame(background, resources["AppBackgroundBrush"]);
            Assert.AreEqual((Color)ColorConverter.ConvertFromString("#F5F5F3"), background.Color);
            Assert.AreEqual((Color)ColorConverter.ConvertFromString("#E3E3DF"), hairline.Color);
            Assert.AreEqual((Color)ColorConverter.ConvertFromString("#D8D8D4"), windowBorder.Color);
            Assert.AreEqual((Color)ColorConverter.ConvertFromString("#FFD5D5D1"), floatingBorder.Color);
            Assert.AreEqual((Color)ColorConverter.ConvertFromString("#FFE6E6E2"), floatingHairline.Color);
        }

        [TestMethod]
        public void BadgedProcessIconRendersAVisibleOverlay()
        {
            using (var icon = BrowserProcessIcon.RenderIcon(null, "A", 64))
            {
                Assert.AreEqual(64, icon.Width);
                Assert.AreEqual(64, icon.Height);
                Assert.IsTrue(icon.GetPixel(12, 12).A > 0, "A badge should occupy the top-left icon area");
                var center = icon.GetPixel(18, 18);
                Assert.IsTrue(center.R < 90 && center.G < 90 && center.B < 90,
                    "The instance letter should use a dark high-contrast foreground on orange");
                Assert.AreEqual(0, icon.GetPixel(58, 58).A,
                    "The bottom-right corner must remain available for Chrome for Testing's T badge");
            }
        }

        [TestMethod]
        public void CdpThumbnailLoadsFromStreamWithoutUriCacheFailure()
        {
            var directory = Path.Combine(Path.GetTempPath(), "YTrayThumbnailTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "preview.jpg");
            try
            {
                using (var bitmap = new System.Drawing.Bitmap(124, 76))
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                {
                    graphics.Clear(System.Drawing.Color.CornflowerBlue);
                    bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Jpeg);
                }

                var source = InstanceThumbnailImageSource.LoadNowForTests(path) as System.Windows.Media.Imaging.BitmapSource;
                Assert.IsNotNull(source, "A valid CDP JPEG must not fall back to the loading placeholder");
                Assert.IsTrue(source.PixelWidth > 0 && source.PixelHeight > 0);
                Assert.IsTrue(source.IsFrozen, "The decoded preview must be safe to reuse across WPF refreshes");
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [TestMethod]
        public void ProcessIconWritesNativeTaskbarSizes()
        {
            var directory = Path.Combine(Path.GetTempPath(), "YTrayIconTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var id = Guid.NewGuid();
                var path = BrowserProcessIcon.Write(typeof(BrowserLauncherTests).Assembly.Location, "B", id, directory);
                using (var stream = File.OpenRead(path))
                using (var reader = new BinaryReader(stream))
                {
                    Assert.AreEqual((ushort)0, reader.ReadUInt16());
                    Assert.AreEqual((ushort)1, reader.ReadUInt16());
                    Assert.AreEqual((ushort)8, reader.ReadUInt16());
                    var sizes = new List<int>();
                    for (var index = 0; index < 8; index++)
                    {
                        var width = reader.ReadByte();
                        reader.ReadBytes(15);
                        sizes.Add(width == 0 ? 256 : width);
                    }
                    CollectionAssert.AreEqual(new[] { 16, 20, 24, 32, 40, 48, 64, 256 }, sizes);
                }
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }
    }
}
