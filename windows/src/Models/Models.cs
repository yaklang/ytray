#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace YTray.Models
{
    public enum RuntimeSource
    {
        Managed,
        Local,
        System
    }

    public static class RuntimeSourceExtensions
    {
        public static string Title(this RuntimeSource s) => s switch
        {
            RuntimeSource.Managed => "YTray 安装",
            RuntimeSource.Local => "自定义路径",
            RuntimeSource.System => "系统环境",
            _ => s.ToString(),
        };
    }

    public enum BrowserKind
    {
        Chrome,
        ChromeBeta,
        ChromeCanary,
        ChromeForTesting,
        Chromium,
        Edge,
    }

    public static class BrowserKindExtensions
    {
        public static string Title(this BrowserKind k) => k switch
        {
            BrowserKind.Chrome => "Google Chrome",
            BrowserKind.ChromeBeta => "Chrome Beta",
            BrowserKind.ChromeCanary => "Chrome Canary",
            BrowserKind.ChromeForTesting => "Chrome for Testing",
            BrowserKind.Chromium => "Chromium",
            BrowserKind.Edge => "Microsoft Edge",
            _ => k.ToString(),
        };

        // Windows has no SF Symbols; these are rough glyph stand-ins used by custom controls.
        public static string Glyph(this BrowserKind k) => k switch
        {
            BrowserKind.Edge => "\uE950", // wave
            BrowserKind.Chromium => "\uE73C", // circle hex
            _ => "\uE774", // globe
        };

        public static BrowserKind Infer(string? name, string? path)
        {
            var value = ((name ?? "") + " " + (path ?? "")).ToLowerInvariant();
            if (value.Contains("for testing")) return BrowserKind.ChromeForTesting;
            if (value.Contains("microsoft edge") || value.Contains("\\edge") || value.Contains("/edge")) return BrowserKind.Edge;
            if (value.Contains("canary")) return BrowserKind.ChromeCanary;
            if (value.Contains("beta")) return BrowserKind.ChromeBeta;
            if (value.Contains("chromium")) return BrowserKind.Chromium;
            return BrowserKind.Chrome;
        }
    }

    public class BrowserRuntime : IEquatable<BrowserRuntime>
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        public string Architecture { get; set; } = "";
        public string ExecutablePath { get; set; } = "";
        public RuntimeSource Source { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public BrowserKind? BrowserKind { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        [JsonIgnore]
        public BrowserKind Kind => BrowserKind ?? BrowserKindExtensions.Infer(Name, ExecutablePath);
        [JsonIgnore]
        public string DisplayTitle => Kind.Title();
        [JsonIgnore]
        public string VersionLabel => string.IsNullOrEmpty(Version) ? "版本未知" : Version;
        [JsonIgnore]
        public bool IsSystemEnvironment =>
            Source == RuntimeSource.System
            || ExecutablePath.StartsWith(@"C:\Program Files\", StringComparison.OrdinalIgnoreCase)
            || ExecutablePath.StartsWith(@"C:\Program Files (x86)\", StringComparison.OrdinalIgnoreCase);

        public bool Equals(BrowserRuntime? other) => other != null && Id == other.Id;
        public override bool Equals(object? obj) => obj is BrowserRuntime r && Equals(r);
        public override int GetHashCode() => Id.GetHashCode();
        public override string ToString() => $"{DisplayTitle} {VersionLabel}";
    }

    public class BrowserPlugin : IEquatable<BrowserPlugin>
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        public string Path { get; set; } = "";
        public string IconPath { get; set; } = "";
        public int ManifestVersion { get; set; }
        public bool Enabled { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public bool Equals(BrowserPlugin? other) => other != null && Id == other.Id;
        public override bool Equals(object? obj) => obj is BrowserPlugin plugin && Equals(plugin);
        public override int GetHashCode() => Id.GetHashCode();
    }

    public class ProxyPreset : IEquatable<ProxyPreset>
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Server { get; set; } = "";
        public string Remark { get; set; } = "";
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public DateTime LastUsedAt { get; set; } = DateTime.Now;

        public ProxyPreset() { }

        public ProxyPreset(string? server, string? remark, string? username = "", string? password = "", DateTime? lastUsedAt = null)
        {
            Id = Guid.NewGuid();
            Server = server ?? "";
            Remark = remark ?? "";
            Username = username ?? "";
            Password = password ?? "";
            LastUsedAt = lastUsedAt ?? DateTime.Now;
        }

        public bool Equals(ProxyPreset? other) => other != null && Id == other.Id;
        public override bool Equals(object? obj) => obj is ProxyPreset preset && Equals(preset);
        public override int GetHashCode() => Id.GetHashCode();
    }

    public enum ProxyScheme
    {
        Http,
        Https,
    }

    public static class ProxySchemeExtensions
    {
        public static string Title(this ProxyScheme s) => s switch
        {
            ProxyScheme.Http => "HTTP",
            ProxyScheme.Https => "HTTPS",
            _ => s.ToString(),
        };
        public static int DefaultPort(this ProxyScheme s) => s == ProxyScheme.Https ? 443 : 80;
        public static string Raw(this ProxyScheme s) => s == ProxyScheme.Https ? "https" : "http";
    }

    public struct ProxyEndpoint
    {
        public ProxyScheme Scheme;
        public string Host;
        public int Port;
        public string Server;
    }

    public static class HTTPProxyAddress
    {
        public static string Build(ProxyScheme scheme, string? host, int port)
        {
            var normalizedHost = (host ?? "").Trim();
            if (normalizedHost.StartsWith("[") && normalizedHost.EndsWith("]"))
            {
                normalizedHost = normalizedHost.Substring(1, normalizedHost.Length - 2);
            }
            if (string.IsNullOrEmpty(normalizedHost)
                || normalizedHost.Contains("://")
                || normalizedHost.Contains("/")
                || normalizedHost.Contains("@")
                || port < 1 || port > 65535)
                throw new YTrayException(YTrayError.InvalidProxy, $"{scheme.Raw()}://{host}:{port}");
            return $"{scheme.Raw()}://{normalizedHost}:{port}";
        }

        public static ProxyEndpoint Split(string? value)
        {
            var normalized = Normalize(value);
            var uri = new Uri(normalized);
            var rawScheme = uri.Scheme.ToLowerInvariant();
            ProxyScheme scheme = rawScheme == "https" ? ProxyScheme.Https : ProxyScheme.Http;
            var host = uri.Host;
            if (host.StartsWith("[") && host.EndsWith("]"))
                host = host.Substring(1, host.Length - 2);
            return new ProxyEndpoint
            {
                Scheme = scheme,
                Host = host,
                Port = uri.Port == -1 ? scheme.DefaultPort() : uri.Port,
                Server = normalized,
            };
        }

        public static string Normalize(string? value)
        {
            var candidate = (value ?? "").Trim();
            if (string.IsNullOrEmpty(candidate)) throw new YTrayException(YTrayError.InvalidProxy, value);
            if (!candidate.Contains("://")) candidate = "http://" + candidate;
            var uri = new Uri(candidate);
            var scheme = uri.Scheme.ToLowerInvariant();
            if (scheme != "http" && scheme != "https")
                throw new YTrayException(YTrayError.InvalidProxy, value);
            if (string.IsNullOrEmpty(uri.Host))
                throw new YTrayException(YTrayError.InvalidProxy, value);
            if (!string.IsNullOrEmpty(uri.UserInfo))
                throw new YTrayException(YTrayError.InvalidProxy, value);
            if (uri.Port != -1 && (uri.Port < 1 || uri.Port > 65535))
                throw new YTrayException(YTrayError.InvalidProxy, value);
            var rebuilt = $"{scheme}://{uri.Host}";
            if (!uri.IsDefaultPort) rebuilt += ":" + uri.Port;
            return rebuilt;
        }
    }

    public enum LaunchMode
    {
        Quick,
        Isolated,
        Custom,
    }

    public static class LaunchModeExtensions
    {
        public static string Title(this LaunchMode m) => m switch
        {
            LaunchMode.Quick => "快速启动",
            LaunchMode.Isolated => "快速启动",
            LaunchMode.Custom => "自定义启动",
            _ => m.ToString(),
        };
    }

    public enum AppThemePreference
    {
        System,
        Light,
        Dark,
    }

    public class LaunchSettings
    {
        public const int CurrentConfigurationVersion = 5;
        public const int CertificateDefaultMigrationVersion = 2;
        public const string DefaultPresetProxyServer = "http://127.0.0.1:8083";

        public int ConfigurationVersion { get; set; } = CurrentConfigurationVersion;
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public Guid? DefaultRuntimeID { get; set; }
        public string HomeURL { get; set; } = "chrome://newtab";
        public string ProxyServer { get; set; } = "";
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string ProxyUsername { get; set; } = "";
        [JsonIgnore]
        public string ProxyPassword { get; set; } = "";
        public string PresetProxyServer { get; set; } = DefaultPresetProxyServer;
        public ProxyScheme PresetProxyScheme { get; set; } = ProxyScheme.Http;
        public string PresetProxyHost { get; set; } = "127.0.0.1";
        public int PresetProxyPort { get; set; } = 8083;
        public string PresetProxyUsername { get; set; } = "";
        public string PresetProxyPassword { get; set; } = "";
        public string PresetProxyRemark { get; set; } = "";
        public string PresetProxyCheckTarget { get; set; } = "";
        public List<ProxyPreset> RecentProxyPresets { get; set; } = new List<ProxyPreset>();
        public int DebugPort { get; set; } = 9222;
        public bool RestrictWebRTC { get; set; } = true;
        public bool DisableNotifications { get; set; } = true;
        public bool IgnoreCertificateErrors { get; set; } = true;
        public string AdditionalFlags { get; set; } = "";
        public List<Guid> DefaultPluginIDs { get; set; } = new List<Guid>();
        public string DockBadge { get; set; } = "";
        public bool EdgeDockEnabled { get; set; } = true;
        public bool EdgeDockOnLeft { get; set; }
        public int EdgeDockYPercent { get; set; } = 58;
        public AppThemePreference ThemePreference { get; set; } = AppThemePreference.System;
        public bool LaunchAtLoginSetupCompleted { get; set; }

        public static readonly string[] BlockedCustomPrefixes =
        {
            "--user-data-dir", "--remote-debugging-address", "--remote-debugging-port",
            "--load-extension", "--disable-extensions-except",
            "--proxy-server", "--no-proxy-server", "--proxy-pac-url",
            "--proxy-auto-detect", "--proxy-bypass-list",
        };

        [JsonConstructor]
        public LaunchSettings() { }

        public LaunchSettings(Guid? defaultRuntimeID)
        {
            DefaultRuntimeID = defaultRuntimeID;
        }

        /// <summary>
        /// Returns an ownership-safe snapshot. Launch workflows may edit this copy without
        /// mutating the global settings or a historical instance through a shared reference.
        /// </summary>
        public LaunchSettings Clone()
        {
            return new LaunchSettings
            {
                ConfigurationVersion = ConfigurationVersion,
                DefaultRuntimeID = DefaultRuntimeID,
                HomeURL = HomeURL ?? "chrome://newtab",
                ProxyServer = ProxyServer ?? "",
                ProxyUsername = ProxyUsername ?? "",
                ProxyPassword = ProxyPassword ?? "",
                PresetProxyServer = PresetProxyServer ?? DefaultPresetProxyServer,
                PresetProxyScheme = PresetProxyScheme,
                PresetProxyHost = PresetProxyHost ?? "127.0.0.1",
                PresetProxyPort = PresetProxyPort,
                PresetProxyUsername = PresetProxyUsername ?? "",
                PresetProxyPassword = PresetProxyPassword ?? "",
                PresetProxyRemark = PresetProxyRemark ?? "",
                PresetProxyCheckTarget = PresetProxyCheckTarget ?? "",
                RecentProxyPresets = (RecentProxyPresets ?? new List<ProxyPreset>())
                    .Where(preset => preset != null)
                    .Select(preset => new ProxyPreset(preset.Server ?? "", preset.Remark ?? "",
                        preset.Username ?? "", preset.Password ?? "", preset.LastUsedAt)
                    {
                        Id = preset.Id,
                    })
                    .ToList(),
                DebugPort = DebugPort,
                RestrictWebRTC = RestrictWebRTC,
                DisableNotifications = DisableNotifications,
                IgnoreCertificateErrors = IgnoreCertificateErrors,
                AdditionalFlags = AdditionalFlags ?? "",
                DefaultPluginIDs = (DefaultPluginIDs ?? new List<Guid>()).ToList(),
                DockBadge = DockBadge ?? "",
                EdgeDockEnabled = EdgeDockEnabled,
                EdgeDockOnLeft = EdgeDockOnLeft,
                EdgeDockYPercent = EdgeDockYPercent,
                ThemePreference = ThemePreference,
                LaunchAtLoginSetupCompleted = LaunchAtLoginSetupCompleted,
            };
        }

        // Migration hook: if loaded JSON lacks IgnoreCertificateErrors and the saved version
        // is below the certificate migration threshold, default it to true (matches macOS behavior).
        public void ApplyCertificateMigration(int savedVersion, bool? savedIgnore)
        {
            IgnoreCertificateErrors = savedVersion < CertificateDefaultMigrationVersion
                ? true
                : (savedIgnore ?? true);
        }
    }

    public enum InstanceStatus
    {
        Running,
        Stopped,
        Failed,
    }

    public static class InstanceStatusExtensions
    {
        public static string Title(this InstanceStatus s) => s switch
        {
            InstanceStatus.Running => "运行中",
            InstanceStatus.Stopped => "已停止",
            InstanceStatus.Failed => "启动失败",
            _ => s.ToString(),
        };
    }

    public enum BrowserLaunchPhase
    {
        Idle,
        Preparing,
        Waiting,
        Succeeded,
    }

    public enum ProxyCheckPhase
    {
        Idle,
        Checking,
        Success,
        Failure,
    }

    public class BrowserInstance : IEquatable<BrowserInstance>
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "";
        public Guid RuntimeID { get; set; }
        public string RuntimeName { get; set; } = "";
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? RuntimeVersion { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public BrowserKind? RuntimeKind { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public RuntimeSource? RuntimeSource { get; set; }
        public LaunchMode Mode { get; set; }
        public int ProcessID { get; set; }
        public int DebugPort { get; set; }
        public string ProfilePath { get; set; } = "";
        public string StartURL { get; set; } = "";
        public DateTime StartedAt { get; set; } = DateTime.Now;
        public InstanceStatus Status { get; set; } = InstanceStatus.Running;
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? LastScreenshotPath { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? ThumbnailPath { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public DateTime? ThumbnailUpdatedAt { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? LastPageTitle { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? LastPageURL { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? DockBadge { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public LaunchSettings? SettingsSnapshot { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public List<Guid> PluginIDs { get; set; } = new List<Guid>();
        /// <summary>Windows-specific: resolved AppUserModelID used for taskbar grouping.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? AppUserModelId { get; set; }

        // Transient interaction state. These fields intentionally stay out of state.json; they
        // let both the manager and the floating panel show immediate, consistent action feedback.
        [JsonIgnore]
        public bool IsCapturing { get; set; }
        [JsonIgnore]
        public bool IsStopping { get; set; }
        [JsonIgnore]
        public string? PreviewError { get; set; }

        public bool Equals(BrowserInstance? other) => other != null && Id == other.Id;
        public override bool Equals(object? obj) => obj is BrowserInstance instance && Equals(instance);
        public override int GetHashCode() => Id.GetHashCode();
    }

    public class PersistedState
    {
        public List<BrowserRuntime> Runtimes { get; set; } = new List<BrowserRuntime>();
        public List<BrowserPlugin> Plugins { get; set; } = new List<BrowserPlugin>();
        public List<BrowserInstance> Instances { get; set; } = new List<BrowserInstance>();
        public LaunchSettings Settings { get; set; } = new LaunchSettings();
    }

    public class MirrorManifest
    {
        [JsonProperty("schema_version")]
        public int SchemaVersion { get; set; }
        public string Product { get; set; } = "";
        public string Channel { get; set; } = "";
        [JsonProperty("generated_at")]
        public string GeneratedAt { get; set; } = "";
        public List<MirrorVersion> Versions { get; set; } = new List<MirrorVersion>();
    }

    public class MirrorVersion
    {
        public string Version { get; set; } = "";
        public List<MirrorArtifact> Artifacts { get; set; } = new List<MirrorArtifact>();

        [JsonIgnore]
        public string PlatformLabel
        {
            get
            {
                var labels = (Artifacts ?? new List<MirrorArtifact>())
                    .Where(artifact => !string.IsNullOrWhiteSpace(artifact.OS) && !string.IsNullOrWhiteSpace(artifact.Arch))
                    .Select(artifact => artifact.OS + "-" + artifact.Arch)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return labels.Count == 0 ? "Windows" : string.Join(" / ", labels);
            }
        }

        public override string ToString() => string.IsNullOrWhiteSpace(Version) ? "未知版本" : Version;
    }

    public class MirrorArtifact
    {
        public string OS { get; set; } = "";
        [JsonProperty("arch")]
        public string Arch { get; set; } = "";
        public string Format { get; set; } = "";
        public string Url { get; set; } = "";
        public long? Size { get; set; }
        public string Sha256 { get; set; } = "";
    }

    public class PluginManifest
    {
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        [JsonProperty("manifest_version")]
        public int ManifestVersion { get; set; }
    }

    public class ExtensionManifest
    {
        public string Latest { get; set; } = "";
        [JsonProperty("updated_at")]
        public string UpdatedAt { get; set; } = "";
        public List<ExtensionReleaseVersion> Versions { get; set; } = new List<ExtensionReleaseVersion>();
    }

    public class ExtensionReleaseVersion
    {
        public string Version { get; set; } = "";
        [JsonProperty("published_at")]
        public string PublishedAt { get; set; } = "";
        public string Commit { get; set; } = "";
        public List<ExtensionArtifact> Artifacts { get; set; } = new List<ExtensionArtifact>();

        public override string ToString() => string.IsNullOrWhiteSpace(Version) ? "未知版本" : Version;
    }

    public class ExtensionArtifact
    {
        public string Variant { get; set; } = "";
        public string Browser { get; set; } = "";
        public string Mode { get; set; } = "";
        public string Filename { get; set; } = "";
        public string Url { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public long? Size { get; set; }
        [JsonProperty("checksum_url")]
        public string ChecksumUrl { get; set; } = "";
    }

    public enum YTrayError
    {
        NoRuntime,
        InvalidExecutable,
        InvalidPlugin,
        InvalidURL,
        InvalidProxy,
        InvalidFlag,
        LaunchFailed,
        RuntimeManifestFailed,
        DownloadFailed,
        ExtensionInstallFailed,
        ScreenshotFailed,
    }

    public class YTrayException : Exception
    {
        public YTrayError Error { get; }
        public YTrayException(YTrayError error, string? message) : base(Message(error, message)) { Error = error; }
        public YTrayException(YTrayError error, string? message, Exception innerException)
            : base(Message(error, message), innerException) { Error = error; }

        public static new string Message(YTrayError error, string? detail) => error switch
        {
            YTrayError.NoRuntime => "没有可用浏览器；请选择本机浏览器，或安装一个 Chrome for Testing 版本",
            YTrayError.InvalidExecutable => $"找不到可执行的 Chrome：{detail}",
            YTrayError.InvalidPlugin => $"插件目录无效：{detail}（目录根部必须包含 manifest.json）",
            YTrayError.InvalidURL => $"启动地址无效：{detail}",
            YTrayError.InvalidProxy => $"HTTP 代理地址无效：{detail}（例如 http://127.0.0.1:8083）",
            YTrayError.InvalidFlag => $"不允许覆盖实例隔离或调试参数：{detail}",
            YTrayError.LaunchFailed => $"浏览器启动失败：{detail}",
            YTrayError.RuntimeManifestFailed => $"获取浏览器版本失败：{detail}",
            YTrayError.DownloadFailed => $"运行时安装失败：{detail}",
            YTrayError.ExtensionInstallFailed => $"插件下载失败：{detail}",
            YTrayError.ScreenshotFailed => $"快速截图失败：{detail}",
            _ => detail ?? "",
        };
    }
}
