#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YTray.Core.BrowserUse;

namespace YTray.Core
{
    internal static class BrowserEngineNative
    {
        internal const string HostName = "com.yaklang.ytray_engine";
        private const string Prefix = "yak-browser-bridge-v1-";
        private static readonly Regex InstanceId = new Regex("^engine-[a-f0-9]{32}$", RegexOptions.CultureInvariant);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

        private static string DirectoryPath => Path.Combine(StatePersistence.DefaultApplicationDirectory, "BrowserEngines");

        internal static async Task<IReadOnlyList<BrowserEngineTarget>> DiscoverAsync()
        {
            if (!Directory.Exists(DirectoryPath)) return Array.Empty<BrowserEngineTarget>();
            try
            {
                var files = Directory.EnumerateFiles(DirectoryPath, Prefix + "*.json").ToArray();
                var targets = await Task.WhenAll(files.Select(ProbeAsync)).ConfigureAwait(false);
                return targets.OfType<BrowserEngineTarget>().ToArray();
            }
            catch (IOException) { return Array.Empty<BrowserEngineTarget>(); }
            catch (UnauthorizedAccessException) { return Array.Empty<BrowserEngineTarget>(); }
        }

        private static async Task<BrowserEngineTarget?> ProbeAsync(string path)
        {
            try
            {
                if (new FileInfo(path).Length > 32768) return null;
                var hint = JObject.Parse(File.ReadAllText(path));
                var name = (string?)hint["pipeName"];
                var run = (string?)hint["engineInstanceId"];
                var identity = (string?)hint["engineIdentityId"];
                var pid = (int?)hint["pid"];
                if (!ValidTarget(name, run, identity) || pid is not int expected || expected <= 0
                    || Path.GetFileName(path) != name + ".json") return null;
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(550));
                using var pipe = new NamedPipeClientStream(".", name!, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(450, timeout.Token).ConfigureAwait(false);
                if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var owner) || owner != expected) return null;
                await BrowserUseWire.WriteAsync(pipe, new JObject
                {
                    ["type"] = "discover", ["engineIdentityId"] = identity, ["engineInstanceId"] = run,
                }, timeout.Token).ConfigureAwait(false);
                var verified = await BrowserUseWire.ReadAsync(pipe, timeout.Token).ConfigureAwait(false);
                if ((string?)verified["pipeName"] != name || (string?)verified["engineInstanceId"] != run
                    || (string?)verified["engineIdentityId"] != identity || (int?)verified["pid"] != expected) return null;
                var target = BrowserEngineDiscovery.Parse(verified.ToString(Formatting.None), 0);
                if (target == null) return null;
                target.Endpoint = "";
                target.PipeName = name;
                return target;
            }
            catch (Exception error) when (error is IOException || error is JsonException || error is OperationCanceledException
                || error is TimeoutException || error is UnauthorizedAccessException || error is ArgumentException
                || error is FormatException || error is OverflowException)
            {
                return null;
            }
        }

        internal static async Task RelayAsync(Stream input, Stream output, JObject request)
        {
            var name = (string?)request["pipeName"];
            var run = (string?)request["engineInstanceId"];
            var identity = (string?)request["engineIdentityId"];
            var purpose = (string?)request["purpose"];
            if (!ValidTarget(name, run, identity) || (purpose != "connect" && purpose != "pair"))
                throw new InvalidDataException("Invalid browser engine Native Messaging target");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var pipe = new NamedPipeClientStream(".", name!, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(2000, timeout.Token).ConfigureAwait(false);
            await BrowserUseWire.WriteAsync(pipe, new JObject
            {
                ["type"] = "open", ["origin"] = BrowserUseNativeHost.AllowedOrigin.TrimEnd('/'),
                ["purpose"] = purpose, ["engineIdentityId"] = identity, ["engineInstanceId"] = run,
            }, timeout.Token).ConfigureAwait(false);
            var toEngine = Task.Run(() => input.CopyToAsync(pipe));
            var toBrowser = pipe.CopyToAsync(output);
            await await Task.WhenAny(toEngine, toBrowser).ConfigureAwait(false);
        }

        private static bool ValidTarget(string? name, string? run, string? identity) =>
            run?.Length == 39 && InstanceId.IsMatch(run) && name == Prefix + run
            && !string.IsNullOrEmpty(identity) && identity!.Length <= 160;
    }
}
