using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using YTray.Core;
using YTray.Models;

namespace YTray.Tests
{
    [TestClass]
    public class ExtensionInstallerTests
    {
        private const string SampleManifest = @"
{
  ""latest"": ""0.2.2"",
  ""updated_at"": ""2026-08-18T07:04:53.588Z"",
  ""versions"": [
    {
      ""version"": ""0.2.2"",
      ""published_at"": ""2026-08-18T07:00:17.374Z"",
      ""commit"": ""b00073480050437d1ed16784b9721f5a6323baf3"",
      ""artifacts"": [
        {
          ""variant"": ""chrome-store"",
          ""browser"": ""chrome"",
          ""mode"": ""store"",
          ""filename"": ""chrome-store-0.2.2.zip"",
          ""url"": ""https://aliyun-oss.yaklang.com/chrome-extension/0.2.2/chrome-store-0.2.2.zip"",
          ""sha256"": ""44cb7d231413420018a83c97432fc306525dac906becdc7b75c923bdd59d0f4b"",
          ""size"": 528697,
          ""checksum_url"": ""https://aliyun-oss.yaklang.com/chrome-extension/0.2.2/chrome-store-0.2.2.zip.sha256.txt""
        },
        {
          ""variant"": ""chrome-enterprise"",
          ""browser"": ""chrome"",
          ""mode"": ""enterprise"",
          ""filename"": ""chrome-enterprise-0.2.2.zip"",
          ""url"": ""https://aliyun-oss.yaklang.com/chrome-extension/0.2.2/chrome-enterprise-0.2.2.zip"",
          ""sha256"": ""97955d404f6ccdfd440e96ed03a92c7f6af4a79427d3bf6f67ff0e6f9035139b"",
          ""size"": 531085,
          ""checksum_url"": ""https://aliyun-oss.yaklang.com/chrome-extension/0.2.2/chrome-enterprise-0.2.2.zip.sha256.txt""
        }
      ]
    }
  ]
}";

        [TestMethod]
        public void ExtensionManifestParsesRealShape()
        {
            var manifest = JsonConvert.DeserializeObject<ExtensionManifest>(SampleManifest);
            Assert.IsNotNull(manifest);
            Assert.AreEqual("0.2.2", manifest.Latest);
            Assert.AreEqual(1, manifest.Versions.Count);
            var version = manifest.Versions[0];
            Assert.AreEqual("0.2.2", version.Version);
            Assert.AreEqual("b00073480050437d1ed16784b9721f5a6323baf3", version.Commit);
            Assert.AreEqual(2, version.Artifacts.Count);
            var enterprise = ExtensionInstaller.EnterpriseArtifact(version);
            Assert.IsNotNull(enterprise);
            Assert.AreEqual("chrome-enterprise-0.2.2.zip", enterprise.Filename);
            Assert.AreEqual(531085L, enterprise.Size);
            Assert.IsTrue(enterprise.Url.EndsWith("chrome-enterprise-0.2.2.zip"));
            Assert.IsTrue(enterprise.ChecksumUrl.EndsWith(".sha256.txt"));
        }

        [TestMethod]
        public void ExtensionVersionComparisonIsNumericPerSegment()
        {
            Assert.AreEqual(0, ExtensionInstaller.CompareVersions("0.2.2", "0.2.2"));
            Assert.IsTrue(ExtensionInstaller.CompareVersions("0.2.10", "0.2.2") > 0);
            Assert.IsTrue(ExtensionInstaller.CompareVersions("0.2.2", "0.2.10") < 0);
            Assert.IsTrue(ExtensionInstaller.CompareVersions("1.0.0", "0.9.9") > 0);
            Assert.IsTrue(ExtensionInstaller.CompareVersions("v1.0", "1.0.0") < 0);
            Assert.IsTrue(ExtensionInstaller.CompareVersions("", null) == 0);
        }

        // Hits the live OSS mirror; guards the gzip/AutomaticDecompression contract that
        // plain .NET Framework HttpClient would otherwise break (compressed JSON body).
        [TestMethod]
        [Ignore] // network-dependent; run manually when touching the installer
        public async Task FetchLiveManifestDiagnostic()
        {
            var manifest = await ExtensionInstaller.FetchManifestAsync();
            Assert.IsFalse(string.IsNullOrEmpty(manifest.Latest));
            Assert.IsTrue(manifest.Versions.Count > 0);
            Assert.IsNotNull(manifest.Versions.Select(ExtensionInstaller.EnterpriseArtifact).FirstOrDefault(a => a != null));
        }
    }
}
