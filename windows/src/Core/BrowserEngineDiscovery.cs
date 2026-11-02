#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace YTray.Core
{
    public sealed class BrowserEngineTarget
    {
        public string Mode { get; set; } = "engine";
        public string FrontendId { get; set; } = "";
        public string FrontendName { get; set; } = "";
        public string EngineIdentityId { get; set; } = "";
        public string EngineInstanceId { get; set; } = "";
        public string Endpoint { get; set; } = "";
        [JsonIgnore] public string DisplayName => Mode == "none" ? "仅启动浏览器，暂不接入"
            : Mode == "legacy" ? "使用原有连接设置" : FrontendName;
        [JsonIgnore] public string Detail => Mode == "none" ? "稍后可重新选择接入目标"
            : Mode == "legacy" ? "沿用插件中的引擎地址、配对与连接方式"
            : Uri.TryCreate(Endpoint, UriKind.Absolute, out var address) ? address.Authority : "等待目标引擎";
        public BrowserEngineTarget Clone() => (BrowserEngineTarget)MemberwiseClone();
        public bool SameFrontend(BrowserEngineTarget other) => EngineIdentityId == other.EngineIdentityId && FrontendId == other.FrontendId;
    }

    public static class BrowserEngineDiscovery
    {
        private static readonly HttpClient Client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromMilliseconds(900), MaxResponseContentBufferSize = 32768 };

        public static IReadOnlyList<BrowserEngineTarget> Find(BrowserEngineTarget? remembered)
            => FindAsync(remembered).GetAwaiter().GetResult();

        private static async Task<IReadOnlyList<BrowserEngineTarget>> FindAsync(BrowserEngineTarget? remembered)
        {
            var ports = new HashSet<int>();
            foreach (var first in new[] { 64333, 14333, 24333, 34333 })
                for (var port = first; port < first + 16; port++) ports.Add(port);
            if (Uri.TryCreate(remembered?.Endpoint, UriKind.Absolute, out var previous) && previous.IsLoopback)
                ports.Add(previous.Port);
            var values = await Task.WhenAll(ports.Select(ProbeAsync)).ConfigureAwait(false);
            return values.Where(value => value != null).Select(value => value!)
                .GroupBy(value => value.EngineIdentityId + ":" + value.EngineInstanceId).Select(group => group.First())
                .OrderBy(value => value.FrontendName).ThenBy(value => value.Endpoint).ToArray();
        }

        private static async Task<BrowserEngineTarget?> ProbeAsync(int port)
        {
            try
            {
                using var response = await Client.GetAsync($"http://127.0.0.1:{port}/discovery").ConfigureAwait(false);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 32768) return null;
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (body.Length > 32768) return null;
                return Parse(body, port);
            }
            catch { return null; }
        }

        internal static BrowserEngineTarget? Parse(string body, int port)
        {
            var value = JObject.Parse(body);
            if ((string?)value["type"] != "engine" || (int?)value["protocolVersion"] != 3) return null;
            var identity = (string?)value["engineIdentityId"];
            var instance = (string?)value["engineInstanceId"];
            if (string.IsNullOrWhiteSpace(identity) || identity!.Length > 160 || string.IsNullOrWhiteSpace(instance) || instance!.Length > 160) return null;
            return new BrowserEngineTarget
            {
                EngineIdentityId = identity, EngineInstanceId = instance,
                FrontendId = ((string?)value["frontendId"] ?? "yak").Substring(0, Math.Min(((string?)value["frontendId"] ?? "yak").Length, 80)),
                FrontendName = ((string?)value["frontendName"] ?? "独立 Yak 引擎").Substring(0, Math.Min(((string?)value["frontendName"] ?? "独立 Yak 引擎").Length, 120)),
                Endpoint = $"ws://127.0.0.1:{port}/extension",
            };
        }

        internal static BrowserEngineTarget? ResolveRemembered(BrowserEngineTarget? remembered, IReadOnlyList<BrowserEngineTarget> online)
        {
            if (remembered == null) return null;
            if (remembered.Mode == "none" || remembered.Mode == "legacy") return remembered.Clone();
            var matches = online.Where(remembered.SameFrontend).ToArray();
            return matches.FirstOrDefault(value => value.EngineInstanceId == remembered.EngineInstanceId)
                ?? (matches.Length == 1 ? matches[0] : matches.Length == 0 ? remembered.Clone() : null);
        }

        internal static BrowserEngineTarget? ResolveForLaunch(BrowserEngineTarget? remembered,
            IReadOnlyList<BrowserEngineTarget> online, bool restoring)
        {
            if (remembered != null) return ResolveRemembered(remembered, online);
            // Existing profiles keep their plugin pairing until the user explicitly selects a target.
            if (restoring || online.Count == 0) return new BrowserEngineTarget { Mode = "legacy" };
            return online.Count == 1 ? online[0] : null;
        }
    }
}
