#nullable enable
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YTray.Core.SystemControl;
using YTray.Models;
using YTray.Native;
using HttpRequest = YTray.Core.SystemControl.ControlGateway.HttpRequest;
using HttpResponse = YTray.Core.SystemControl.ControlGateway.HttpResponse;

namespace YTray.Core.BrowserUse
{
    internal sealed class BrowserUseServer : IDisposable
    {
        internal const int Port = 51374;
        private const string Path = "/browser/mcp";
        private const int ConnectionLimit = 16;
        private const int SharedPageLimit = 16;
        private const int InstanceLimit = 16;
        private static readonly string[] ProtocolVersions = { "2025-11-25", "2025-06-18", "2025-03-26" };
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private readonly SemaphoreSlim _connections = new SemaphoreSlim(ConnectionLimit);
        private readonly SemaphoreSlim _pipeConnections = new SemaphoreSlim(SharedPageLimit + InstanceLimit + 4);
        private readonly SemaphoreSlim _pageSlots = new SemaphoreSlim(SharedPageLimit);
        private readonly SemaphoreSlim _instanceSlots = new SemaphoreSlim(InstanceLimit);
        private readonly ConcurrentDictionary<string, BrowserUseSession> _sessions = new ConcurrentDictionary<string, BrowserUseSession>();
        private readonly ConcurrentDictionary<Guid, BrowserUseInstanceConnection> _instances = new ConcurrentDictionary<Guid, BrowserUseInstanceConnection>();
        private readonly TcpListener _listener;
        private readonly BrowserUseNativeHost? _host;
        private readonly string _authorization;
        private readonly BrowserUseEnrollment? _enrollment;
        private readonly InstanceStore? _store;
        private readonly Dispatcher? _dispatcher;
        private readonly string _pageIdPrefix = "p" + Guid.NewGuid().ToString("N").Substring(0, 8) + "-";
        private long _nextPageId;
        internal string Endpoint { get; }
        internal int BoundPort { get; }
        internal string ConfigurationJSON => new JObject
        {
            ["mcpServers"] = new JObject
            {
                ["ytray-browser"] = new JObject
                {
                    ["type"] = "http", ["url"] = Endpoint,
                    ["headers"] = new JObject { ["Authorization"] = _authorization },
                },
            },
        }.ToString(Formatting.Indented);

        internal BrowserUseServer(string directory, string executable, int port = Port,
            string? accessToken = null, BrowserUseEnrollment? enrollment = null, InstanceStore? store = null)
        {
            _authorization = "Bearer " + (accessToken ?? ControlJson.Secret());
            _enrollment = enrollment;
            _store = store;
            _dispatcher = store == null ? null : Dispatcher.CurrentDispatcher;
            _listener = new TcpListener(IPAddress.Loopback, port);
            try
            {
                _listener.Start(ConnectionLimit);
                BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
                Endpoint = "http://127.0.0.1:" + BoundPort + Path;
                if (!string.IsNullOrEmpty(executable))
                {
                    _host = new BrowserUseNativeHost(directory, executable, BoundPort);
                    _host.Register();
                }
                _ = Task.Run(AcceptHttpAsync);
                _ = Task.Run(AcceptBrowserAsync);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private async Task AcceptHttpAsync()
        {
            while (!_shutdown.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    if (_connections.Wait(0)) _ = HandleClientAsync(client);
                    else client.Dispose();
                }
                catch (Exception) when (_shutdown.IsCancellationRequested) { return; }
                catch (SocketException error) { DiagnosticLog.Error("browser-use.http.accept", error); return; }
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                using var close = deadline.Token.Register(client.Close);
                try
                {
                    var stream = client.GetStream();
                    var request = await ControlGateway.ReadAsync(stream, deadline.Token).ConfigureAwait(false);
                    deadline.CancelAfter(TimeSpan.FromSeconds(70));
                    var result = await HandleRequestAsync(request, deadline.Token).ConfigureAwait(false);
                    await ControlGateway.WriteAsync(stream, result, deadline.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException || error is ObjectDisposedException || error is OperationCanceledException) { }
                catch (Exception error) { DiagnosticLog.Error("browser-use.http.request", error); }
                finally { _connections.Release(); }
            }
        }

        private async Task AcceptBrowserAsync()
        {
            while (!_shutdown.IsCancellationRequested)
            {
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = CreatePipe();
                    using (var stop = _shutdown.Token.Register(pipe.Dispose))
                        await pipe.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);
                    if (!_pipeConnections.Wait(0)) continue;
                    _ = HandleBrowserAsync(pipe);
                    pipe = null;
                }
                catch (Exception) when (_shutdown.IsCancellationRequested) { return; }
                catch (Exception error) { DiagnosticLog.Error("browser-use.pipe.accept", error); return; }
                finally { pipe?.Dispose(); }
            }
        }

