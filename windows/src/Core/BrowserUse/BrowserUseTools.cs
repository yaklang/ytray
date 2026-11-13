#nullable enable
using System;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace YTray.Core.BrowserUse
{
    internal static class BrowserUseTools
    {
        private const string Prefix = "browser_";
        private const string PageIdPattern = @"p(?:[0-9a-f]{8}-)?[1-9][0-9]{0,18}";
        private static readonly Regex PageId = new Regex("^" + PageIdPattern + "$", RegexOptions.CultureInvariant);
        private static readonly Regex CaptureId = new Regex("^" + PageIdPattern + @"c[1-9][0-9]{0,18}$", RegexOptions.CultureInvariant);
        private static readonly Regex NodeId = new Regex(@"^n[0-9a-z]{1,12}$", RegexOptions.CultureInvariant);
        private static readonly JObject Page = new JObject { ["page_id"] = String("Page ID from browser_list_pages.") };
        private static readonly JObject Open = new JObject
        {
            ["url"] = String("HTTP or HTTPS URL to open."),
            ["instance_id"] = String("Running YTray browser instance ID from browser_list_instances."),
            ["runtime_id"] = String("Browser runtime ID from browser_list_runtimes. Starts a new profile."),
            ["use_proxy"] = new JObject { ["type"] = "boolean", ["description"] = "Use the YTray preset proxy when starting a new instance. Defaults to false." },
        };
        private static readonly JObject Node = new JObject
        {
            ["page_id"] = Page["page_id"]!.DeepClone(),
            ["capture_id"] = String("Capture ID from the latest browser_observe on this page."),
            ["node_id"] = String("Node ID from the same observation."),
        };
        private static readonly JObject Type = new JObject(Node)
        {
            ["text"] = new JObject { ["type"] = "string", ["maxLength"] = 8_000, ["description"] = "Replacement text." },
        };
        private static readonly JObject Scroll = new JObject(Node)
        {
            ["delta_y"] = new JObject
            {
                ["type"] = "integer", ["minimum"] = -5_000, ["maximum"] = 5_000,
                ["description"] = "Vertical pixels. Omit when scrolling to a node.",
            },
        };

        private static readonly JArray ScrollModes = new JArray(
            new JObject { ["required"] = new JArray("page_id", "delta_y"), ["not"] = new JObject { ["anyOf"] = new JArray(Requires("capture_id"), Requires("node_id")) } },
            new JObject { ["required"] = new JArray("page_id", "capture_id", "node_id"), ["not"] = Requires("delta_y") });

        internal static readonly JArray Definitions = new JArray(
            Define("list_runtimes", "List browsers installed or configured in YTray. Use a runtime ID to start a new browser instance.", new JObject(), true),
            Define("list_instances", "List running YTray instances, their browser runtimes, A/B badges, and whether their extensions are ready.", new JObject(), true),
            DefineOpen(),
            Define("list_pages", "List available tabs from YTray-managed instances and tabs shared manually. Use the instance label, title, or URL to match the user's target.", new JObject { ["instance_id"] = Open["instance_id"]!.DeepClone() }, true),
            Define("observe", "Read the selected tab's current title, text, and interactive nodes. Returns a fresh capture_id. Observe the same page_id after same-origin navigation.", Page, true, "page_id"),
            Define("focus", "Show the selected tab in its own browser window. Use when the user wants to see the page or its operation feedback.", Page, false, "page_id"),
            Define("click", "Click a node from the latest observation. Observe the page again to verify the result.", Node, false, "page_id", "capture_id", "node_id"),
            Define("type", "Replace the text of an editable node and dispatch input and change events.", Type, false, "page_id", "capture_id", "node_id", "text"),
            DefineScroll());

        private static JObject String(string description) => new JObject { ["type"] = "string", ["description"] = description };
        private static JObject Requires(string name) => new JObject { ["required"] = new JArray(name) };

        private static JObject Define(string name, string description, JObject properties, bool readOnly, params string[] required) => new JObject
        {
            ["name"] = Prefix + name,
            ["description"] = description,
            ["inputSchema"] = new JObject
            {
                ["type"] = "object", ["properties"] = properties.DeepClone(), ["additionalProperties"] = false,
                ["required"] = new JArray(required),
            },
            ["annotations"] = new JObject { ["readOnlyHint"] = readOnly, ["openWorldHint"] = true },
        };

        private static JObject DefineScroll()
        {
            var tool = Define("scroll", "Scroll a shared page by delta_y pixels or scroll to a node from its latest observation.", Scroll, false, "page_id");
            tool["inputSchema"]!["oneOf"] = ScrollModes.DeepClone();
            return tool;
        }

        private static JObject DefineOpen()
        {
            var tool = Define("open", "Open a URL in a new tab of the specified running instance, or start a new instance of the specified runtime. Wait for the extension and return the exact page_id to observe. Use only for an explicit request to open a page or start a browser.", Open, false, "url");
            tool["inputSchema"]!["oneOf"] = new JArray(
                new JObject { ["required"] = new JArray("instance_id"), ["not"] = new JObject { ["anyOf"] = new JArray(Requires("runtime_id"), Requires("use_proxy")) } },
                new JObject { ["required"] = new JArray("runtime_id"), ["not"] = Requires("instance_id") });
            return tool;
        }

        internal static bool TryPageUrl(string? value, out Uri uri)
        {
            if (value?.Length <= 8_192 && !value.Any(char.IsControl) && Uri.TryCreate(value, UriKind.Absolute, out var parsed)
                && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
                && parsed.Host.Length > 0 && parsed.UserInfo.Length == 0)
            {
                uri = parsed;
                return true;
            }
            uri = null!;
            return false;
        }

        internal static string Origin(Uri uri) => new UriBuilder(uri.Scheme, uri.IdnHost, uri.Port).Uri.GetLeftPart(UriPartial.Authority);

        private static void ValidateId(JObject arguments, string field)
        {
            if (arguments[field] is JToken value && (value.Type != JTokenType.String || !Guid.TryParseExact((string?)value, "D", out _)))
                throw new BrowserUseException("invalid_arguments", field + " must be an ID from the corresponding list tool.");
        }

        internal static (string Method, JObject Arguments) Parse(JObject parameters)
        {
            var name = parameters["name"]?.Type == JTokenType.String ? (string?)parameters["name"] : null;
            var tool = Definitions.FirstOrDefault(definition => (string?)definition["name"] == name);
            if (tool == null || name == null) throw new BrowserUseException("unknown_tool", "Unknown Browser Use tool.");
            if (parameters["arguments"] != null && !(parameters["arguments"] is JObject))
                throw new BrowserUseException("invalid_arguments", "arguments must be an object.");
            var arguments = parameters["arguments"] as JObject ?? new JObject();
            var schema = tool["inputSchema"]!;
            if (arguments.Properties().Any(property => schema["properties"]?[property.Name] == null)
                || schema["required"]!.Values<string>().Any(field => arguments[field!] == null))
                throw new BrowserUseException("invalid_arguments", "Check the tool's required and accepted fields.");
            ValidateId(arguments, "instance_id");
            if (name == Prefix + "list_pages" || name == Prefix + "list_instances" || name == Prefix + "list_runtimes")
                return (name.Substring(Prefix.Length), arguments);
            if (name == Prefix + "open")
            {
                ValidateId(arguments, "runtime_id");
                if ((arguments["instance_id"] == null) == (arguments["runtime_id"] == null)
                    || (arguments["use_proxy"] is JToken proxy && (arguments["runtime_id"] == null || proxy.Type != JTokenType.Boolean)))
                    throw new BrowserUseException("invalid_arguments", "Provide exactly one instance_id or runtime_id. use_proxy is only available with runtime_id.");
                if (arguments["url"]?.Type != JTokenType.String || !TryPageUrl((string?)arguments["url"], out _))
                    throw new BrowserUseException("invalid_arguments", "Use an absolute HTTP(S) URL without credentials.");
                return ("open", arguments);
            }
            if (arguments["page_id"]?.Type != JTokenType.String || !PageId.IsMatch((string)arguments["page_id"]!))
                throw new BrowserUseException("invalid_arguments", "page_id must be copied from browser_list_pages.");
            if (name == Prefix + "observe" || name == Prefix + "focus") return (name.Substring(Prefix.Length), arguments);
            if (name == Prefix + "scroll" && arguments["delta_y"] is JToken delta)
            {
                if (arguments["capture_id"] != null || arguments["node_id"] != null
                    || delta.Type != JTokenType.Integer || !int.TryParse(delta.ToString(Formatting.None), out var pixels)
                    || pixels < -5_000 || pixels > 5_000 || pixels == 0)
                    throw new BrowserUseException("invalid_arguments", "delta_y must be a nonzero integer between -5000 and 5000. Omit capture_id and node_id for pixel scrolling.");
                return ("scroll", arguments);
            }
            if (arguments["capture_id"]?.Type != JTokenType.String || !CaptureId.IsMatch((string)arguments["capture_id"]!))
                throw new BrowserUseException("invalid_arguments", "capture_id must be copied from browser_observe.");
            if (arguments["node_id"]?.Type != JTokenType.String || !NodeId.IsMatch((string)arguments["node_id"]!))
                throw new BrowserUseException("invalid_arguments", "node_id must be from browser_observe.");
            if (name == Prefix + "type" && (arguments["text"]?.Type != JTokenType.String || ((string)arguments["text"]!).Length > 8_000))
                throw new BrowserUseException("invalid_arguments", "text must be a string with at most 8000 characters.");
            return (name.Substring(Prefix.Length), arguments);
        }

        internal static JObject Result(JObject value, bool failed = false)
        {
            var text = failed ? (string?)value["code"] + ": " + (string?)value["message"] : value.ToString(Formatting.None);
            return new JObject
            {
                ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = text }),
                ["structuredContent"] = value,
                ["isError"] = failed,
            };
        }
    }
}
