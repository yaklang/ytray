#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace YTray.Core.BrowserUse
{
    internal sealed class BrowserUseException : Exception
    {
        internal string Code { get; }
        internal Guid? InstanceId { get; }
        internal BrowserUseException(string code, string message, Guid? instanceId = null) : base(message)
        {
            Code = code;
            InstanceId = instanceId;
        }
    }

    internal sealed class BrowserUseSession : IDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly object _gate = new object();
        private readonly TaskCompletionSource<bool> _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _captureSequence;
        private string? _captureId;
        private TaskCompletionSource<JObject>? _pending;
        private string? _pendingId;
        private string? _pendingCaptureId;
        private readonly bool _managed;
        private volatile bool _closed;
        internal string Id { get; }
        internal Guid InstanceId { get; }
        internal string Origin { get; }
        internal string? OpenRequestId { get; }
        internal bool Managed => _managed;
        private JObject _info;
        internal JObject Info { get { lock (_gate) return (JObject)_info.DeepClone(); } }
        internal bool IsConnected => !_closed && _ready.Task.Status == TaskStatus.RanToCompletion && _ready.Task.Result;

        internal BrowserUseSession(NamedPipeServerStream pipe, JObject share, string pageId, BrowserUseEnrollment? enrollment)
        {
            _pipe = pipe;
            Id = pageId;
            var instanceId = ReadString(share, "instance_id", 36);
            if ((string?)share["type"] != "share" || share["version"]?.Type != JTokenType.Integer
                || (int?)share["version"] != 1 || !Guid.TryParseExact(instanceId, "D", out var instance))
                throw new InvalidDataException("Invalid Browser Use grant");
            InstanceId = instance;
            var ticket = share["managed_ticket"];
            _managed = ticket != null;
            if (_managed && (ticket?.Type != JTokenType.String || enrollment?.Accepts(instance, (string?)ticket) != true))
                throw new InvalidDataException("This browser was not enrolled by YTray.");
            var url = ReadString(share, "url", 8_192);
            Origin = ReadOrigin(url);
            if (share["open_id"] != null)
                OpenRequestId = ReadString(share, "open_id", 32);
            _info = new JObject
            {
                ["page_id"] = Id,
                ["instance_id"] = instanceId,
                ["instance_label"] = ReadString(share, "instance_label", 32),
                ["title"] = ReadString(share, "title", 1_000),
                ["url"] = url,
                ["managed"] = _managed,
            };
        }

        private static string ReadOrigin(string url)
        {
            if (!BrowserUseTools.TryPageUrl(url, out var uri))
                throw new InvalidDataException("Invalid Browser Use URL");
            return BrowserUseTools.Origin(uri);
        }

        internal static string ReadString(JObject message, string field, int max)
        {
            if (message[field]?.Type == JTokenType.String && (string?)message[field] is string value && value.Length <= max)
                return value;
            throw new InvalidDataException("Invalid Browser Use field: " + field);
        }

        internal async Task SendReadyAsync(CancellationToken cancellation)
        {
            await BrowserUseWire.WriteAsync(_pipe, new JObject { ["type"] = "ready", ["version"] = 1, ["page_id"] = Id }, cancellation).ConfigureAwait(false);
            _ready.TrySetResult(true);
        }

        internal async Task ReceiveAsync(CancellationToken cancellation)
        {
            while (true)
            {
                var response = await BrowserUseWire.ReadAsync(_pipe, cancellation).ConfigureAwait(false);
                lock (_gate)
                {
                    var type = (string?)response["type"];
                    if (_managed && (type == "page_changed" || type == "page_metadata"))
                    {
                        var url = ReadString(response, "url", 8_192);
                        if (!string.Equals(ReadOrigin(url), Origin, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Browser Use navigation crossed the enrolled origin");
                        _info["url"] = url;
                        _info["title"] = ReadString(response, "title", 1_000);
                        if (type == "page_changed")
                        {
                            _captureId = null;
                            _pendingCaptureId = null;
                        }
                        continue;
                    }
                    if ((string?)response["type"] != "response" || _pending == null || (string?)response["id"] != _pendingId
                        || (!(response["result"] is JObject) && !(response["error"] is JObject)))
                        throw new InvalidDataException("Unexpected Browser Use response");
                    var reply = _pending;
                    if (_pendingCaptureId != null && response["result"] is JObject result && response["error"] == null)
                    {
                        _captureId = _pendingCaptureId;
                        result["capture_id"] = _captureId;
                    }
                    _pending = null;
                    _pendingId = null;
                    _pendingCaptureId = null;
                    reply.TrySetResult(response);
                }
            }
        }

        internal async Task<JObject> CallAsync(string method, JObject arguments, CancellationToken cancellation)
        {
            if (!await _ready.Task.ConfigureAwait(false))
                throw new BrowserUseException("page_not_shared", "The page is no longer shared.");
            var reply = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            var id = Guid.NewGuid().ToString("N");
            lock (_gate)
            {
                if (_closed) throw new BrowserUseException("page_not_shared", "The page is no longer shared.");
                if (_pending != null) throw new BrowserUseException("page_busy", "Another operation is running on this page.");
                if (_managed && method != "observe" && method != "focus" && method != "presentation" && _captureId == null)
                    throw new BrowserUseException("stale_node", "Observe the selected tab before operating it.");
                if (method == "observe")
                {
                    _captureId = null;
                    _pendingCaptureId = Id + "c" + (++_captureSequence).ToString(CultureInfo.InvariantCulture);
                    arguments["capture_id"] = _pendingCaptureId;
                }
                else if (arguments["capture_id"] != null && (string?)arguments["capture_id"] != _captureId)
                    throw new BrowserUseException("stale_node", "The capture ID does not belong to the latest observation of this page. Observe it again.");
                _pending = reply;
                _pendingId = id;
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            using var abort = deadline.Token.Register(Dispose);
            JObject response;
            try
            {
                await BrowserUseWire.WriteAsync(_pipe, new JObject
                {
                    ["type"] = "request", ["id"] = id, ["method"] = method, ["params"] = arguments,
                }, deadline.Token).ConfigureAwait(false);
                response = await reply.Task.ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException || error is ObjectDisposedException || error is OperationCanceledException)
            {
                Dispose();
                throw new BrowserUseException(deadline.IsCancellationRequested ? "page_timeout" : "page_disconnected",
                    "The browser connection ended during the operation. List available pages and observe the page before retrying.");
            }
            if (response["error"] is JObject failure)
                throw new BrowserUseException((string?)failure["code"] ?? "page_operation_failed", (string?)failure["message"] ?? "Browser operation failed.");
            return (JObject)response["result"]!;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_closed) return;
                _closed = true;
                _pipe.Dispose();
                _ready.TrySetResult(false);
                _pending?.TrySetCanceled();
            }
        }
    }
}
