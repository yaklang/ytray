#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YTray.Models;

namespace YTray.Core.SystemControl
{
    internal sealed class ControlGateway : IDisposable
    {
        internal const int Port = 51373;
        private readonly SystemControlController _controller;
        private readonly SemaphoreSlim _connections = new SemaphoreSlim(16);
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private TcpListener? _listener;
        private readonly object _rateLock = new object();
        private DateTime _rateWindow; private int _rateCount;
        private readonly ConcurrentDictionary<string, string> _activeRequests = new ConcurrentDictionary<string, string>();
        internal ControlGateway(SystemControlController controller) { _controller = controller; }
        internal void Start()
        {
            if (_listener != null) return;
            var listener = new TcpListener(IPAddress.Loopback, Port); listener.Start(16); _listener = listener;
            _ = AcceptAsync(listener);
        }
        private async Task AcceptAsync(TcpListener listener)
        {
            while (!_shutdown.IsCancellationRequested) {
                try {
                    var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    if (!_connections.Wait(0)) { client.Close(); continue; }
                    _ = HandleAsync(client);
                } catch when (_shutdown.IsCancellationRequested || _listener == null) { return; }
                catch { await Task.Delay(50).ConfigureAwait(false); }
            }
        }
        private async Task HandleAsync(TcpClient client)
        {
            using (client) using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token)) {
                deadline.CancelAfter(TimeSpan.FromSeconds(45));
                using var close = deadline.Token.Register(() => client.Close());
                try {
                    var stream = client.GetStream();
                    HttpRequest request;
                    using (var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token)) {
                        readDeadline.CancelAfter(TimeSpan.FromSeconds(10));
                        using var abort = readDeadline.Token.Register(() => client.Close());
                        request = await ReadAsync(stream, readDeadline.Token).ConfigureAwait(false);
                    }
                    var response = await HandleRequestAsync(request).ConfigureAwait(false);
                    await WriteAsync(stream, response, deadline.Token).ConfigureAwait(false);
                } catch { try { await WriteAsync(client.GetStream(), new HttpResponse(400, "Bad Request"), CancellationToken.None).ConfigureAwait(false); } catch { } }
                finally { _connections.Release(); }
            }
        }
        internal async Task<HttpResponse> HandleRequestAsync(HttpRequest request)
        {
            if (request.Path != "/system/mcp") return new HttpResponse(404, "Not Found");
            var host = request.Header("host");
            if (!string.Equals(host, "127.0.0.1:" + Port, StringComparison.OrdinalIgnoreCase) && !string.Equals(host, "localhost:" + Port, StringComparison.OrdinalIgnoreCase)) return new HttpResponse(403, "Invalid Host");
            var origin = request.Header("origin"); var cors = new Dictionary<string, string>();
            if (origin != null) {
                if (!_controller.AllowedOrigin(origin)) return new HttpResponse(403, "Origin not authorized");
                cors["Access-Control-Allow-Origin"] = origin; cors["Vary"] = "Origin";
                cors["Access-Control-Allow-Methods"] = "POST, OPTIONS";
                cors["Access-Control-Allow-Headers"] = "Authorization, Content-Type, MCP-Protocol-Version";
                cors["Access-Control-Max-Age"] = "60";
            }
            if (request.Method == "OPTIONS") return new HttpResponse(origin == null ? 403 : 200, "", cors);
            if (!_controller.Authorized(request.Header("authorization"), origin)) { cors["WWW-Authenticate"] = "Bearer realm=\"YTray\""; return new HttpResponse(401, "Authorization required", cors); }
            var generation = _controller.Generation;
            if (request.Method != "POST") return new HttpResponse(405, "Use POST", cors);
            lock (_rateLock) {
                if ((DateTime.UtcNow - _rateWindow).TotalSeconds >= 1) { _rateWindow = DateTime.UtcNow; _rateCount = 0; }
                if (++_rateCount > 20) return new HttpResponse(429, "Rate limit", cors);
            }
            if (request.Header("content-type")?.Split(';')[0].Trim().ToLowerInvariant() != "application/json") return new HttpResponse(415, "JSON required", cors);
            var version = request.Header("mcp-protocol-version");
            if (version != null && version != "2025-11-25" && version != "2025-06-18") return new HttpResponse(400, "Unsupported protocol version", cors);
            JObject rpc; try { using var json = new JsonTextReader(new StringReader(request.Body)) { DateParseHandling = DateParseHandling.None, MaxDepth = 64 }; rpc = JObject.Load(json); } catch { return new HttpResponse(400, "Invalid JSON-RPC", cors); }
            if ((string?)rpc["jsonrpc"] != "2.0" || rpc["method"]?.Type != JTokenType.String) return new HttpResponse(400, "Invalid JSON-RPC", cors);
            if (rpc["id"] == null) {
                if ((string?)rpc["method"] == "notifications/cancelled" && rpc["params"]?["requestId"] is JToken cancelledId && _activeRequests.TryGetValue(generation + ":" + cancelledId.ToString(Formatting.None), out var requestId)) {
                    try { await _controller.CallAsync("ytray_request_cancel", new JObject { ["request_id"] = requestId }, generation).ConfigureAwait(false); } catch (ControlException) { }
                }
                return new HttpResponse(202, "", cors);
            }
            if (rpc["id"]!.Type != JTokenType.Integer && rpc["id"]!.Type != JTokenType.String) return new HttpResponse(400, "Invalid JSON-RPC id", cors);
            JObject response;
            try {
                JObject result; var parameters = rpc["params"] as JObject ?? new JObject();
                if (generation != _controller.Generation) throw new ControlException("GrantRevoked", "会话已撤销");
                switch ((string?)rpc["method"]) {
                    case "initialize": result = new JObject { ["protocolVersion"] = (string?)parameters["protocolVersion"] == "2025-06-18" ? "2025-06-18" : "2025-11-25", ["capabilities"] = new JObject { ["tools"] = new JObject() },
                        ["serverInfo"] = new JObject { ["name"] = "YTray Windows Computer Use", ["version"] = "4" },
                        ["instructions"] = "Choose window_id with launch_app(app,url?) or list_windows. Actions return fresh compact controls, a short focus/text-selection summary and one screenshot by default; do not add a separate read after each action. Same-destination links retain one original actionable index, preferring a readable title. type_text(element_index?,text,replace?,submit?) focuses, replaces and submits in one call. include_screenshot:false saves image context. get_window_state(region:{x,y,width,height}) reads local controls/text in window-image pixels (default long edge 1400) without a screenshot by default; no prior capture is required. Use query for a control or detail:full for every alias. Focus/selection can be unavailable when the provider does not expose it. No client sessions or observation IDs are needed. Never repeat an input whose result is unknown." }; break;
                    case "ping": result = new JObject(); break;
                    case "tools/list": result = new JObject { ["tools"] = _controller.Tools() }; break;
                    case "tools/call":
                        if (parameters["name"]?.Type != JTokenType.String || parameters["arguments"] != null && !(parameters["arguments"] is JObject)) throw new ControlException("InvalidArguments", "工具参数无效");
                        var rpcKey = generation + ":" + rpc["id"]!.ToString(Formatting.None);
                        var actionRequest = (string?)parameters["arguments"]?["request_id"];
                        var tracked = actionRequest != null && CuaDriverSchemaAdapter.Actions.Contains((string)parameters["name"]!);
                        if (tracked && !_activeRequests.TryAdd(rpcKey, actionRequest!)) throw new ControlException("RequestConflict", "JSON-RPC id 已有进行中的请求");
                        try { result = await _controller.CallAsync((string)parameters["name"]!, (JObject?)parameters["arguments"] ?? new JObject(), generation).ConfigureAwait(false); }
                        catch (ControlException error) { result = error.Result(); }
                        finally { if (tracked) _activeRequests.TryRemove(rpcKey, out _); }
                        break;
                    default: return RpcError(rpc["id"], -32601, "Method not found", cors);
                }
                response = new JObject { ["jsonrpc"] = "2.0", ["id"] = rpc["id"]!.DeepClone(), ["result"] = result };
            } catch (ControlException error) { return RpcError(rpc["id"], -32602, error.Message, cors); }
            catch { return RpcError(rpc["id"], -32603, "Internal error", cors); }
            cors["Content-Type"] = "application/json";
            return new HttpResponse(200, response.ToString(Formatting.None), cors);
        }
        private static HttpResponse RpcError(JToken? id, int code, string message, Dictionary<string, string> headers)
        {
            headers["Content-Type"] = "application/json";
            return new HttpResponse(200, new JObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JObject { ["code"] = code, ["message"] = message } }.ToString(Formatting.None), headers);
        }
        internal static async Task<HttpRequest> ReadAsync(Stream stream, CancellationToken cancellation)
        {
            using var data = new MemoryStream(); var one = new byte[1]; var ending = 0;
            while (ending != 4) {
                if (data.Length >= 16384 || await stream.ReadAsync(one, 0, 1, cancellation).ConfigureAwait(false) != 1) throw new InvalidDataException();
                data.WriteByte(one[0]); var expected = ending == 0 || ending == 2 ? 13 : 10;
                ending = one[0] == expected ? ending + 1 : one[0] == 13 ? 1 : 0;
            }
            var text = new UTF8Encoding(false, true).GetString(data.ToArray()); var lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var requestLine = lines[0].Split(' ');
            if (requestLine.Length != 3 || requestLine[2] != "HTTP/1.1" || !requestLine[1].StartsWith("/", StringComparison.Ordinal) || requestLine[1].Contains(":") || requestLine[1].Contains("#")) throw new InvalidDataException();
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 1; i < lines.Length && lines[i].Length != 0; i++) {
                var colon = lines[i].IndexOf(':'); if (colon <= 0 || char.IsWhiteSpace(lines[i][0])) throw new InvalidDataException();
                var name = lines[i].Substring(0, colon); var value = lines[i].Substring(colon + 1).Trim();
                if (name.Trim() != name || headers.ContainsKey(name) || name.StartsWith("x-forwarded-", StringComparison.OrdinalIgnoreCase) || name.Equals("forwarded", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
                headers.Add(name, value);
            }
            if (headers.ContainsKey("transfer-encoding")) throw new InvalidDataException();
            var length = 0;
            if (headers.TryGetValue("content-length", out var contentLength)) {
                if (!int.TryParse(contentLength, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out length) || length > 1024 * 1024) throw new InvalidDataException();
            } else if (requestLine[0] == "POST") throw new InvalidDataException();
            var body = new byte[length]; var offset = 0;
            while (offset < length) { var count = await stream.ReadAsync(body, offset, length - offset, cancellation).ConfigureAwait(false); if (count == 0) throw new EndOfStreamException(); offset += count; }
            return new HttpRequest(requestLine[0], requestLine[1], headers, new UTF8Encoding(false, true).GetString(body));
        }
        private static async Task WriteAsync(Stream stream, HttpResponse response, CancellationToken cancellation)
        {
            var body = Encoding.UTF8.GetBytes(response.Body); var header = new StringBuilder("HTTP/1.1 " + response.Status + " " + (response.Status < 400 ? "OK" : "Error") + "\r\nConnection: close\r\nCache-Control: no-store\r\nContent-Length: " + body.Length + "\r\n");
            foreach (var pair in response.Headers) header.Append(pair.Key).Append(": ").Append(pair.Value).Append("\r\n");
            var bytes = Encoding.ASCII.GetBytes(header.Append("\r\n").ToString()); await stream.WriteAsync(bytes, 0, bytes.Length, cancellation).ConfigureAwait(false); await stream.WriteAsync(body, 0, body.Length, cancellation).ConfigureAwait(false);
        }
        public void Dispose() { _shutdown.Cancel(); _listener?.Stop(); _listener = null; }
        internal sealed class HttpRequest {
            internal string Method, Path, Body; internal Dictionary<string, string> Headers;
            internal HttpRequest(string method, string path, Dictionary<string, string> headers, string body) { Method = method; Path = path; Headers = headers; Body = body; }
            internal string? Header(string key) => Headers.TryGetValue(key, out var value) ? value : null;
        }
        internal sealed class HttpResponse {
            internal int Status; internal string Body; internal Dictionary<string, string> Headers;
            internal HttpResponse(int status, string body, Dictionary<string, string>? headers = null) { Status = status; Body = body; Headers = headers ?? new Dictionary<string, string>(); }
        }
    }
}
