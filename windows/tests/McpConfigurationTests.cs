using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using YTray.Core;

namespace YTray.Tests
{
    [TestClass]
    public class McpConfigurationTests
    {
        [TestMethod]
        public void CombinedImportKeepsTwoDistinctServicesAndTheirOwnCredentials()
        {
            Assert.IsNull(McpConfiguration.Both(null));
            var computer = new JObject { ["mcpServers"] = new JObject { ["ytray-system"] = new JObject {
                ["type"] = "http", ["url"] = "http://127.0.0.1:51373/system/mcp",
                ["headers"] = new JObject { ["Authorization"] = "Bearer fixture-token" }
            } } }.ToString();
            var services = (JObject)JObject.Parse(McpConfiguration.Both(computer))["mcpServers"];
            CollectionAssert.AreEquivalent(new[] { "ytray", "ytray-system" }, services.Properties().Select(p => p.Name).ToArray());
            Assert.AreEqual(McpServer.Endpoint, (string)services["ytray"]["url"]);
            Assert.IsNull(services["ytray"]["headers"]);
            Assert.AreEqual("Bearer fixture-token", (string)services["ytray-system"]["headers"]["Authorization"]);
            Assert.AreNotEqual((string)services["ytray"]["url"], (string)services["ytray-system"]["url"]);
        }
    }
}
