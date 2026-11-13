#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YTray.Models;

namespace YTray.Core
{
    internal sealed class McpServer : IDisposable
    {
        internal const int Port = 19222;
        internal const string Endpoint = "http://127.0.0.1:19222/mcp";
        private const int MaxHeaderBytes = 64 * 1024;
        private const int MaxBodyBytes = 1024 * 1024;
        private const string ModernVersion = "2026-07-28";
        private static readonly string[] LegacyVersions = { "2025-11-25", "2025-06-18", "2025-03-26" };
        private readonly InstanceStore _store;
        private TcpListener? _listener;
        private bool _disposed;

        private static readonly JArray Tools = JArray.Parse(@"[
          {
            'name':'list_browser_runtimes',
            'description':'List browser runtimes available to YTray. Use a runtime id with open_browser_instance.',
            'inputSchema':{'type':'object','additionalProperties':false},
            'annotations':{'readOnlyHint':true,'idempotentHint':true}
          },
          {
            'name':'list_browser_instances',
            'description':'List YTray browser instances. Running instances are returned by default.',
            'inputSchema':{
              'type':'object','additionalProperties':false,
              'properties':{'include_history':{'type':'boolean','default':false,'description':'Include stopped historical instances.'}}
            },
            'annotations':{'readOnlyHint':true,'idempotentHint':true}
          },
          {
            'name':'open_browser_instance',
            'description':'Open a new browser instance, or restore a stopped history instance by id.',
            'inputSchema':{
              'type':'object','additionalProperties':false,
              'properties':{
                'instance_id':{'type':'string','description':'Optional stopped history instance id to restore.'},
                'url':{'type':'string','description':'Optional http, https, chrome, or about URL to open.'},
                'runtime_id':{'type':'string','description':'Optional runtime id from list_browser_runtimes.'},
                'use_proxy':{'type':'boolean','default':false,'description':'Use the proxy preset currently configured in YTray.'}
              }
            },
            'annotations':{'readOnlyHint':false,'destructiveHint':false,'idempotentHint':false}
          },
          {
            'name':'focus_browser_instance',
            'description':'Bring a running YTray browser instance to the foreground.',
            'inputSchema':{
              'type':'object','additionalProperties':false,
              'properties':{'instance_id':{'type':'string'}},'required':['instance_id']
            },
            'annotations':{'readOnlyHint':false,'destructiveHint':false,'idempotentHint':true}
          },
          {
            'name':'stop_browser_instance',
            'description':'Stop a running YTray browser instance while preserving it in history.',
            'inputSchema':{
              'type':'object','additionalProperties':false,
              'properties':{'instance_id':{'type':'string'}},'required':['instance_id']
            },
            'annotations':{'readOnlyHint':false,'destructiveHint':true,'idempotentHint':true}
          }
        ]");

        internal static IReadOnlyList<string> ToolNames =>
            Tools.Select(tool => (string?)tool["name"] ?? "").ToList();

        internal McpServer(InstanceStore store) => _store = store;

