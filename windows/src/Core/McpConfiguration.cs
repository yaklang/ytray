#nullable enable
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace YTray.Core
{
    // Both entries use the same import format, so copying both never overwrites one service.
    internal static class McpConfiguration
    {
        internal static string YTray => new JObject {
            ["mcpServers"] = new JObject {
                ["ytray"] = new JObject { ["type"] = "http", ["url"] = McpServer.Endpoint }
            }
        }.ToString(Formatting.Indented);

        internal static string? Both(string? computerConfiguration)
        {
            if (computerConfiguration == null) return null;
            var result = JObject.Parse(YTray);
            var services = (JObject)result["mcpServers"]!;
            var computer = JObject.Parse(computerConfiguration)["mcpServers"]?["ytray-system"];
            if (computer == null) return null;
            services["ytray-system"] = computer.DeepClone();
            return result.ToString(Formatting.Indented);
        }
    }
}
