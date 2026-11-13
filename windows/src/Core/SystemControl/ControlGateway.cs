#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
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
        private const int ToolCallLimit = 16;
        private const int ConnectionLimit = ToolCallLimit + 2; // Allow cancellation while tools occupy all their slots.
        private readonly SystemControlController _controller;
        private readonly SemaphoreSlim _connections = new SemaphoreSlim(ConnectionLimit);
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private TcpListener? _listener;
        private readonly object _rateLock = new object();
        private DateTime _rateWindow; private int _rateCount;
        private readonly Dictionary<(string Bearer, string Session, string Id), CancellationTokenSource> _activeRequests =
            new Dictionary<(string, string, string), CancellationTokenSource>();
        internal int BoundPort { get; private set; } = Port;
        internal ControlGateway(SystemControlController controller) { _controller = controller; }
        internal void Start(int port = Port)
        {
            if (_listener != null) return;
            var listener = new TcpListener(IPAddress.Loopback, port);
            try { listener.Start(ConnectionLimit); }
            catch (SocketException error) { DiagnosticLog.Error("control.mcp", error, "could not listen on " + _controller.Endpoint); throw; }
            _listener = listener;
            BoundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            DiagnosticLog.Info("control.mcp", "listening on http://127.0.0.1:" + BoundPort + "/system/mcp");
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
                    var response = await HandleRequestAsync(request, deadline.Token).ConfigureAwait(false);
                    await WriteAsync(stream, response, deadline.Token).ConfigureAwait(false);
                } catch { try { await WriteAsync(client.GetStream(), new HttpResponse(400, "Bad Request"), CancellationToken.None).ConfigureAwait(false); } catch { } }
                finally { _connections.Release(); }
            }
        }
        internal async Task<HttpResponse> HandleRequestAsync(HttpRequest request, CancellationToken deadline = default)
        {
            if (request.Path != "/system/mcp") return Reject(404, "Not Found");
            var host = request.Header("host");
            if (!string.Equals(host, "127.0.0.1:" + BoundPort, StringComparison.OrdinalIgnoreCase) && !string.Equals(host, "localhost:" + BoundPort, StringComparison.OrdinalIgnoreCase)) return Reject(403, "Invalid Host");
            var origin = request.Header("origin"); var cors = new Dictionary<string, string>();
            if (origin != null) {
                if (!_controller.AllowedOrigin(origin)) return Reject(403, "Origin not authorized");
                cors["Access-Control-Allow-Origin"] = origin; cors["Vary"] = "Origin";
                cors["Access-Control-Allow-Methods"] = "POST, OPTIONS";
                cors["Access-Control-Allow-Headers"] = "Authorization, Content-Type, MCP-Protocol-Version, Mcp-Session-Id";
                cors["Access-Control-Expose-Headers"] = "Mcp-Session-Id";
                cors["Access-Control-Max-Age"] = "60";
            }
            if (request.Method == "OPTIONS") return new HttpResponse(origin == null ? 403 : 200, "", cors);
            var authorization = request.Header("authorization");
            if (!_controller.Authorized(authorization, origin)) {
                cors["WWW-Authenticate"] = "Bearer realm=\"YTray\"";
                return Reject(401, "Authorization required", cors, string.IsNullOrEmpty(authorization) ? "Authorization header missing" : "Authorization header rejected");
            }
            var generation = _controller.Generation;
            if (request.Method != "POST") return new HttpResponse(405, "Use POST", cors);
            var clientSession = request.Header("mcp-session-id") ?? "";
            if (clientSession.Length != 0 && !ValidSessionId(clientSession, authorization!))
                return Reject(404, "Unknown MCP session", cors);
            if (request.Header("content-type")?.Split(';')[0].Trim().ToLowerInvariant() != "application/json") return Reject(415, "JSON required", cors);
            var version = request.Header("mcp-protocol-version");
            if (version != null && version != "2025-11-25" && version != "2025-06-18") return Reject(400, "Unsupported protocol version", cors);
            JObject rpc; try { using var json = new JsonTextReader(new StringReader(request.Body)) { DateParseHandling = DateParseHandling.None, MaxDepth = 64 }; rpc = JObject.Load(json); } catch { return new HttpResponse(400, "Invalid JSON-RPC", cors); }
            if ((string?)rpc["jsonrpc"] != "2.0" || rpc["method"]?.Type != JTokenType.String) return new HttpResponse(400, "Invalid JSON-RPC", cors);
            if (rpc["id"] == null) {
                if ((string?)rpc["method"] == "notifications/cancelled" && rpc["params"] is JObject notification
                    && notification["requestId"] is JToken cancelledId && IsRequestId(cancelledId)) {
                    var key = (authorization!, clientSession, cancelledId.ToString(Formatting.None));
                    lock (_activeRequests)
                        if (_activeRequests.TryGetValue(key, out var active)) active.Cancel();
                }
                return new HttpResponse(202, "", cors);
            }
            if (!IsRequestId(rpc["id"]!)) return new HttpResponse(400, "Invalid JSON-RPC id", cors);
            lock (_rateLock) {
                if ((DateTime.UtcNow - _rateWindow).TotalSeconds >= 1) { _rateWindow = DateTime.UtcNow; _rateCount = 0; }
                if (++_rateCount > 20) return new HttpResponse(429, "Rate limit", cors);
            }
            JObject response;
            ControlTraceCall? trace = null;
            JObject? traceResult = null, traceError = null;
            var traceStatus = 200;
            try {
                JObject result; var parameters = rpc["params"] as JObject ?? new JObject();
                if (generation != _controller.Generation) throw new ControlException("GrantRevoked", "会话已撤销");
                switch ((string?)rpc["method"]) {
                    case "initialize":
                        var protocolVersion = (string?)parameters["protocolVersion"] == "2025-06-18" ? "2025-06-18" : "2025-11-25";
                        var sessionNonce = Guid.NewGuid().ToString("N");
                        cors["Mcp-Session-Id"] = sessionNonce + SessionSignature(sessionNonce, authorization!);
                        if (parameters["clientInfo"] is JObject client && client["name"]?.Type == JTokenType.String)
                            _controller.Trace.RegisterClient(sessionNonce, client.Value<string>("name")!);
                        result = new JObject { ["protocolVersion"] = protocolVersion, ["capabilities"] = new JObject { ["tools"] = new JObject() },
                        ["serverInfo"] = new JObject { ["name"] = "YTray Windows Computer Use", ["version"] = "4" },
                        ["instructions"] = "Use open_folder(path) to open an existing Windows folder. Start with list_windows(app) and get_window_state for an open application. launch_app reuses an existing window by default, or starts the app if needed. Browsers accept url for navigation. get_window_state reads the native layout in the background, then tries a window screenshot if the layout is unavailable. It only brings the window forward for a screenshot when a reliable image is unavailable and foreground access is authorized. Use include_screenshot:false for a controls-only observation. If the layout exists but omits your target, request include_screenshot:true. Prefer numbered controls and pass the state_id from the same observation as the control number or image coordinates. Coordinate and keyboard actions activate the window when needed. If the state is stale, choose again from a fresh observation. type_text(element_index?,text,replace?,submit?) can focus, replace and submit in one call. get_window_state(region:{x,y,width,height}) reads local controls and text in window-image pixels (default long edge 1400). Focus and selection depend on the Windows provider. Never repeat an input whose result is unknown." };
                        result["instructions"] = (string?)result["instructions"] + " For window-image clicks with state_id, YTray compares the latest bound image with a fresh window capture before input. A possible blinking caret can trigger a short re-observation. The verified pixels must match before any click. For dynamic windows, verify_regions can pin stable context in addition to the click area. If a running app has no visible window, launch_app with new_window:false can restore a uniquely matching notification-area icon. If the tray action fails after dispatch, check list_windows before another attempt. Use a globally unique task_id for each agent task and pass it to all its tool calls, including across MCP reconnects. On failure, read task.checkpoint and observe the target with the same task_id when the effects are uncertain. Then pass the checkpoint id as resume_from to the next necessary call.";
                        result["instructions"] = (string?)result["instructions"] + " Visible owned popups appear in related_windows. Use each popup's own window_id and state_id with its numbered controls or image at content[image_content_index] (zero-based). Click popup image coordinates while its window group is foreground. PopupChanged returns the owner's latest observation when available. Choose again from that observation.";
                        result["instructions"] = (string?)result["instructions"] + " launch_app and activate_window already return a native-first observation with a window image if needed. Reuse that state and each action's readback when they contain the next target. For a visual click, set target_region to the visible button or field bounds containing the point. Add verify_regions for context that must match, choosing stable bounds around draft text and the conversation title. A failed verification describes the first changed area in error.verification.";
                        DiagnosticLog.Info("control.mcp", $"initialize HTTP 200, protocol={protocolVersion}");
                        break;
                    case "ping": result = new JObject(); break;
                    case "tools/list":
                        var tools = _controller.Tools();
                        result = new JObject { ["tools"] = tools };
                        DiagnosticLog.Info("control.mcp", $"tools/list HTTP 200, tools={tools.Count}");
                        break;
                    case "tools/call":
                        if (parameters["name"]?.Type != JTokenType.String || parameters["arguments"] != null && !(parameters["arguments"] is JObject)) throw new ControlException("InvalidArguments", "工具参数无效");
                        var requestId = rpc["id"]!;
                        var tool = (string)parameters["name"]!;
                        var arguments = (JObject?)parameters["arguments"] ?? new JObject();
                        trace = _controller.Trace.Begin(clientSession, requestId, tool, arguments);
                        var rpcKey = (authorization!, clientSession, requestId.ToString(Formatting.None));
                        using (var cancelledByClient = new CancellationTokenSource())
                        using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline, cancelledByClient.Token, _shutdown.Token)) {
                            lock (_activeRequests) {
                                if (_activeRequests.ContainsKey(rpcKey)) throw new ControlException("RequestConflict", "JSON-RPC id 已有进行中的请求");
                                if (_activeRequests.Count >= ToolCallLimit) {
                                    traceStatus = 429;
                                    traceError = new JObject { ["message"] = "Tool queue full" };
                                    return new HttpResponse(traceStatus, "Tool queue full", cors);
                                }
                                _activeRequests.Add(rpcKey, cancelledByClient);
                            }
                            try {
                                result = await _controller.CallAsync(tool, arguments, generation, cancellation.Token, trace).ConfigureAwait(false);
                            } catch (ControlException error) { result = error.Result(); }
                            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { result = ControlCancellation.Error().Result(); }
                            finally { lock (_activeRequests) _activeRequests.Remove(rpcKey); }
                            traceResult = result;
                            if (cancelledByClient.IsCancellationRequested) { traceStatus = 202; return new HttpResponse(202, "", cors); }
                        }
                        break;
                    default: return RpcError(rpc["id"], -32601, "Method not found", cors);
                }
                response = new JObject { ["jsonrpc"] = "2.0", ["id"] = rpc["id"]!.DeepClone(), ["result"] = result };
            } catch (ControlException error) {
                traceError = new JObject { ["code"] = -32602, ["message"] = error.Message };
                return RpcError(rpc["id"], -32602, error.Message, cors);
            } catch {
                traceError = new JObject { ["code"] = -32603, ["message"] = "Internal error" };
                return RpcError(rpc["id"], -32603, "Internal error", cors);
            } finally {
                if (trace != null) _controller.Trace.Finish(trace, traceStatus, traceResult, traceError);
            }
            cors["Content-Type"] = "application/json";
            return new HttpResponse(200, response.ToString(Formatting.None), cors);
        }
        private static bool IsRequestId(JToken id) => id.Type == JTokenType.Integer || id.Type == JTokenType.String;
        private static string SessionSignature(string nonce, string bearer)
        {
            using var signer = new HMACSHA256(Encoding.UTF8.GetBytes(bearer));
            return BitConverter.ToString(signer.ComputeHash(Encoding.ASCII.GetBytes("YTray MCP session " + nonce))).Replace("-", "");
        }
        private static bool ValidSessionId(string id, string bearer) => id.Length == 96
            && Guid.TryParseExact(id.Substring(0, 32), "N", out _)
            && ControlJson.EqualSecret(id.Substring(32), SessionSignature(id.Substring(0, 32), bearer));
        private static HttpResponse Reject(int status, string reason, Dictionary<string, string>? headers = null, string? logReason = null)
        {
            DiagnosticLog.Warning("control.mcp", $"HTTP {status} {logReason ?? reason}");
            return new HttpResponse(status, reason, headers);
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
        internal static async Task WriteAsync(Stream stream, HttpResponse response, CancellationToken cancellation)
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
