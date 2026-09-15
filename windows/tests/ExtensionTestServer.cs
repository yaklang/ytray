#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace YTray.Tests
{
    /// <summary>Local deterministic HTTP origin/proxy. Never forwards requests to the internet.</summary>
    internal sealed class ExtensionTestServer : IDisposable
    {
        internal const string Username = "ytray-test-user";
        internal const string Password = "test-only-secret";
        internal const string Title = "YTray extensions verified";
        private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly ConcurrentBag<TcpClient> _clients = new ConcurrentBag<TcpClient>();
        private readonly Task _loop;
        private volatile bool _stopped;
        internal volatile bool RequireAuthentication;
        private int _challenges;
        private int _authenticated;
        internal int Challenges => _challenges;
        internal int AuthenticatedRequests => _authenticated;
        internal int Port { get; }
        internal string DirectURL => $"http://127.0.0.1:{Port}/verify";
        internal string ProxiedURL => "http://ytray-proxy.test/verify";
        internal ExtensionTestServer()
        {
            _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(async () =>
            {
                while (!_stopped)
                {
                    try
                    {
                        var client = await _listener.AcceptTcpClientAsync();
                        _clients.Add(client);
                        _ = HandleAsync(client);
                    }
                    catch when (_stopped) { break; }
                }
            });
        }
        private async Task HandleAsync(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, true))
                {
                    var first = await reader.ReadLineAsync() ?? "";
                    var auth = "";
                    for (var i = 0; i < 100; i++)
                    {
                        var header = await reader.ReadLineAsync();
                        if (string.IsNullOrEmpty(header)) break;
                        if (header.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase))
                            auth = header.Substring("Proxy-Authorization:".Length).Trim();
                    }
                    var target = first.Contains("/verify");
                    string response;
                    if (!target) response = "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                    else if (RequireAuthentication && auth != "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(Username + ":" + Password)))
                    {
                        Interlocked.Increment(ref _challenges);
                        response = "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"YTray test\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                    }
                    else
                    {
                        if (RequireAuthentication) Interlocked.Increment(ref _authenticated);
                        var body = "<!doctype html><title>" + Title + "</title><h1>YTray plugin verification</h1>";
                        response = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nContent-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\nConnection: close\r\n\r\n" + body;
                    }
                    var bytes = Encoding.UTF8.GetBytes(response);
                    await stream.WriteAsync(bytes, 0, bytes.Length);
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
        public void Dispose()
        {
            _stopped = true; _listener.Stop();
            foreach (var client in _clients) client.Dispose();
            _loop.GetAwaiter().GetResult();
        }
    }
}
