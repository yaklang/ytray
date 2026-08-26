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
            Assert.AreEqual("0.1.2", YTrayBuildInfo.Version);
        }
    }
}
