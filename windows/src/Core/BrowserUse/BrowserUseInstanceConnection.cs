#nullable enable
using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace YTray.Core.BrowserUse
{
    // This connection belongs to a browser profile and stays available with only a blank tab open.
    internal sealed class BrowserUseInstanceConnection : IDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly object _gate = new object();
        private PendingOpen? _pending;
        private volatile bool _closed;
        private volatile bool _ready;

        private sealed class PendingOpen
        {
            internal string Id { get; } = Guid.NewGuid().ToString("N");
            internal string Origin { get; }
            internal bool PageClaimed { get; set; }
            internal TaskCompletionSource<JObject> Reply { get; } = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal PendingOpen(Uri url) => Origin = BrowserUseTools.Origin(url);
        }

        internal Guid Id { get; }
        internal bool IsConnected => _ready && !_closed;

        internal BrowserUseInstanceConnection(NamedPipeServerStream pipe, JObject hello, BrowserUseEnrollment? enrollment)
        {
            _pipe = pipe;
            if (hello["version"]?.Type != JTokenType.Integer || (int?)hello["version"] != 1
                || !Guid.TryParseExact(BrowserUseSession.ReadString(hello, "instance_id", 36), "D", out var id)
                || enrollment?.Accepts(id, BrowserUseSession.ReadString(hello, "managed_ticket", 44)) != true)
                throw new InvalidDataException("This browser instance was not enrolled by YTray.");
            Id = id;
        }

        internal async Task SendReadyAsync(CancellationToken cancellation)
        {
            await BrowserUseWire.WriteAsync(_pipe, new JObject { ["type"] = "ready", ["version"] = 1 }, cancellation).ConfigureAwait(false);
            _ready = true;
        }

        internal bool TryClaimPage(string requestId, string origin)
        {
            lock (_gate)
            {
                if (!IsConnected || _pending?.Id != requestId || _pending.PageClaimed
                    || !string.Equals(_pending.Origin, origin, StringComparison.OrdinalIgnoreCase)) return false;
                _pending.PageClaimed = true;
                return true;
            }
        }

        internal async Task ReceiveAsync(CancellationToken cancellation)
        {
            while (true)
            {
                var response = await BrowserUseWire.ReadAsync(_pipe, cancellation).ConfigureAwait(false);
                lock (_gate)
                {
                    var pending = _pending;
                    if (pending == null || (string?)response["type"] != "response" || (string?)response["id"] != pending.Id
                        || (!(response["result"] is JObject) && !(response["error"] is JObject)))
                        throw new InvalidDataException("Unexpected Browser Use instance response");
                    pending.Reply.TrySetResult(response);
                }
            }
        }

        internal async Task<(string RequestId, string PageId)> OpenAsync(Uri url, CancellationToken cancellation)
        {
            var pending = new PendingOpen(url);
            lock (_gate)
            {
                if (!IsConnected) throw new BrowserUseException("instance_disconnected", "The extension connection closed.", Id);
                if (_pending != null) throw new BrowserUseException("instance_busy", "This instance is already opening a tab.", Id);
                _pending = pending;
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(22));
            using var abort = deadline.Token.Register(Dispose);
            try
            {
                deadline.Token.ThrowIfCancellationRequested();
                await BrowserUseWire.WriteAsync(_pipe, new JObject
                {
                    ["type"] = "request", ["id"] = pending.Id, ["method"] = "open",
                    ["params"] = new JObject { ["url"] = url.AbsoluteUri },
                }, deadline.Token).ConfigureAwait(false);
                var response = await pending.Reply.Task.ConfigureAwait(false);
                if (response["error"] is JObject error)
                    throw new BrowserUseException((string?)error["code"] ?? "open_failed",
                        (string?)error["message"] ?? "The extension could not open the tab.", Id);
                return (pending.Id, BrowserUseSession.ReadString((JObject)response["result"]!, "page_id", 32));
            }
            catch (Exception error) when (error is IOException || error is ObjectDisposedException
                || error is OperationCanceledException || error is InvalidDataException)
            {
                Dispose();
                throw new BrowserUseException(deadline.IsCancellationRequested ? "open_timeout" : "instance_disconnected",
                    "The connection ended while opening the tab. List the instance's pages to check the result before retrying.", Id);
            }
            finally
            {
                lock (_gate) _pending = null;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_closed) return;
                _closed = true;
                _pipe.Dispose();
                _pending?.Reply.TrySetCanceled();
            }
        }
    }
}
