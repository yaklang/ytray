using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YTray.Core;

namespace YTray.Tests
{
    [TestClass]
    public class AppUpdateServiceTests
    {
        [TestMethod]
        public void VersionComparisonHandlesStableAndPrereleaseBuilds()
        {
            Assert.IsTrue(AppUpdateService.CompareVersions("0.1.3", "0.1.2") > 0);
            Assert.IsTrue(AppUpdateService.CompareVersions("0.1.3", "0.1.3-beta.4") > 0);
            Assert.IsTrue(AppUpdateService.CompareVersions("0.1.3-beta.10", "0.1.3-beta.2") > 0);
            Assert.IsTrue(AppUpdateService.CompareVersions("0.1.3-beta10", "0.1.3-beta2") > 0);
            Assert.IsTrue(AppUpdateService.CompareVersions("0.1.3-beta.2", "0.1.3") < 0);
            Assert.AreEqual(0, AppUpdateService.CompareVersions("v0.1.2+build.9", "0.1.2"));
        }

        [TestMethod]
        public void ReleaseAssetSelectionKeepsTheInstalledArchitecture()
        {
            var release = new AppReleaseManifest
            {
                Version = "0.1.3",
                Assets = new[]
                {
                    new AppReleaseAsset { Platform = "windows", Architecture = "386", Kind = "setup", Filename = "x86.exe" },
                    new AppReleaseAsset { Platform = "windows", Architecture = "amd64", Kind = "setup", Filename = "x64.exe" },
                    new AppReleaseAsset { Platform = "darwin", Architecture = "arm64", Kind = "dmg", Filename = "mac.dmg" },
                },
            };

            var selected = AppUpdateService.SelectAsset(release, "windows", "amd64", "setup");

            Assert.IsNotNull(selected);
            Assert.AreEqual("x64.exe", selected!.Filename);
        }

