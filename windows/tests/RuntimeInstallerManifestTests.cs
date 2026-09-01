#nullable enable
using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YTray.Core;
using YTray.Models;

namespace YTray.Tests
{
    [TestClass]
    public class RuntimeInstallerManifestTests
    {
        private const string ValidManifest =
            "{\"schema_version\":1,\"product\":\"chrome-for-testing\",\"versions\":[" +
            "{\"version\":\"152.0.1.2\",\"artifacts\":[]}]}";

        [TestMethod]
        public void RuntimeManifestClientAcceptsGzipAndDeflate()
        {
            using (var handler = RuntimeInstaller.CreateDefaultHandler())
                Assert.AreEqual(DecompressionMethods.GZip | DecompressionMethods.Deflate,
                    handler.AutomaticDecompression);
        }

        [TestMethod]
        public async Task GzipPayloadIsDecodedWhenAnIntermediaryLeavesItCompressed()
        {
            var compressed = Gzip(Encoding.UTF8.GetBytes(ValidManifest));
            using (var client = new HttpClient(new StubHandler(new ByteArrayContent(compressed))))
            {
                var versions = await RuntimeInstaller.FetchVersionsAsync(
                    client, new Uri("https://example.test/manifest.json"), CancellationToken.None);
                Assert.AreEqual(1, versions.Count);
                Assert.AreEqual("152.0.1.2", versions[0].Version);
            }
        }

        [TestMethod]
        public async Task HtmlGatewayResponseUsesActionableChineseError()
        {
            using (var client = new HttpClient(new StubHandler(
                       new StringContent("<html>proxy error</html>", Encoding.UTF8, "text/html"))))
            {
                var error = await Assert.ThrowsExceptionAsync<YTrayException>(() =>
                    RuntimeInstaller.FetchVersionsAsync(
                        client, new Uri("https://example.test/manifest.json"), CancellationToken.None));
                var message = ((Exception)error).Message;
                StringAssert.Contains(message, "无法识别");
                Assert.IsFalse(message.Contains("Unexpected character"));
            }
        }

        private static byte[] Gzip(byte[] bytes)
        {
            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, CompressionMode.Compress, true))
                    gzip.Write(bytes, 0, bytes.Length);
                return output.ToArray();
            }
        }

        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly HttpContent _content;
            internal StubHandler(HttpContent content) { _content = content; }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = _content });
            }
        }
    }
}
