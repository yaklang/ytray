#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YTray.Native;

namespace YTray.Core
{
    public sealed class BrowserEngineTarget
    {
        public string Mode { get; set; } = "engine";
        public string FrontendId { get; set; } = "";
        public string FrontendName { get; set; } = "";
        public string? FrontendOwnerId { get; set; }
        public bool ChallengeOnly { get; set; }
        public string? PipeName { get; set; }
        public string EngineIdentityId { get; set; } = "";
        public string EngineInstanceId { get; set; } = "";
        public string Endpoint { get; set; } = "";
        [JsonIgnore] public string DisplayName => Mode == "none" ? "仅浏览器" : Mode == "auto" ? "自动接入本机引擎"
            : Mode == "legacy" ? "使用原有连接设置" : FrontendName;
        [JsonIgnore] public string Detail => Mode == "none" ? "稍后可重新选择接入目标"
            : Mode == "legacy" ? "沿用插件中的引擎地址、配对与连接方式"
            : PipeName != null ? "本机管道"
            : Uri.TryCreate(Endpoint, UriKind.Absolute, out var address) ? address.Authority : "等待目标引擎";
        public BrowserEngineTarget Clone() => (BrowserEngineTarget)MemberwiseClone();
        [JsonIgnore] public string EffectiveFrontendId => FrontendOwnerId ?? FrontendId;
        public bool SameFrontend(BrowserEngineTarget other) => EngineIdentityId == other.EngineIdentityId && EffectiveFrontendId == other.EffectiveFrontendId;
        internal JObject ToWire()
        {
            var payload = new JObject { ["mode"] = Mode };
            if (Mode != "engine") return payload;
            payload["frontendId"] = FrontendId;
            payload["frontendName"] = FrontendName;
            payload["engineIdentityId"] = EngineIdentityId;
            payload["engineInstanceId"] = EngineInstanceId;
            if (PipeName != null) payload["pipeName"] = PipeName;
            else payload["endpoint"] = Endpoint;
            payload["exactInstance"] = true;
            if (ChallengeOnly) payload["challengeOnly"] = true;
            if (FrontendOwnerId != null) payload["ownerFrontendId"] = FrontendOwnerId;
            return payload;
        }
    }

    public static class BrowserEngineDiscovery
    {
        private static readonly HttpClient Client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromMilliseconds(900), MaxResponseContentBufferSize = 32768 };

        public static IReadOnlyList<BrowserEngineTarget> Find(BrowserEngineTarget? remembered)
            => FindAsync(remembered).GetAwaiter().GetResult();

        internal static Task<IReadOnlyList<BrowserEngineTarget>> FindAsync(BrowserEngineTarget? remembered) =>
            FindForTargetsAsync(remembered == null ? Enumerable.Empty<BrowserEngineTarget>() : new[] { remembered }, includeWebSocket: true);

        internal static async Task<IReadOnlyList<BrowserEngineTarget>> FindForTargetsAsync(
            IEnumerable<BrowserEngineTarget> rememberedTargets, bool includeWebSocket = false)
        {
            var remembered = rememberedTargets.ToArray();
            if (!includeWebSocket && !remembered.Any(target => target.Mode == "engine" && target.PipeName == null))
                return await BrowserEngineNative.DiscoverAsync().ConfigureAwait(false);
            var ports = new HashSet<int>();
            foreach (var first in new[] { 64333, 14333, 24333, 34333 })
                for (var port = first; port < first + 16; port++) ports.Add(port);
            foreach (var target in remembered)
                if (Uri.TryCreate(target.Endpoint, UriKind.Absolute, out var previous) && previous.IsLoopback)
                    ports.Add(previous.Port);
            var native = BrowserEngineNative.DiscoverAsync();
            var values = await Task.WhenAll(ports.Select(ProbeAsync)).ConfigureAwait(false);
            return (await native.ConfigureAwait(false)).Concat(values.OfType<BrowserEngineTarget>())
                .GroupBy(value => (value.EffectiveFrontendId, value.EngineIdentityId, value.EngineInstanceId)).Select(group => group.First())
                .OrderBy(value => value.FrontendName).ThenBy(value => value.Endpoint).ToArray();
        }