        private NamedPipeServerStream CreatePipe()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User ?? throw new InvalidOperationException("无法识别当前 Windows 用户");
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                PipeAccessRights.FullControl, AccessControlType.Deny));
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
            return new NamedPipeServerStream(BrowserUseNativeHost.PipeName(BoundPort), PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                4096, 4096, security);
        }

        private async Task HandleBrowserAsync(NamedPipeServerStream pipe)
        {
            BrowserUseSession? session = null;
            var added = false;
            var pageSlot = false;
            using (pipe)
            using (_shutdown.Token.Register(pipe.Dispose))
            {
                try
                {
                    using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token))
                    {
                        deadline.CancelAfter(TimeSpan.FromSeconds(5));
                        using var abort = deadline.Token.Register(pipe.Dispose);
                        var share = await BrowserUseWire.ReadAsync(pipe, deadline.Token).ConfigureAwait(false);
                        if ((string?)share["type"] == "instance")
                        {
                            abort.Dispose();
                            await HandleInstanceAsync(pipe, share, deadline.Token).ConfigureAwait(false);
                            return;
                        }
                        var pageId = _pageIdPrefix + Interlocked.Increment(ref _nextPageId).ToString(CultureInfo.InvariantCulture);
                        if (!_pageSlots.Wait(0)) throw new InvalidDataException("The Browser Use page limit has been reached.");
                        pageSlot = true;
                        var candidate = new BrowserUseSession(pipe, share, pageId, _enrollment);
                        if (candidate.OpenRequestId is string openId && (!candidate.Managed
                            || !_instances.TryGetValue(candidate.InstanceId, out var instance)
                            || !instance.TryClaimPage(openId, candidate.Origin)))
                            throw new InvalidDataException("This tab does not match a pending Browser Use request.");
                        session = candidate;
                        added = _sessions.TryAdd(session.Id, session);
                        if (!added) throw new InvalidDataException("Duplicate Browser Use page");
                        await session.SendReadyAsync(deadline.Token).ConfigureAwait(false);
                    }
                    await session.ReceiveAsync(_shutdown.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException || error is ObjectDisposedException || error is OperationCanceledException) { }
                catch (InvalidDataException error) when (session == null)
                {
                    DiagnosticLog.Warning("browser-use.pipe.enrollment", error.Message);
                    try
                    {
                        await BrowserUseWire.WriteAsync(pipe, new JObject { ["type"] = "rejected" },
                            _shutdown.Token).ConfigureAwait(false);
                    }
                    catch (Exception) { /* the browser already closed */ }
                }
                catch (Exception error) { DiagnosticLog.Error("browser-use.pipe.session", error); }
                finally
                {
                    session?.Dispose();
                    if (added && session != null) _sessions.TryRemove(session.Id, out _);
                    if (pageSlot) _pageSlots.Release();
                    _pipeConnections.Release();
                }
            }
        }

        private async Task HandleInstanceAsync(NamedPipeServerStream pipe, JObject hello, CancellationToken handshake)
        {
            var connection = new BrowserUseInstanceConnection(pipe, hello, _enrollment);
            if (!_instanceSlots.Wait(0)) throw new InvalidDataException("The Browser Use instance limit has been reached.");
            var added = false;
            try
            {
                added = _instances.TryAdd(connection.Id, connection);
                if (!added) throw new InvalidDataException("This browser instance is already connected.");
                using (handshake.Register(pipe.Dispose)) await connection.SendReadyAsync(handshake).ConfigureAwait(false);
                await connection.ReceiveAsync(_shutdown.Token).ConfigureAwait(false);
            }
            finally
            {
                if (added) _instances.TryRemove(connection.Id, out _);
                _instanceSlots.Release();
                if (added) connection.Dispose();
            }
        }

        internal async Task<HttpResponse> HandleRequestAsync(HttpRequest request, CancellationToken cancellation)
        {
            if (request.Path != Path) return new HttpResponse(404, "");
            var host = new Uri(Endpoint).Authority;
            if (!string.Equals(request.Header("Host"), host, StringComparison.OrdinalIgnoreCase) || request.Header("Origin") != null)
                return new HttpResponse(403, "");
            if (!ControlJson.EqualSecret(request.Header("Authorization") ?? "", _authorization))
                return new HttpResponse(401, "", new System.Collections.Generic.Dictionary<string, string> { ["WWW-Authenticate"] = "Bearer" });
            if (request.Method != "POST")
                return new HttpResponse(405, "", new System.Collections.Generic.Dictionary<string, string> { ["Allow"] = "POST" });
            var mediaType = request.Header("Content-Type")?.Split(';')[0].Trim();
            if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                return new HttpResponse(415, "");

            JObject rpc;
            try { rpc = JObject.Parse(request.Body); }
            catch (JsonException) { return Error(null, -32700, "Parse error", 400); }
            var id = rpc["id"];
            if ((string?)rpc["jsonrpc"] != "2.0" || rpc["method"]?.Type != JTokenType.String
                || (id != null && id.Type != JTokenType.String && id.Type != JTokenType.Integer))
                return Error(null, -32600, "Invalid request", 400);
            if (id == null) return new HttpResponse(202, "");
            var method = (string)rpc["method"]!;
            var protocol = request.Header("MCP-Protocol-Version");
            if (method != "initialize" && protocol != null && !ProtocolVersions.Contains(protocol))
                return Error(id, -32600, "Unsupported MCP protocol version", 400);
            if (rpc["params"] != null && !(rpc["params"] is JObject))
                return Error(id, -32602, "params must be an object");
            var parameters = rpc["params"] as JObject ?? new JObject();
            JObject result;
            switch (method)
            {
                case "initialize":
                    var requested = parameters["protocolVersion"]?.Type == JTokenType.String
                        ? (string?)parameters["protocolVersion"] : null;
                    result = new JObject
                    {
                        ["protocolVersion"] = requested != null && ProtocolVersions.Contains(requested) ? requested : ProtocolVersions[0],
                        ["capabilities"] = new JObject { ["tools"] = new JObject() },
                        ["serverInfo"] = new JObject { ["name"] = "ytray-browser", ["version"] = YTrayBuildInfo.Version },
                        ["instructions"] = "Use browser_list_instances to resolve the user's A/B instance and browser_list_runtimes to resolve a browser for a new instance. For an existing page, list its instance's pages. For an explicit open request, browser_open waits for the exact new tab and returns its page_id. Ask only if the target is ambiguous. Use browser_focus when the user wants to see the selected page or its action feedback. Observe the page, then use its capture and node IDs. Keep the same page ID after same-origin navigation and observe again before acting. Unmanaged tabs can be connected through the extension.",
                    };
                    break;
                case "ping": result = new JObject(); break;
                case "tools/list": result = new JObject { ["tools"] = BrowserUseTools.Definitions.DeepClone() }; break;
                case "tools/call": result = await CallToolAsync(parameters, cancellation).ConfigureAwait(false); break;
                default: return Error(id, -32601, "Method not found");
            }
            return Json(new JObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result });
        }

        private async Task<JObject> CallToolAsync(JObject parameters, CancellationToken cancellation)
        {
            try
            {
                var (method, arguments) = BrowserUseTools.Parse(parameters);
                if (method == "list_pages")
                {
                    var instance = arguments["instance_id"] is JToken id ? Guid.Parse((string)id!) : (Guid?)null;
                    var pages = new JArray(_sessions.Values.Where(page => page.IsConnected
                            && (instance == null || page.InstanceId == instance)).OrderBy(page => page.Id.Length)
                        .ThenBy(page => page.Id, StringComparer.Ordinal).Select(page => page.Info));
                    return BrowserUseTools.Result(new JObject
                    {
                        ["pages"] = pages,
                        ["hint"] = pages.Count == 0
                            ? "Open an HTTP(S) tab in a browser launched by YTray. For an existing unmanaged browser, use the extension's Share this page button."
                            : null,
                    });
                }
                if (method == "list_runtimes" || method == "list_instances" || method == "open")
                {
                    var store = _store ?? throw new BrowserUseException("instance_management_unavailable", "This MCP server is not attached to YTray instance management.");
                    if (method == "open") return BrowserUseTools.Result(await OpenPageAsync(store, arguments, cancellation).ConfigureAwait(false));
                    var list = await OnDispatcherAsync(() => method == "list_runtimes"
                        ? new JObject { ["runtimes"] = new JArray(store.Runtimes.Select(runtime => McpServer.RuntimeJson(runtime, store.DefaultRuntime?.Id))) }
                        : new JObject { ["instances"] = new JArray(store.RunningInstances.Select(DescribeInstance)) }, cancellation).ConfigureAwait(false);
                    return BrowserUseTools.Result(list);
                }
                var pageId = (string)arguments["page_id"]!;
                if (!_sessions.TryGetValue(pageId, out var session))
                    throw new BrowserUseException("page_not_shared", "The page is not shared. List the currently shared pages.");
                var result = await session.CallAsync(method, arguments, cancellation).ConfigureAwait(false);
                if (method == "focus")
                {
                    if (_store != null && session.Managed)
                    {
                        var (window, focused) = await OnDispatcherAsync(async () =>
                        {
                            var handle = _store.ResolveBrowserUseWindow(session.InstanceId);
                            if (handle != IntPtr.Zero) WindowEnum.RaiseBrowserWindow(handle);
                            return (handle, handle != IntPtr.Zero && await WindowsApplicationControl.TryBringToForegroundAsync(handle, cancellation));
                        }, cancellation).Unwrap().ConfigureAwait(false);
                        if (window != IntPtr.Zero)
                        {
                            result = await session.CallAsync("presentation", arguments, cancellation).ConfigureAwait(false);
                            result["window_focused"] = focused && WindowsApplicationControl.IsForeground(window);
                        }
                    }
                    if ((string?)result["visibility"] != "visible")
                        throw new BrowserUseException("page_not_visible", "The tab is selected, but its browser window is still hidden. Bring this window into view to see feedback.", session.InstanceId);
                }
                result["page_id"] = pageId;
                return BrowserUseTools.Result(result);
            }
            catch (BrowserUseException error)
            {
                var failure = new JObject { ["code"] = error.Code, ["message"] = error.Message };
                if (error.InstanceId is Guid instanceId) failure["instance_id"] = instanceId.ToString("D");
                return BrowserUseTools.Result(failure, true);
            }
        }

        private JObject DescribeInstance(BrowserInstance instance) => new JObject
        {
            ["instance_id"] = instance.Id.ToString("D"),
            ["instance_label"] = instance.DockBadge,
            ["name"] = instance.Name,
            ["runtime"] = instance.RuntimeName,
            ["runtime_id"] = instance.RuntimeID.ToString("D"),
            ["extension_ready"] = _instances.TryGetValue(instance.Id, out var connection) && connection.IsConnected,
        };

        private Task<T> OnDispatcherAsync<T>(Func<T> action, CancellationToken cancellation) =>
            _dispatcher!.InvokeAsync(action, DispatcherPriority.Normal, cancellation).Task;

        private async Task<JObject> OpenPageAsync(InstanceStore store, JObject arguments, CancellationToken cancellation)
        {
            // Leave time to serialize a structured timeout before the HTTP connection closes.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(65));
            var runtimeId = (string?)arguments["runtime_id"];
            var instanceId = runtimeId == null ? Guid.Parse((string)arguments["instance_id"]!) : (Guid?)null;
            var created = runtimeId != null;
            var url = new Uri((string)arguments["url"]!);
            try
            {
                var id = await OnDispatcherAsync(() =>
                {
                    if (runtimeId != null) return store.LaunchForBrowserUse(Guid.Parse(runtimeId), url, (bool?)arguments["use_proxy"] == true).Id;
                    if (store.RunningInstances.Any(instance => instance.Id == instanceId && !instance.IsStopping)) return instanceId!.Value;
                    throw new BrowserUseException("instance_not_found", "Choose a running instance from browser_list_instances.");
                }, deadline.Token).ConfigureAwait(false);
                instanceId = id;

                // A new browser may need to load the extension before the native profile connection appears.
                var connection = await WaitForInstanceAsync(store, id, created, deadline.Token).ConfigureAwait(false);
                var (requestId, pageId) = await connection.OpenAsync(url, deadline.Token).ConfigureAwait(false);
                if (!_sessions.TryGetValue(pageId, out var page) || page.InstanceId != id || page.OpenRequestId != requestId || !page.IsConnected)
                    throw new BrowserUseException("target_unavailable", "The opened tab is no longer connected. List the instance's pages before retrying.", id);
                var result = page.Info;
                result["created_instance"] = created;
                return result;
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                throw new BrowserUseException("open_timeout", "YTray timed out waiting for the extension or target tab. Check the instance's pages before retrying.", instanceId);
            }
        }

        private async Task<BrowserUseInstanceConnection> WaitForInstanceAsync(InstanceStore store, Guid id, bool created, CancellationToken cancellation)
        {
            var wait = Stopwatch.StartNew();
            var timeout = TimeSpan.FromSeconds(created ? 43 : 6);
            do
            {
                if (_instances.TryGetValue(id, out var connection) && connection.IsConnected) return connection;
                await OnDispatcherAsync(() =>
                {
                    if (!store.RunningInstances.Any(instance => instance.Id == id && !instance.IsStopping))
                    {
                        if (created && store.LaunchingInstanceID != id && store.ErrorMessage is string failure)
                            throw new BrowserUseException("launch_failed", failure, id);
                        throw new BrowserUseException("instance_stopped", "The selected instance stopped while waiting for the extension.", id);
                    }
                    return true;
                }, cancellation).ConfigureAwait(false);
                await Task.Delay(150, cancellation).ConfigureAwait(false);
            }
            while (wait.Elapsed < timeout);
            throw new BrowserUseException("extension_not_ready", "The selected instance has no Browser Use connection. Check the bundled extension and restart that instance from YTray if it is using an older version.", id);
        }

        private static HttpResponse Error(JToken? id, int code, string message, int status = 200) =>
            Json(new JObject
            {
                ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(),
                ["error"] = new JObject { ["code"] = code, ["message"] = message },
            }, status);

        private static HttpResponse Json(JObject value, int status = 200) =>
            new HttpResponse(status, value.ToString(Formatting.None),
                new System.Collections.Generic.Dictionary<string, string> { ["Content-Type"] = "application/json" });

        public void Dispose()
        {
            _shutdown.Cancel();
            _listener.Stop();
            foreach (var session in _sessions.Values) session.Dispose();
            foreach (var instance in _instances.Values) instance.Dispose();
            _host?.Dispose();
        }
    }
}