        internal bool Start()
        {
            if (_listener != null) return true;
            if (_disposed)
            {
                _store.SetMcpStatus(false, "MCP 服务已经关闭，请重启 YTray");
                return false;
            }
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, Port);
                _listener = listener;
                listener.Start();
                _ = Task.Run(() => AcceptLoopAsync(listener));
                _store.SetMcpStatus(true, $"正在监听 {Endpoint}");
                DiagnosticLog.Info("mcp.start", $"listening on {Endpoint}");
                return true;
            }
            catch (Exception ex)
            {
                try { _listener?.Stop(); } catch { }
                _listener = null;
                _store.SetMcpStatus(false, "启动失败：" + ex.Message);
                DiagnosticLog.Error("mcp.start", ex);
                return false;
            }
        }

        private async Task AcceptLoopAsync(TcpListener listener)
        {
            while (!_disposed && ReferenceEquals(_listener, listener))
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync();
                    _ = Task.Run(() => HandleClientAsync(client));
                }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) when (_disposed || !ReferenceEquals(_listener, listener)) { return; }
                catch (Exception ex) { DiagnosticLog.Error("mcp.accept", ex); }
            }
        }

        internal void Stop()
        {
            var listener = _listener;
            _listener = null;
            try { listener?.Stop(); } catch { }
            _store.SetMcpStatus(false, "MCP 服务已停止，可随时重新启动");
            DiagnosticLog.Info("mcp.stop", "listener stopped");
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                try
                {
                    var request = await ReadRequestAsync(stream);
                    if (request == null)
                    {
                        await WriteResponseAsync(stream, 400, JsonError(null, -32700, "Invalid HTTP request"));
                        return;
                    }
                    var response = HandleRequest(request);
                    await WriteResponseAsync(stream, response.Status, response.Body);
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Error("mcp.request", ex);
                    try { await WriteResponseAsync(stream, 500, JsonError(null, -32603, "Internal error")); }
                    catch { }
                }
            }
        }

        private Response HandleRequest(HttpRequest request)
        {
            if (!string.Equals(request.Method, "POST", StringComparison.Ordinal)
                || !string.Equals(request.Path, "/mcp", StringComparison.Ordinal))
                return new Response(404, JsonError(null, -32601, "MCP endpoint not found"));
            if (!AllowedHost(request.Header("Host")) || !AllowedOrigin(request.Header("Origin")))
                return new Response(403, JsonError(null, -32600, "Forbidden host or origin"));
            if (!(request.Header("Content-Type") ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                return new Response(415, JsonError(null, -32600, "Content-Type must be application/json"));

            JObject message;
            try { message = JObject.Parse(request.Body); }
            catch { return new Response(400, JsonError(null, -32700, "Parse error")); }
            var id = message["id"]?.DeepClone();
            var method = (string?)message["method"];
            if ((string?)message["jsonrpc"] != "2.0" || string.IsNullOrEmpty(method))
                return new Response(400, JsonError(id, -32600, "Invalid Request"));

            var modern = IsModern(message, request);
            if (modern)
            {
                var error = ValidateModernHeaders(message, request, method!);
                if (error != null) return new Response(400, JsonError(id, error.Value.Code, error.Value.Message, error.Value.Data));
            }

            if (id == null)
                return new Response(202, null);

            try
            {
                switch (method)
                {
                    case "initialize":
                        return new Response(200, JsonResult(id, Initialize(message), false));
                    case "server/discover":
                        return new Response(200, JsonResult(id, Discover(), true));
                    case "ping":
                        return new Response(200, JsonResult(id, new JObject(), modern));
                    case "tools/list":
                        return new Response(200, JsonResult(id, ToolList(modern), modern));
                    case "tools/call":
                        return new Response(200, JsonResult(id, CallTool(message), modern));
                    default:
                        return new Response(modern ? 404 : 200, JsonError(id, -32601, "Method not found"));
                }
            }
            catch (ArgumentException ex)
            {
                return new Response(200, JsonError(id, -32602, ex.Message));
            }
            catch (Exception ex)
            {
                DiagnosticLog.Error("mcp.call", ex);
                return new Response(200, JsonError(id, -32603, "Internal error"));
            }
        }

        private JObject Initialize(JObject message)
        {
            var requested = (string?)message["params"]?["protocolVersion"];
            var selected = requested != null && LegacyVersions.Contains(requested) ? requested : LegacyVersions[0];
            return new JObject
            {
                ["protocolVersion"] = selected,
                ["capabilities"] = new JObject { ["tools"] = new JObject { ["listChanged"] = false } },
                ["serverInfo"] = ServerInfo(),
                ["instructions"] = "Use YTray tools to open and manage local isolated browser instances."
            };
        }

        private static JObject Discover() => new JObject
        {
            ["resultType"] = "complete",
            ["supportedVersions"] = new JArray(new[] { ModernVersion }.Concat(LegacyVersions)),
            ["capabilities"] = new JObject { ["tools"] = new JObject { ["listChanged"] = false } },
            ["_meta"] = ServerMeta(),
            ["instructions"] = "Use YTray tools to open and manage local isolated browser instances.",
            ["ttlMs"] = 3600000,
            ["cacheScope"] = "public"
        };

        private static JObject ToolList(bool modern)
        {
            var result = new JObject { ["tools"] = Tools.DeepClone() };
            if (modern)
            {
                result["resultType"] = "complete";
                result["ttlMs"] = 3600000;
                result["cacheScope"] = "public";
            }
            return result;
        }

        private JObject CallTool(JObject message)
        {
            var parameters = message["params"] as JObject ?? throw new ArgumentException("params must be an object");
            var name = (string?)parameters["name"] ?? throw new ArgumentException("tool name is required");
            var argumentToken = parameters["arguments"];
            if (argumentToken != null && argumentToken.Type != JTokenType.Object)
                throw new ArgumentException("arguments must be an object");
            var arguments = argumentToken as JObject ?? new JObject();
            try
            {
                switch (name)
                {
                    case "list_browser_runtimes":
                        Only(arguments);
                        return ToolResult(OnUI(() => new JObject
                        {
                            ["runtimes"] = new JArray(_store.Runtimes.Select(runtime => RuntimeJson(runtime, _store.DefaultRuntime?.Id)))
                        }));
                    case "list_browser_instances":
                        Only(arguments, "include_history");
                        var includeHistory = OptionalBool(arguments, "include_history") ?? false;
                        return ToolResult(OnUI(() => new JObject
                        {
                            ["instances"] = new JArray(_store.Instances
                                .Where(instance => includeHistory || instance.Status == InstanceStatus.Running)
                                .Select(InstanceJson))
                        }));
                    case "open_browser_instance":
                        Only(arguments, "instance_id", "url", "runtime_id", "use_proxy");
                        return OpenBrowser(arguments);
                    case "focus_browser_instance":
                        Only(arguments, "instance_id");
                        return WithRunningInstance(arguments, instance =>
                        {
                            _store.Focus(instance);
                            return ToolResult(new JObject { ["instance_id"] = instance.Id.ToString(), ["status"] = "focused" });
                        });
                    case "stop_browser_instance":
                        Only(arguments, "instance_id");
                        return WithRunningInstance(arguments, instance =>
                        {
                            _store.Stop(instance);
                            return ToolResult(new JObject { ["instance_id"] = instance.Id.ToString(), ["status"] = "stopping" });
                        });
                    default:
                        return ToolError($"Unknown tool: {name}");
                }
            }
            catch (ArgumentException ex) { return ToolError(ex.Message); }
        }

        private JObject OpenBrowser(JObject arguments)
        {
            var instanceID = OptionalString(arguments, "instance_id");
            if (instanceID != null)
            {
                if (arguments.Properties().Any(property => property.Name != "instance_id"))
                    return ToolError("instance_id cannot be combined with new-instance options");
                if (!Guid.TryParse(instanceID, out var parsedInstanceID))
                    return ToolError("instance_id is not a valid id");
                return OnUI(() =>
                {
                    if (_store.IsLaunching) return ToolError("YTray is already starting a browser instance");
                    var instance = _store.Instances.FirstOrDefault(item =>
                        item.Id == parsedInstanceID && item.Status != InstanceStatus.Running);
                    if (instance == null) return ToolError("stopped browser instance was not found");
                    _store.RestoreHistory(instance);
                    if (!_store.IsLaunching)
                        return ToolError(_store.ErrorMessage ?? "YTray could not restore the browser instance");
                    var restored = _store.Instances.FirstOrDefault(item => item.Id == parsedInstanceID) ?? instance;
                    var result = InstanceJson(restored);
                    result["launch_status"] = "starting";
                    return ToolResult(result);
                });
            }

            var url = OptionalString(arguments, "url")?.Trim();
            if (!string.IsNullOrEmpty(url) && !IsAllowedStartUrl(url!))
                return ToolError("url must use http, https, chrome, or about");
            var useProxy = OptionalBool(arguments, "use_proxy") ?? false;
            var runtimeID = OptionalString(arguments, "runtime_id");
            Guid? parsedRuntimeID = null;
            if (runtimeID != null)
            {
                if (!Guid.TryParse(runtimeID, out var value)) return ToolError("runtime_id is not a valid id");
                parsedRuntimeID = value;
            }

            return OnUI(() =>
            {
                if (_store.IsLaunching) return ToolError("YTray is already starting a browser instance");
                var configuration = _store.QuickLaunchConfiguration(useProxy);
                if (configuration == null) return ToolError(_store.ErrorMessage ?? "The configured proxy is invalid");
                if (parsedRuntimeID.HasValue)
                {
                    if (!_store.Runtimes.Any(runtime => runtime.Id == parsedRuntimeID.Value))
                        return ToolError("runtime_id was not found");
                    configuration.DefaultRuntimeID = parsedRuntimeID.Value;
                }
                if (!string.IsNullOrEmpty(url)) configuration.HomeURL = url!;
                var before = new HashSet<Guid>(_store.Instances.Select(instance => instance.Id));
                if (!_store.Launch(LaunchMode.Quick, configuration))
                    return ToolError(_store.ErrorMessage ?? "YTray could not start the browser instance");
                var opened = _store.Instances.FirstOrDefault(instance => !before.Contains(instance.Id));
                if (opened == null) return ToolError("YTray did not create a browser instance");
                var result = InstanceJson(opened);
                result["launch_status"] = "starting";
                return ToolResult(result);
            });
        }

        private JObject WithRunningInstance(JObject arguments, Func<BrowserInstance, JObject> action)
        {
            var raw = RequiredString(arguments, "instance_id");
            if (!Guid.TryParse(raw, out var id)) return ToolError("instance_id is not a valid id");
            return OnUI(() =>
            {
                var instance = _store.Instances.FirstOrDefault(item => item.Id == id && item.Status == InstanceStatus.Running);
                return instance == null ? ToolError("running browser instance was not found") : action(instance);
            });
        }

        private T OnUI<T>(Func<T> action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            return dispatcher == null || dispatcher.CheckAccess() ? action() : dispatcher.Invoke(action);
        }

        internal static JObject RuntimeJson(BrowserRuntime runtime, Guid? defaultRuntimeId) => new JObject
        {
            ["id"] = runtime.Id.ToString(),
            ["name"] = runtime.DisplayTitle,
            ["version"] = runtime.VersionLabel,
            ["kind"] = runtime.Kind.ToString(),
            ["source"] = runtime.Source.ToString(),
            ["default"] = runtime.Id == defaultRuntimeId
        };

        private static JObject InstanceJson(BrowserInstance instance) => new JObject
        {
            ["id"] = instance.Id.ToString(),
            ["name"] = instance.Name,
            ["runtime"] = instance.RuntimeName,
            ["runtime_id"] = instance.RuntimeID.ToString(),
            ["status"] = instance.Status.ToString().ToLowerInvariant(),
            ["pid"] = instance.ProcessID,
            ["debug_port"] = instance.DebugPort,
            ["start_url"] = instance.StartURL,
            ["page_title"] = instance.LastPageTitle,
            ["page_url"] = instance.LastPageURL,
            ["dock_badge"] = instance.DockBadge,
            ["started_at"] = instance.StartedAt.ToUniversalTime().ToString("O")
        };

        internal static bool IsAllowedStartUrl(string value)
        {
            if (value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                return !string.IsNullOrEmpty(uri.Host);
            return uri.Scheme.Equals("chrome", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase);
        }

        private static JObject ToolResult(JObject value) => new JObject
        {
            ["resultType"] = "complete",
            ["content"] = new JArray(new JObject
            {
                ["type"] = "text",
                ["text"] = value.ToString(Formatting.None)
            }),
            ["structuredContent"] = value,
            ["isError"] = false
        };

        private static JObject ToolError(string message) => new JObject
        {
            ["resultType"] = "complete",
            ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = message }),
            ["isError"] = true
        };

        private static void Only(JObject arguments, params string[] allowed)
        {
            var names = new HashSet<string>(allowed, StringComparer.Ordinal);
            var unexpected = arguments.Properties().FirstOrDefault(property => !names.Contains(property.Name));
            if (unexpected != null) throw new ArgumentException($"unexpected argument: {unexpected.Name}");
        }

        private static string RequiredString(JObject arguments, string name) =>
            OptionalString(arguments, name) ?? throw new ArgumentException($"{name} is required");

        private static string? OptionalString(JObject arguments, string name)
        {
            var token = arguments[name];
            if (token == null) return null;
            if (token.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)token))
                throw new ArgumentException($"{name} must be a non-empty string");
            return token.ToObject<string>()!;
        }

        private static bool? OptionalBool(JObject arguments, string name)
        {
            var token = arguments[name];
            if (token == null) return null;
            if (token.Type != JTokenType.Boolean) throw new ArgumentException($"{name} must be a boolean");
            return (bool)token;
        }

        private static bool IsModern(JObject message, HttpRequest request)
        {
            var bodyVersion = (string?)message["params"]?["_meta"]?["io.modelcontextprotocol/protocolVersion"];
            var headerVersion = request.Header("MCP-Protocol-Version");
            return bodyVersion != null || (headerVersion != null && !LegacyVersions.Contains(headerVersion));
        }

        private static (int Code, string Message, JToken? Data)? ValidateModernHeaders(
            JObject message, HttpRequest request, string method)
        {
            var bodyVersion = (string?)message["params"]?["_meta"]?["io.modelcontextprotocol/protocolVersion"];
            var capabilities = message["params"]?["_meta"]?["io.modelcontextprotocol/clientCapabilities"];
            var headerVersion = request.Header("MCP-Protocol-Version");
            if (headerVersion != bodyVersion)
                return (-32020, "MCP request headers do not match the body", null);
            if (bodyVersion != ModernVersion)
                return (-32022, "Unsupported protocol version", new JObject
                {
                    ["supported"] = new JArray(new[] { ModernVersion }.Concat(LegacyVersions)),
                    ["requested"] = bodyVersion ?? headerVersion ?? "missing",
                });
            if (capabilities?.Type != JTokenType.Object)
                return (-32602, "Modern MCP requests require clientCapabilities metadata", null);
            if (request.Header("Mcp-Method") != method)
                return (-32020, "Mcp-Method header does not match the body", null);
            if (method == "tools/call")
            {
                var bodyName = (string?)message["params"]?["name"];
                if (bodyName == null || DecodeHeader(request.Header("Mcp-Name")) != bodyName)
                    return (-32020, "Mcp-Name header does not match the tool name", null);
            }
            return null;
        }

        private static string? DecodeHeader(string? value)
        {
            if (value == null || !value.StartsWith("=?base64?", StringComparison.Ordinal)
                || !value.EndsWith("?=", StringComparison.Ordinal)) return value;
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(value.Substring(9, value.Length - 11))); }
            catch { return null; }
        }

        private static bool AllowedHost(string? value) =>
            string.Equals(value, $"127.0.0.1:{Port}", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, $"localhost:{Port}", StringComparison.OrdinalIgnoreCase);

        private static bool AllowedOrigin(string? value) => value == null
            || string.Equals(value, $"http://127.0.0.1:{Port}", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, $"http://localhost:{Port}", StringComparison.OrdinalIgnoreCase);

        private static JObject JsonResult(JToken id, JObject result, bool modern)
        {
            if (modern && result["resultType"] == null) result["resultType"] = "complete";
            if (modern && result["_meta"] == null) result["_meta"] = ServerMeta();
            return new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }

        private static JObject JsonError(JToken? id, int code, string message, JToken? data = null)
        {
            var error = new JObject { ["code"] = code, ["message"] = message };
            if (data != null) error["data"] = data;
            return new JObject { ["jsonrpc"] = "2.0", ["id"] = id ?? JValue.CreateNull(), ["error"] = error };
        }

        private static JObject ServerInfo() => new JObject { ["name"] = "ytray", ["version"] = YTrayBuildInfo.Version };
        private static JObject ServerMeta() => new JObject { ["io.modelcontextprotocol/serverInfo"] = ServerInfo() };

        private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream)
        {
            var bytes = new MemoryStream();
            var buffer = new byte[4096];
            var headerEnd = -1;
            while (headerEnd < 0)
            {
                var read = await stream.ReadAsync(buffer, 0, buffer.Length);
                if (read <= 0) return null;
                bytes.Write(buffer, 0, read);
                var snapshot = bytes.ToArray();
                headerEnd = HeaderEnd(snapshot);
                if (headerEnd < 0 && snapshot.Length > MaxHeaderBytes) return null;
            }

            var data = bytes.ToArray();
            var headerText = Encoding.ASCII.GetString(data, 0, headerEnd);
            var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var requestLine = lines[0].Split(' ');
            if (requestLine.Length != 3) return null;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) return null;
                headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }
            if (!headers.TryGetValue("Content-Length", out var rawLength)
                || !int.TryParse(rawLength, out var contentLength)
                || contentLength < 0 || contentLength > MaxBodyBytes) return null;
            var bodyOffset = headerEnd + 4;
            while (data.Length - bodyOffset < contentLength)
            {
                var read = await stream.ReadAsync(buffer, 0, Math.Min(buffer.Length, contentLength - (data.Length - bodyOffset)));
                if (read <= 0) return null;
                bytes.Write(buffer, 0, read);
                data = bytes.ToArray();
            }
            return new HttpRequest(requestLine[0], requestLine[1], headers,
                Encoding.UTF8.GetString(data, bodyOffset, contentLength));
        }

        private static int HeaderEnd(byte[] data)
        {
            for (var i = 0; i + 3 < data.Length; i++)
                if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10) return i;
            return -1;
        }

        private static async Task WriteResponseAsync(NetworkStream stream, int status, JObject? body)
        {
            var payload = body == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body.ToString(Formatting.None));
            var reason = status == 200 ? "OK" : status == 202 ? "Accepted" : status == 400 ? "Bad Request"
                : status == 403 ? "Forbidden" : status == 404 ? "Not Found"
                : status == 415 ? "Unsupported Media Type" : "Internal Server Error";
            var head = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json; charset=utf-8\r\n" +
                $"Content-Length: {payload.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
            await stream.WriteAsync(head, 0, head.Length);
            if (payload.Length > 0) await stream.WriteAsync(payload, 0, payload.Length);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }

        private sealed class HttpRequest
        {
            internal string Method { get; }
            internal string Path { get; }
            internal IReadOnlyDictionary<string, string> Headers { get; }
            internal string Body { get; }
            internal HttpRequest(string method, string path, IReadOnlyDictionary<string, string> headers, string body)
            { Method = method; Path = path; Headers = headers; Body = body; }
            internal string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
        }

        private readonly struct Response
        {
            internal int Status { get; }
            internal JObject? Body { get; }
            internal Response(int status, JObject? body) { Status = status; Body = body; }
        }
    }
}