        private static async Task<BrowserEngineTarget?> ProbeAsync(int port)
        {
            try
            {
                var pid = TcpListenerProcess.FindOwnerProcessId(port);
                using var response = await Client.GetAsync($"http://127.0.0.1:{port}/discovery").ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    var owner = BrowserEngineOwner.Find(pid);
                    return owner != null && pid == TcpListenerProcess.FindOwnerProcessId(port)
                        ? await ProbeChallengeAsync(port, pid, owner).ConfigureAwait(false) : null;
                }
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 32768) return null;
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (body.Length > 32768) return null;
                var target = Parse(body, port);
                if (target != null && pid != 0 && pid == TcpListenerProcess.FindOwnerProcessId(port))
                {
                    var owner = BrowserEngineOwner.Find(pid);
                    if (owner == "memfit" || owner == "yakit")
                    {
                        target.FrontendOwnerId = owner;
                        target.FrontendName = owner == "memfit" ? "Memfit AI" : "Yakit";
                    }
                }
                return target;
            }
            catch { return null; }
        }

        internal static async Task<BrowserEngineTarget?> ProbeChallengeAsync(int port, int pid, string owner)
        {
            // Older Yakit engines expose a signed challenge, but have no discovery route.
            using var socket = new ClientWebSocket();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(650));
            socket.Options.Proxy = new WebProxy();
            socket.Options.SetRequestHeader("Origin", BrowserUse.BrowserUseNativeHost.AllowedOrigin.TrimEnd('/'));
            var address = $"ws://127.0.0.1:{port}/extension";
            await socket.ConnectAsync(new Uri(address), timeout.Token).ConfigureAwait(false);
            var buffer = new byte[4096];
            using var body = new MemoryStream();
            WebSocketReceiveResult part;
            do
            {
                part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token).ConfigureAwait(false);
                if (part.MessageType != WebSocketMessageType.Text || body.Length + part.Count > 32768) return null;
                body.Write(buffer, 0, part.Count);
            } while (!part.EndOfMessage);

            if (pid != TcpListenerProcess.FindOwnerProcessId(port)) return null;
            var challenge = JObject.Parse(Encoding.UTF8.GetString(body.ToArray()));
            if ((string?)challenge["type"] != "challenge" || challenge["publicKey"] is not JObject
                || string.IsNullOrEmpty((string?)challenge["signature"])) return null;
            challenge["type"] = "engine";
            challenge["frontendId"] = owner;
            var target = Parse(challenge.ToString(Formatting.None), port);
            if (target != null)
            {
                target.ChallengeOnly = true;
                target.FrontendOwnerId = owner;
            }
            return target;
        }

        internal static BrowserEngineTarget? Parse(string body, int port)
        {
            var value = JObject.Parse(body);
            if ((string?)value["type"] != "engine" || (int?)value["protocolVersion"] != 3) return null;
            var identity = (string?)value["engineIdentityId"];
            var instance = (string?)value["engineInstanceId"];
            if (string.IsNullOrWhiteSpace(identity) || identity!.Length > 160 || string.IsNullOrWhiteSpace(instance) || instance!.Length > 160) return null;
            var frontendId = (string?)value["frontendId"] ?? "yak";
            if (frontendId.Length == 0 || frontendId.Length > 80) frontendId = "yak";
            var name = frontendId switch
            {
                "memfit" => "Memfit AI",
                "yakit" => "Yakit",
                "yakit-enterprise" => "Yakit 企业版",
                "irify" => "IRify",
                "yak" => "独立 Yak 引擎",
                _ => (string?)value["frontendName"] ?? "本机引擎",
            };
            return new BrowserEngineTarget
            {
                EngineIdentityId = identity, EngineInstanceId = instance,
                FrontendId = frontendId, FrontendName = string.IsNullOrWhiteSpace(name) ? "本机引擎" : name.Substring(0, Math.Min(name.Length, 120)),
                Endpoint = $"ws://127.0.0.1:{port}/extension",
            };
        }

        internal static BrowserEngineTarget? ResolveRemembered(BrowserEngineTarget? remembered, IReadOnlyList<BrowserEngineTarget> online)
        {
            if (remembered == null) return null;
            if (remembered.Mode == "none" || remembered.Mode == "legacy") return remembered.Clone();
            var matches = online.Where(value => remembered.SameFrontend(value)
                && (remembered.PipeName == null || value.PipeName != null)).ToArray();
            return matches.FirstOrDefault(value => value.EngineInstanceId == remembered.EngineInstanceId)
                ?? (matches.Length == 1 ? matches[0] : matches.Length == 0 ? remembered.Clone() : null);
        }

        internal static BrowserEngineTarget ResolveForLaunch(BrowserEngineTarget? remembered,
            IReadOnlyList<BrowserEngineTarget> online)
        {
            if (remembered != null && remembered.Mode != "auto")
                return ResolveRemembered(remembered, online) ?? remembered.Clone();
            return new BrowserEngineTarget { Mode = "auto" };
        }
    }
}
