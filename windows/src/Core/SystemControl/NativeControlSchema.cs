#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using YTray.Models;

namespace YTray.Core.SystemControl
{
    internal static class NativeControlSchema
    {
        internal static readonly string[] Actions = { "click", "type_text", "press_key", "scroll" };
        internal static JArray Catalog()
        {
            var result = new JArray();
            foreach (var definition in Fields) {
                var properties = new JObject();
                foreach (var field in definition.Value) {
                    var type = field == "modifiers" ? "array" : field.StartsWith("include_", StringComparison.Ordinal) ? "boolean"
                        : new[] { "pid", "window_id", "max_elements", "max_depth", "timeout_ms", "max_image_dimension", "count", "amount" }.Contains(field) ? "integer"
                        : field == "x" || field == "y" ? "number" : "string";
                    var property = new JObject { ["type"] = type };
                    if (type == "array") property["items"] = new JObject { ["type"] = "string" };
                    properties[field] = property;
                }
                var required = new JArray();
                if (definition.Key == "type_text") required.Add("text");
                if (definition.Key == "press_key") required.Add("key");
                if (definition.Key == "scroll") required.Add("direction");
                result.Add(new JObject { ["name"] = definition.Key, ["inputSchema"] = new JObject { ["type"] = "object", ["properties"] = properties, ["required"] = required } });
            }
            return result;
        }
        internal static readonly Dictionary<string, string[]> Fields = new Dictionary<string, string[]> {
            ["get_window_state"] = new[] { "pid", "window_id", "query", "include_accessibility_tree", "include_screenshot", "max_elements", "max_depth", "timeout_ms", "max_image_dimension", "tree_format" },
            ["get_desktop_state"] = new[] { "max_image_dimension" }, ["check_permissions"] = Array.Empty<string>(),
            ["click"] = new[] { "pid", "window_id", "element_token", "capture_id", "x", "y", "delivery_mode", "button", "count" },
            ["type_text"] = new[] { "pid", "window_id", "element_token", "x", "y", "delivery_mode", "text" },
            ["press_key"] = new[] { "pid", "window_id", "element_token", "delivery_mode", "key", "modifiers" },
            ["scroll"] = new[] { "pid", "window_id", "element_token", "x", "y", "delivery_mode", "direction", "by", "amount" }
        };
        internal static Dictionary<string, JObject> Review(JArray raw, ControlSessionOptions grant)
        {
            var tools = new Dictionary<string, JObject>();
            foreach (var tool in raw.OfType<JObject>()) {
                var name = (string?)tool["name"] ?? "";
                if (!Fields.TryGetValue(name, out var allowed) || grant.ReadOnly && Actions.Contains(name) || !grant.AllowDesktop && name == "get_desktop_state") continue;
                var original = tool["inputSchema"] as JObject ?? throw new ControlException("VersionIncompatible", "Driver 缺少参数定义");
                var properties = (JObject?)original["properties"] ?? new JObject();
                var required = ((JArray?)original["required"] ?? new JArray()).Values<string>().Where(k => k != "session").ToArray();
                if (required.Any(k => k == null || !allowed.Contains(k))) continue;
                if (Actions.Contains(name) && properties["delivery_mode"] == null) continue;
                var filtered = new JObject(); foreach (var key in allowed) if (properties[key] != null) filtered[key] = properties[key]!.DeepClone();
                if (filtered["max_elements"] is JObject elements) elements["maximum"] = 1000;
                if (filtered["max_depth"] is JObject depth) depth["maximum"] = 25;
                if (filtered["timeout_ms"] is JObject timeout) timeout["maximum"] = 5000;
                if (filtered["max_image_dimension"] is JObject dimension) { dimension["minimum"] = 100; dimension["maximum"] = 4096; }
                if (filtered["amount"] is JObject amount) { amount["minimum"] = 1; amount["maximum"] = 50; }
                filtered["target_ref"] = new JObject { ["type"] = "string" };
                filtered["observation_id"] = new JObject { ["type"] = "string" };
                filtered["session_id"] = new JObject { ["type"] = "string" };
                filtered["runtime_generation"] = new JObject { ["type"] = "string" };
                filtered["request_id"] = new JObject { ["type"] = "string" };
                filtered["deadline"] = new JObject { ["type"] = "string" };
                // PID/HWND are resolved server-side. Optional legacy fields must match a
                // previously registered exact target; they never select a fallback window.
                tools[name] = new JObject { ["name"] = name, ["description"] = "YTray 授权范围内：" + name + ". Observe again after each action; never automatically replay unknown outcomes.",
                    ["inputSchema"] = new JObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = filtered,
                        ["required"] = new JArray(required.Where(k => k != "pid" && k != "window_id").Concat(Actions.Contains(name)
                            ? new[] { "target_ref", "observation_id", "request_id", "deadline" } : name == "get_window_state" ? new[] { "target_ref" } : Array.Empty<string>()).Distinct()) },
                    ["annotations"] = new JObject { ["readOnlyHint"] = !Actions.Contains(name), ["destructiveHint"] = Actions.Contains(name) } };
            }
            if (!tools.ContainsKey("get_window_state")) throw new ControlException("VersionIncompatible", "Driver 不支持受审阅的窗口观察工具");
            return tools;
        }
    }
}