        [TestMethod]
        public void BuildVersionComesFromRepositoryReleaseVersion()
        {
            var versionFile = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "VERSION"));
            Assert.AreEqual(File.ReadAllText(versionFile).Trim(), YTrayBuildInfo.Version);
        }

        [TestMethod]
        public async Task ManifestTimeoutLeavesCheckingStateAndAllowsRetry()
        {
            var handler = new NeverCompletingHandler();
            using (var service = new AppUpdateService(
                handler,
                TimeSpan.FromMilliseconds(40), enabled: true))
            {
                await service.CheckAsync();

                Assert.AreEqual(AppUpdatePhase.Failed, service.Phase);
                Assert.AreEqual("检查更新超时，请稍后重试", service.StatusText);
                Assert.IsFalse(service.IsBusy);
                Assert.AreEqual("检查更新", service.ActionLabel);

                await service.CheckAsync();
                Assert.AreEqual(2, handler.RequestCount);
                Assert.AreEqual(AppUpdatePhase.Failed, service.Phase);
            }
        }

        [TestMethod]
        public void DefaultUpdateClientAcceptsCompressedOssResponses()
        {
            using (var handler = AppUpdateService.CreateDefaultHandler())
            {
                Assert.IsTrue(handler.AutomaticDecompression.HasFlag(DecompressionMethods.GZip));
                Assert.IsTrue(handler.AutomaticDecompression.HasFlag(DecompressionMethods.Deflate));
                Assert.IsFalse(handler.AllowAutoRedirect);
                Assert.IsFalse(handler.UseCookies);
                Assert.IsFalse(handler.UseDefaultCredentials);
            }
        }

        [TestMethod]
        public async Task GzipManifestIsDecodedEvenWhenAnIntermediaryLeavesItCompressed()
        {
            var manifest = "{\"schema_version\":1,\"product\":\"ytray\",\"version\":\"99.0.0\","
                + "\"assets\":[{\"platform\":\"windows\",\"architecture\":\""
                + (Environment.Is64BitProcess ? "amd64" : "386")
                + "\",\"kind\":\"setup\",\"filename\":\"YTray-99.0.0-windows-" + (Environment.Is64BitProcess ? "amd64" : "386") + "-setup.exe\","
                + "\"url\":\"https://aliyun-oss.yaklang.com/ytray/99.0.0/YTray-99.0.0-windows-" + (Environment.Is64BitProcess ? "amd64" : "386") + "-setup.exe\","
                + "\"sha256\":\"" + new string('a', 64) + "\",\"size\":123}]}";
            using (var service = new AppUpdateService(
                new StaticResponseHandler(() => GzipResponse(manifest)), enabled: true))
            {
                await service.CheckAsync();

                Assert.AreEqual(AppUpdatePhase.Available, service.Phase);
                Assert.AreEqual("99.0.0", service.AvailableVersion);
                StringAssert.Contains(service.StatusText, "发现新版本 v99.0.0");
            }
        }

        [TestMethod]
        public async Task InvalidManifestUsesAChineseUserFacingMessage()
        {
            using (var service = new AppUpdateService(new StaticResponseHandler(() =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("proxy returned an error page", Encoding.UTF8, "application/json"),
                }), enabled: true))
            {
                await service.CheckAsync();

                Assert.AreEqual(AppUpdatePhase.Failed, service.Phase);
                Assert.AreEqual("检查更新失败 · 更新服务器返回的数据无法识别", service.StatusText);
            }
        }

        [TestMethod]
        public async Task ThrowingObserverCannotLeaveUpdaterInCheckingState()
        {
            var manifest = "{\"schema_version\":1,\"product\":\"ytray\",\"version\":\"99.0.0\","
                + "\"assets\":[{\"platform\":\"windows\",\"architecture\":\""
                + (Environment.Is64BitProcess ? "amd64" : "386")
                + "\",\"kind\":\"setup\",\"filename\":\"YTray-99.0.0-windows-" + (Environment.Is64BitProcess ? "amd64" : "386") + "-setup.exe\","
                + "\"url\":\"https://aliyun-oss.yaklang.com/ytray/99.0.0/YTray-99.0.0-windows-" + (Environment.Is64BitProcess ? "amd64" : "386") + "-setup.exe\","
                + "\"sha256\":\"" + new string('a', 64) + "\",\"size\":123}]}";
            using (var service = new AppUpdateService(new StaticResponseHandler(() =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(manifest, Encoding.UTF8, "application/json"),
                }), enabled: true))
            {
                var notifications = 0;
                service.PropertyChanged += (sender, args) =>
                {
                    Interlocked.Increment(ref notifications);
                    throw new InvalidOperationException("Broken UI observer");
                };

                await service.CheckAsync();

                Assert.IsTrue(notifications > 0);
                Assert.AreEqual(AppUpdatePhase.Available, service.Phase);
                Assert.IsFalse(service.IsBusy);
                Assert.AreEqual("99.0.0", service.AvailableVersion);
            }
        }

        [TestMethod]
        public async Task DevelopmentModeMakesNoRequestsAndCannotInstall()
        {
            var handler = new NeverCompletingHandler();
            using (var service = new AppUpdateService(handler, enabled: false))
            {
                await service.CheckAsync();
                service.InstallUpdate();
                Assert.AreEqual(0, handler.RequestCount);
                Assert.AreEqual(AppUpdatePhase.Idle, service.Phase);
            }
        }

        [TestMethod]
        public void BusyWorkPreventsTheNativeInstaller()
        {
            using (var service = new AppUpdateService(enabled: true))
            {
                service.CanInstall = () => false;
                service.InstallUpdate();
                Assert.AreEqual(AppUpdatePhase.Failed, service.Phase);
                StringAssert.Contains(service.StatusText, "请先完成");
                Assert.IsFalse(service.IsBusy);
            }
        }

        [TestMethod]
        public void NativeEngineMatchesTheProcessArchitecture()
        {
            var bytes = NativeAppUpdater.VerifiedEngineBytes();
            Assert.IsTrue(bytes.Length > 1000000);
            var peOffset = BitConverter.ToInt32(bytes, 0x3c);
            Assert.AreEqual(Environment.Is64BitProcess ? 0x8664 : 0x14c, (int)BitConverter.ToUInt16(bytes, peOffset + 4));
        }

        [TestMethod]
        public async Task CatalogRejectsWrongOriginDuplicatesAndOversizedBodies()
        {
            var architecture = Environment.Is64BitProcess ? "amd64" : "386";
            var filename = "YTray-99.0.0-windows-" + architecture + "-setup.exe";
            var asset = "{\"platform\":\"windows\",\"architecture\":\"" + architecture + "\",\"kind\":\"setup\",\"filename\":\"" + filename
                + "\",\"url\":\"https://aliyun-oss.yaklang.com/ytray/99.0.0/" + filename + "\",\"sha256\":\"" + new string('a', 64) + "\",\"size\":123}";
            var valid = "{\"schema_version\":1,\"product\":\"ytray\",\"version\":\"99.0.0\",\"assets\":[" + asset + "]}";
            foreach (var invalid in new[] {
                valid.Replace("aliyun-oss.yaklang.com", "example.com"),
                valid.Replace("\"ytray\"", "\"yconnect\""),
                valid.Replace("99.0.0", "099.0.0"),
                valid.Replace("[" + asset + "]", "[" + asset + "," + asset + "]"),
                valid.Replace("123}", "-1}"),
                new string(' ', AppUpdateService.MaximumManifestBytes + 1),
            })
            {
                using (var service = new AppUpdateService(new StaticResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(invalid) }), enabled: true))
                {
                    await service.CheckAsync();
                    Assert.AreEqual(AppUpdatePhase.Failed, service.Phase);
                    Assert.IsFalse(service.IsUpdateAvailable);
                }
            }
        }

        private static HttpResponseMessage GzipResponse(string value)
        {
            byte[] compressed;
            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
                {
                    var input = Encoding.UTF8.GetBytes(value);
                    gzip.Write(input, 0, input.Length);
                }
                compressed = output.ToArray();
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(compressed),
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            response.Content.Headers.ContentEncoding.Add("gzip");
            return response;
        }

        private sealed class StaticResponseHandler : HttpMessageHandler
        {
            private readonly Func<HttpResponseMessage> _response;

            internal StaticResponseHandler(Func<HttpResponseMessage> response) => _response = response;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken) => Task.FromResult(_response());
        }

        private sealed class RoutedResponseHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;

            internal RoutedResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> response) =>
                _response = response;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken) => Task.FromResult(_response(request));
        }

        private sealed class NeverCompletingHandler : HttpMessageHandler
        {
            internal int RequestCount;

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref RequestCount);
                await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
                throw new InvalidOperationException("The timeout token was not observed.");
            }
        }
    }
}
