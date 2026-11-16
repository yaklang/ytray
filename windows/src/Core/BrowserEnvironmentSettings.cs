#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YTray.Models;

namespace YTray.Core
{
    internal static class BrowserEnvironmentSettings
    {
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
            if (!Enum.IsDefined(typeof(BrowserEnvironmentNetwork), environment.Network))
                throw new InvalidOperationException("请选择有效的网络配置。");
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
    }
}
