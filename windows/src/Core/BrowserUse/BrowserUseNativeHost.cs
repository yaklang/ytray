#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace YTray.Core.BrowserUse
{
    internal sealed class BrowserUseNativeHost : IDisposable
    {
        private const string Name = "com.yaklang.ytray_browser_use";
        // Stable Chromium ID from the public manifest key in the bundled extension.
        internal const string AllowedOrigin = "chrome-extension://mcnaombmlombekhbonfndagbcfhmoail/";
        private static readonly string PipePrefix = "ytray-browser-use-v1-" + Process.GetCurrentProcess().SessionId;
        internal static string PipeName(int port) => PipePrefix + "-" + port;
        private static readonly string[] RegistryPaths =
        {
            @"Software\Google\Chrome\NativeMessagingHosts\",
            @"Software\Google\ChromeForTesting\NativeMessagingHosts\",
            @"Software\Chromium\NativeMessagingHosts\",
            @"Software\Microsoft\Edge\NativeMessagingHosts\",
        };
        private readonly string _manifest;
        private readonly string _name;
        private readonly string _executable;

        internal BrowserUseNativeHost(string directory, string executable, int port)
        {
            _name = port == BrowserUseServer.Port ? Name : Name + ".test_" + port;
            _executable = executable;
            Directory.CreateDirectory(directory);
            _manifest = Path.Combine(directory, _name + "." + Guid.NewGuid().ToString("N") + ".json");
        }

        private string RegistryPath(string browser) => browser + _name;

        internal void Register()
        {
            var manifest = new JObject
            {
                ["name"] = _name,
                ["description"] = "YTray Browser Use",
                ["path"] = _executable,
                ["type"] = "stdio",
                ["allowed_origins"] = new JArray(AllowedOrigin),
            };
            File.WriteAllText(_manifest, manifest.ToString(Formatting.None), new UTF8Encoding(false));
            foreach (var path in RegistryPaths)
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegistryPath(path));
                if (key == null) throw new InvalidOperationException("无法注册浏览器控制 Native Messaging Host");
                key.SetValue("", _manifest, RegistryValueKind.String);
            }
        }

        public void Dispose()
        {
            foreach (var path in RegistryPaths)
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RegistryPath(path));
                    if (string.Equals(key?.GetValue("") as string, _manifest, StringComparison.OrdinalIgnoreCase))
                        Registry.CurrentUser.DeleteSubKey(RegistryPath(path), false);
                }
                catch (Exception error) { DiagnosticLog.Error("browser-use.host.unregister", error); }
            }
            try { File.Delete(_manifest); }
            catch (Exception error) { DiagnosticLog.Error("browser-use.host.unregister", error); }
        }

        internal static int Run()
        {
            try
            {
                using var input = Console.OpenStandardInput();
                using var output = Console.OpenStandardOutput();
                var port = BrowserUseServer.Port;
#if DEBUG
                var first = BrowserUseWire.ReadAsync(input, CancellationToken.None).GetAwaiter().GetResult();
                if (first["host_port"]?.Type == JTokenType.Integer && (int?)first["host_port"] is int requested
                    && requested >= 1 && requested <= 65535) port = requested;
#endif
                using var pipe = new NamedPipeClientStream(".", PipeName(port), PipeDirection.InOut, PipeOptions.Asynchronous);
                pipe.Connect(2_000);
#if DEBUG
                BrowserUseWire.WriteAsync(pipe, first, CancellationToken.None).GetAwaiter().GetResult();
#endif
                var toYTray = Task.Run(() => input.CopyToAsync(pipe));
                var toBrowser = pipe.CopyToAsync(output);
                Task.WhenAny(toYTray, toBrowser).GetAwaiter().GetResult().GetAwaiter().GetResult();
                return 0;
            }
            catch (IOException) { return 1; }
            catch (TimeoutException) { return 1; }
            catch (ObjectDisposedException) { return 1; }
            catch (JsonException) { return 1; }
        }
    }

    internal static class BrowserUseWire
    {
        private const int MaxMessageBytes = 256 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        internal static async Task<JObject> ReadAsync(Stream stream, CancellationToken cancellation)
        {
            var header = new byte[4];
            await ReadExactlyAsync(stream, header, cancellation).ConfigureAwait(false);
            var size = BitConverter.ToInt32(header, 0);
            if (size <= 0 || size > MaxMessageBytes) throw new InvalidDataException("Invalid Native Messaging frame length");
            var bytes = new byte[size];
            await ReadExactlyAsync(stream, bytes, cancellation).ConfigureAwait(false);
            return JObject.Parse(Utf8.GetString(bytes));
        }

        internal static async Task WriteAsync(Stream stream, JObject message, CancellationToken cancellation)
        {
            var bytes = Utf8.GetBytes(message.ToString(Formatting.None));
            if (bytes.Length > MaxMessageBytes) throw new InvalidDataException("Native Messaging frame exceeds size limit");
            var header = BitConverter.GetBytes(bytes.Length);
            await stream.WriteAsync(header, 0, header.Length, cancellation).ConfigureAwait(false);
            await stream.WriteAsync(bytes, 0, bytes.Length, cancellation).ConfigureAwait(false);
            await stream.FlushAsync(cancellation).ConfigureAwait(false);
        }

        private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellation)
        {
            for (var offset = 0; offset < buffer.Length;)
            {
                var count = await stream.ReadAsync(buffer, offset, buffer.Length - offset, cancellation).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException();
                offset += count;
            }
        }
    }
}
