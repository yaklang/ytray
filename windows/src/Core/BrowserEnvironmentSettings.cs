#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YTray.Models;

namespace YTray.Core
{
    internal static class BrowserEnvironmentSettings
    {
        internal static readonly (string Label, string Language, string TimeZone)[] Regions =
        {
            ("美国 · 纽约", "en-US", "America/New_York"),
            ("美国 · 洛杉矶", "en-US", "America/Los_Angeles"),
            ("英国 · 伦敦", "en-GB", "Europe/London"),
            ("法国 · 巴黎", "fr-FR", "Europe/Paris"),
            ("德国 · 柏林", "de-DE", "Europe/Berlin"),
            ("中国 · 上海", "zh-CN", "Asia/Shanghai"),
            ("中国香港", "zh-HK", "Asia/Hong_Kong"),
            ("日本 · 东京", "ja-JP", "Asia/Tokyo"),
            ("韩国 · 首尔", "ko-KR", "Asia/Seoul"),
            ("新加坡", "en-SG", "Asia/Singapore"),
            ("澳大利亚 · 悉尼", "en-AU", "Australia/Sydney"),
        };

        internal static readonly (int Width, int Height)[] Windows = { (1024, 768), (1280, 800), (1366, 768), (1440, 900), (1600, 900), (1920, 1080) };
        internal static readonly (int Width, int Height)[] Viewports = { (960, 640), (1200, 680), (1280, 648), (1360, 780), (1520, 780), (1840, 960) };

        internal static BrowserEnvironment Randomized(BrowserEnvironment template)
        {
            using var random = RandomNumberGenerator.Create();
            var selection = new byte[2];
            // Use only whole groups so all presets have the same probability.
            do { random.GetBytes(selection); }
            while (selection[0] >= 256 - 256 % Regions.Length || selection[1] >= 256 - 256 % Windows.Length);
            var region = Regions[selection[0] % Regions.Length];
            var display = selection[1] % Windows.Length;
            var value = template.Clone();
            (value.Language, value.TimeZone) = (region.Language, region.TimeZone);
            (value.WindowWidth, value.WindowHeight) = Windows[display];
            (value.ViewportWidth, value.ViewportHeight) = Viewports[display];
            return value;
        }

        internal static void Validate(BrowserEnvironment environment)
        {
            if (string.IsNullOrWhiteSpace(environment.Name) || environment.Name.Length > 80)
                throw new InvalidOperationException("请输入长度不超过 80 字符的环境名称。");
            if (environment.Language.Length != 0)
            {
                try { CultureInfo.GetCultureInfo(environment.Language); }
                catch (CultureNotFoundException) { throw new InvalidOperationException("请输入浏览器语言，例如 en-US 或 zh-CN。"); }
                if (!Regex.IsMatch(environment.Language, @"^[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8}){0,3}$"))
                    throw new InvalidOperationException("请输入单个浏览器语言，例如 en-US。");
            }
            if (environment.TimeZone.Length != 0 && !Regex.IsMatch(environment.TimeZone, @"^(UTC|[A-Za-z][A-Za-z0-9_+\-]*(?:/[A-Za-z0-9_+\-]+)+)$"))
                throw new InvalidOperationException("请输入 IANA 时区，例如 Asia/Shanghai。");
            ValidateSize(environment.WindowWidth, environment.WindowHeight, "窗口大小");
            ValidateSize(environment.ViewportWidth, environment.ViewportHeight, "页面视口");
            if (environment.PixelRatio is double ratio && (double.IsNaN(ratio) || ratio < 1 || ratio > 3))
                throw new InvalidOperationException("请选择 1 到 3 之间的设备像素比。");
            if (!Enum.IsDefined(typeof(BrowserEnvironmentNetwork), environment.Network))
                throw new InvalidOperationException("请选择有效的网络配置。");
            foreach (var option in new Enum[] { environment.ColorScheme, environment.Motion, environment.WebRtc, environment.Cookies,
                         environment.Location, environment.Camera, environment.Microphone })
                if (!Enum.IsDefined(option.GetType(), option)) throw new InvalidOperationException("模板含有未知的浏览器环境选项。");
        }

        private static void ValidateSize(int? width, int? height, string name)
        {
            if (width == null && height == null) return;
            if (width is not int w || height is not int h || w < 240 || h < 240 || w > 8192 || h > 8192)
                throw new InvalidOperationException(name + "需要同时填写宽和高，范围为 240–8192 像素。");
        }

        internal static void PrepareProfile(string profile, BrowserEnvironment environment)
        {
            var directory = Path.Combine(profile, "Default");
            Directory.CreateDirectory(directory);
            var preferences = Path.Combine(directory, "Preferences");
            var json = File.Exists(preferences) ? JObject.Parse(File.ReadAllText(preferences)) : new JObject();
            if (environment.Language.Length != 0)
            {
                var intl = json["intl"] as JObject ?? new JObject();
                var primary = environment.Language.Split('-')[0];
                var languages = environment.Language.Equals(primary, StringComparison.OrdinalIgnoreCase)
                    ? primary : environment.Language + "," + primary;
                intl["accept_languages"] = languages;
                intl["selected_languages"] = languages;
                json["intl"] = intl;
            }
            if (environment.WebRtc != BrowserEnvironmentWebRtc.Default)
                Section(json, "webrtc")["ip_handling_policy"] = environment.WebRtc == BrowserEnvironmentWebRtc.PublicInterfaceOnly
                    ? "default_public_interface_only" : "disable_non_proxied_udp";
            if (environment.Cookies == BrowserEnvironmentCookies.BlockThirdParty)
                Section(json, "profile")["cookie_controls_mode"] = 1;
            SetPermission(json, "geolocation", environment.Location);
            SetPermission(json, "media_stream_camera", environment.Camera);
            SetPermission(json, "media_stream_mic", environment.Microphone);
            // Let YTray apply the saved environment before reopening the saved page.
            var session = json["session"] as JObject ?? new JObject();
            session["restore_on_startup"] = 5;
            json["session"] = session;
            var temporary = preferences + ".ytray.tmp";
            try
            {
                File.WriteAllText(temporary, json.ToString(Formatting.None));
                if (File.Exists(preferences)) File.Replace(temporary, preferences, null);
                else File.Move(temporary, preferences);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static void SetPermission(JObject prefs, string name, BrowserEnvironmentPermission permission)
        {
            if (permission != BrowserEnvironmentPermission.Default)
                Section(Section(prefs, "profile"), "default_content_setting_values")[name] = permission == BrowserEnvironmentPermission.Block ? 2 : 3;
        }

        private static JObject Section(JObject parent, string name)
        {
            if (parent[name] is JObject section) return section;
            var value = new JObject();
            parent[name] = value;
            return value;
        }
    }
}
