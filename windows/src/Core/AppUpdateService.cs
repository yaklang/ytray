#nullable enable
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using Newtonsoft.Json;

namespace YTray.Core
{
    internal enum AppUpdatePhase
    {
        Idle,
        Checking,
        UpToDate,
        Available,
        Installing,
        Failed,
    }

    internal sealed class AppReleaseManifest
    {
        [JsonProperty("schema_version")]
        public int SchemaVersion { get; set; }

        [JsonProperty("product")]
        public string Product { get; set; } = "";

        [JsonProperty("version")]
        public string Version { get; set; } = "";

        [JsonProperty("release_notes")]
        public string? ReleaseNotes { get; set; }
        [JsonProperty("release_notes_text")]
        public string? ReleaseNotesText { get; set; }

        [JsonProperty("assets")]
        public AppReleaseAsset[] Assets { get; set; } = Array.Empty<AppReleaseAsset>();
    }

    internal sealed class AppReleaseAsset
    {
        [JsonProperty("platform")]
        public string Platform { get; set; } = "";

        [JsonProperty("architecture")]
        public string Architecture { get; set; } = "";

        [JsonProperty("kind")]
        public string Kind { get; set; } = "";

        [JsonProperty("filename")]
        public string Filename { get; set; } = "";

        [JsonProperty("url")]
        public string Url { get; set; } = "";

        [JsonProperty("sha256")]
        public string SHA256 { get; set; } = "";

        [JsonProperty("size")]
        public long Size { get; set; }
    }

    // The catalog only controls availability hints. WinSparkle authenticates every
    // installation with the embedded Ed25519 key before handing off to Inno Setup.
    internal sealed class AppUpdateService : INotifyPropertyChanged, IDisposable
    {
        internal const string ManifestUrl = "https://aliyun-oss.yaklang.com/ytray/latest.json";
        internal const string InstallerArguments =
            "/SILENT /SP- /NORESTART /NOFORCECLOSEAPPLICATIONS /YTRAYAUTOUPDATE=1";
        internal static readonly TimeSpan DefaultCheckTimeout = TimeSpan.FromSeconds(10);
        internal const int MaximumManifestBytes = 524288;
        private static readonly Lazy<AppUpdateService> LazyShared =
            new Lazy<AppUpdateService>(() => new AppUpdateService());

        private readonly HttpClient _client;
        private readonly TimeSpan _checkTimeout;
        private readonly bool _enabled;
        private NativeAppUpdater? _native;
        private DispatcherTimer? _timer;
        private bool _disposed;
        internal Func<bool> CanInstall { get; set; } = () => false;
        internal Func<bool> AutomaticChecks { get; set; } = () => true;
        internal bool Enabled => _enabled;
        internal DateTime? LastCheck { get; private set; }
        internal string? ReleaseNotesText => _release?.ReleaseNotesText;
        private readonly SemaphoreSlim _operationGate = new SemaphoreSlim(1, 1);
        private AppReleaseManifest? _release;
        private AppReleaseAsset? _asset;
        private AppUpdatePhase _phase;
        private string _statusText;

        internal static AppUpdateService Shared => LazyShared.Value;

        internal AppUpdateService(
            HttpMessageHandler? handler = null,
            TimeSpan? checkTimeout = null,
            bool? enabled = null)
        {
            _client = new HttpClient(handler ?? CreateDefaultHandler());
            _checkTimeout = checkTimeout ?? DefaultCheckTimeout;
            if (_checkTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(checkTimeout));
            _enabled = enabled ?? IsProductionProcess();
            _client.Timeout = _checkTimeout;
            _client.MaxResponseContentBufferSize = MaximumManifestBytes;
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("YTray/" + CurrentVersion);
            _statusText = _enabled ? $"当前版本 v{CurrentVersion}" : "开发与演示环境不检查或安装正式更新";
        }

        internal static HttpClientHandler CreateDefaultHandler() => new HttpClientHandler
        {
            // The OSS/CDN currently returns latest.json as gzip even for clients that do
            // not advertise Accept-Encoding. .NET Framework does not decompress it unless
            // explicitly configured, which otherwise feeds the 1F 8B bytes to Json.NET.
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = false, UseDefaultCredentials = false, AllowAutoRedirect = false,
        };

        public event PropertyChangedEventHandler? PropertyChanged;

        internal string CurrentVersion => YTrayBuildInfo.Version;
        internal string? AvailableVersion => _release?.Version;
        internal AppUpdatePhase Phase => _phase;
        internal string StatusText => _statusText;
        internal bool IsBusy => _phase == AppUpdatePhase.Checking
            || _phase == AppUpdatePhase.Installing;
        internal bool IsUpdateAvailable => _release != null && _asset != null
            && CompareVersions(_release.Version, CurrentVersion) > 0;
        internal string ActionLabel
        {
            get
            {
                switch (_phase)
                {
                    case AppUpdatePhase.Checking: return "正在检查…";
                    case AppUpdatePhase.Installing: return "正在启动安装…";
                    default: return !_enabled ? "开发版不更新" : IsUpdateAvailable ? "更新" : "检查更新";
                }
            }
        }

        internal async Task CheckAsync()
        {
            if (!_enabled || _disposed || IsBusy || !await _operationGate.WaitAsync(0).ConfigureAwait(false)) return;
            try
            {
                SetPhase(AppUpdatePhase.Checking, "正在检查 YTray 更新…");
                using (var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    ManifestUrl))
                using (var timeout = new CancellationTokenSource(_checkTimeout))
                {
                    request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
                    {
                        NoCache = true,
                        NoStore = true,
                    };
                    using (var response = await _client.SendAsync(
                        request,
                        HttpCompletionOption.ResponseContentRead,
                        timeout.Token).ConfigureAwait(false))
                    {
                        if (response.StatusCode != HttpStatusCode.OK) throw new InvalidDataException("更新服务器返回了异常状态");
                        var json = await ReadManifestJsonAsync(response.Content).ConfigureAwait(false);
                        AppReleaseManifest release;
                        try
                        {
                            release = JsonConvert.DeserializeObject<AppReleaseManifest>(json)
                                ?? throw new InvalidDataException("更新清单为空");
                        }
                        catch (JsonException ex)
                        {
                            throw new InvalidDataException("更新服务器返回的数据无法识别", ex);
                        }
                        ValidateManifest(release);
                        var architecture = Environment.Is64BitProcess ? "amd64" : "386";
                        var asset = SelectAsset(release, "windows", architecture, "setup")
                            ?? throw new InvalidDataException($"最新版本没有 Windows {architecture} 安装包");
                        ValidateAsset(asset, release.Version, architecture);

                        _release = release;
                        _asset = asset;
                        LastCheck = DateTime.Now;
                        SetPhase(IsUpdateAvailable ? AppUpdatePhase.Available : AppUpdatePhase.UpToDate,
                            IsUpdateAvailable ? $"发现新版本 v{release.Version} · 当前 v{CurrentVersion}"
                                : $"YTray v{CurrentVersion} 已是最新版本");
                    }
                }
            }
            catch (OperationCanceledException ex)
            {
                DiagnosticLog.Error("app.update.check", ex, "update check timed out");
                SetPhase(AppUpdatePhase.Failed, "检查更新超时，请稍后重试");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Error("app.update.check", ex);
                SetPhase(AppUpdatePhase.Failed, "检查更新失败 · " + UserFacingError(ex));
            }
            finally
            {
                _operationGate.Release();
            }
        }

        private static bool IsProductionProcess()
        {
#if DEBUG
            return false;
#else
            return System.Reflection.Assembly.GetEntryAssembly() == typeof(AppUpdateService).Assembly
                && !Environment.GetCommandLineArgs().Any(a => a.StartsWith("--capture-", StringComparison.Ordinal)
                    || a.StartsWith("--verify-", StringComparison.Ordinal) || a.StartsWith("--smoke-", StringComparison.Ordinal));
#endif
        }

        internal void Start()
        {
            if (!_enabled || _disposed || _timer != null) return;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            _timer.Tick += async (sender, args) =>
            {
                _timer.Interval = TimeSpan.FromHours(6);
                if (AutomaticChecks()) await CheckAsync();
            };
            _timer.Start();
        }

        internal void InstallUpdate()
        {
            if (!_enabled || _disposed || IsBusy) return;
            if (!CanInstall()) { SetPhase(AppUpdatePhase.Failed, "请先完成浏览器启动、组件安装或当前弹窗，再更新 YTray。"); return; }
            if (!File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "unins000.exe"))
                && MessageBox.Show("当前为便携版。更新将安装到本机应用目录，保留实例和配置；原便携目录不会删除。", "更新 YTray？",
                    MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
            try
            {
                if (_native == null) _native = new NativeAppUpdater(CurrentVersion, CanInstall,
                    (result, message) =>
                    {
                        if (result == NativeUpdateResult.Current) { _release = null; _asset = null; }
                        SetPhase(result == NativeUpdateResult.Failed ? AppUpdatePhase.Failed
                            : result == NativeUpdateResult.Current ? AppUpdatePhase.UpToDate : AppUpdatePhase.Idle, message);
                    });
                SetPhase(AppUpdatePhase.Installing, "正在下载并校验，安装完成后会重新打开 YTray。");
                _native.Install();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Error("app.update.native", ex);
                SetPhase(AppUpdatePhase.Failed, "更新组件不可用，请重试或手动下载。");
            }
        }

        internal void OpenDownloads()
        {
            var version = IsUpdateAvailable ? AvailableVersion! : CurrentVersion;
            var architecture = Environment.Is64BitProcess ? "amd64" : "386";
            try { Process.Start(new ProcessStartInfo($"https://aliyun-oss.yaklang.com/ytray/{version}/YTray-{version}-windows-{architecture}-setup.exe") { UseShellExecute = true }); }
            catch (Exception ex) { SetPhase(AppUpdatePhase.Failed, "无法打开浏览器：" + ex.Message); }
        }

        private static async Task<string> ReadManifestJsonAsync(HttpContent content)
        {
            var payload = await content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (payload.Length == 0) throw new InvalidDataException("更新清单为空");
            if (payload.Length > MaximumManifestBytes)
                throw new InvalidDataException("更新清单超过允许的大小");

            // AutomaticDecompression is the normal path. Keep a small payload-level
            // fallback for proxies/CDNs that preserve Content-Encoding incorrectly or
            // return gzip bytes without the header.
            var isGzip = payload.Length >= 2 && payload[0] == 0x1f && payload[1] == 0x8b;
            var isDeflate = content.Headers.ContentEncoding.Any(value =>
                string.Equals(value, "deflate", StringComparison.OrdinalIgnoreCase));
            if (isGzip || isDeflate)
                payload = DecompressManifest(payload, isGzip);

            var json = Encoding.UTF8.GetString(payload).TrimStart('\uFEFF');
            var first = json.FirstOrDefault(character => !char.IsWhiteSpace(character));
            if (first != '{')
                throw new InvalidDataException("更新服务器返回的数据无法识别");
            return json;
        }

        private static byte[] DecompressManifest(byte[] payload, bool gzip)
        {
            using (var source = new MemoryStream(payload, writable: false))
            using (var decoder = gzip
                ? (Stream)new GZipStream(source, CompressionMode.Decompress)
                : new DeflateStream(source, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                var buffer = new byte[8192];
                int read;
                while ((read = decoder.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (output.Length + read > MaximumManifestBytes)
                        throw new InvalidDataException("解压后的更新清单超过允许的大小");
                    output.Write(buffer, 0, read);
                }
                return output.ToArray();
            }
        }

        private static void ValidateManifest(AppReleaseManifest release)
        {
            if (release.SchemaVersion != 1 || release.Product != "ytray" || release.Version == null
                || !Regex.IsMatch(release.Version, @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z")
                || !Version.TryParse(release.Version, out _) || release.Assets == null
                || (release.ReleaseNotes != null && release.ReleaseNotes != "https://github.com/yaklang/ytray/releases/tag/v" + release.Version)
                || (release.ReleaseNotesText?.Length ?? 0) > 32000)
                throw new InvalidDataException("更新清单格式无效");
        }

        private static void ValidateAsset(AppReleaseAsset asset, string version, string architecture)
        {
            var filename = $"YTray-{version}-windows-{architecture}-setup.exe";
            if (asset.Filename != filename || asset.Url != $"https://aliyun-oss.yaklang.com/ytray/{version}/{filename}"
                || asset.Size <= 0 || asset.Size >= 536870912 || asset.SHA256 == null
                || !Regex.IsMatch(asset.SHA256, @"\A[a-f0-9]{64}\z"))
                throw new InvalidDataException("更新包校验信息无效");
        }

        internal static AppReleaseAsset? SelectAsset(AppReleaseManifest release, string platform, string architecture, string kind)
        {
            var matches = release.Assets.Where(a => a != null && a.Platform == platform
                && a.Architecture == architecture && a.Kind == kind).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }

        internal static int CompareVersions(string left, string right)
        {
            var lhs = ParsedVersion.Parse(left);
            var rhs = ParsedVersion.Parse(right);
            for (var index = 0; index < Math.Max(lhs.Core.Length, rhs.Core.Length); index++)
            {
                var comparison = Part(lhs.Core, index).CompareTo(Part(rhs.Core, index));
                if (comparison != 0) return comparison;
            }
            if (lhs.PreRelease.Length == 0 || rhs.PreRelease.Length == 0)
            {
                if (lhs.PreRelease.Length == rhs.PreRelease.Length) return 0;
                return lhs.PreRelease.Length == 0 ? 1 : -1;
            }
            for (var index = 0; index < Math.Max(lhs.PreRelease.Length, rhs.PreRelease.Length); index++)
            {
                if (index >= lhs.PreRelease.Length) return -1;
                if (index >= rhs.PreRelease.Length) return 1;
                var comparison = CompareIdentifier(lhs.PreRelease[index], rhs.PreRelease[index]);
                if (comparison != 0) return comparison;
            }
            return 0;
        }

        private static int CompareIdentifier(string left, string right)
        {
            var leftNumeric = int.TryParse(left, out var leftNumber);
            var rightNumeric = int.TryParse(right, out var rightNumber);
            if (leftNumeric && rightNumeric) return leftNumber.CompareTo(rightNumber);
            if (leftNumeric != rightNumeric) return leftNumeric ? -1 : 1;
            var leftSuffix = NumericSuffix(left);
            var rightSuffix = NumericSuffix(right);
            if (leftSuffix.HasValue && rightSuffix.HasValue
                && string.Equals(leftSuffix.Value.Prefix, rightSuffix.Value.Prefix, StringComparison.OrdinalIgnoreCase))
                return leftSuffix.Value.Number.CompareTo(rightSuffix.Value.Number);
            return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static (string Prefix, int Number)? NumericSuffix(string value)
        {
            var index = value.Length;
            while (index > 0 && char.IsDigit(value[index - 1])) index--;
            if (index == value.Length || !int.TryParse(value.Substring(index), out var number)) return null;
            return (value.Substring(0, index), number);
        }

        private static int Part(int[] values, int index) => index < values.Length ? values[index] : 0;

        private sealed class ParsedVersion
        {
            internal int[] Core { get; private set; } = Array.Empty<int>();
            internal string[] PreRelease { get; private set; } = Array.Empty<string>();

            internal static ParsedVersion Parse(string value)
            {
                var normalized = (value ?? "").Trim().TrimStart('v', 'V');
                var metadata = normalized.IndexOf('+');
                if (metadata >= 0) normalized = normalized.Substring(0, metadata);
                var separator = normalized.IndexOf('-');
                var core = separator >= 0 ? normalized.Substring(0, separator) : normalized;
                var prerelease = separator >= 0 ? normalized.Substring(separator + 1) : "";
                var coreParts = core.Split('.');
                var parsed = new int[coreParts.Length];
                for (var index = 0; index < coreParts.Length; index++)
                {
                    if (!int.TryParse(coreParts[index], out parsed[index]) || parsed[index] < 0)
                        throw new FormatException("无法识别版本号 " + value);
                }
                return new ParsedVersion
                {
                    Core = parsed,
                    PreRelease = string.IsNullOrWhiteSpace(prerelease)
                        ? Array.Empty<string>()
                        : prerelease.Split('.'),
                };
            }
        }

        private void SetPhase(AppUpdatePhase phase, string status)
        {
            _phase = phase;
            _statusText = status;
            DiagnosticLog.Info("app.update", $"phase={phase}; status={status}");
            OnPropertyChanged(nameof(Phase));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(IsUpdateAvailable));
            OnPropertyChanged(nameof(ActionLabel));
            OnPropertyChanged(nameof(AvailableVersion));
        }

        private void OnPropertyChanged(string name)
        {
            var handlers = PropertyChanged;
            if (handlers == null) return;
            var args = new PropertyChangedEventArgs(name);
            foreach (PropertyChangedEventHandler handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, args);
                }
                catch (Exception ex)
                {
                    // A presentation observer must never break the updater state machine or
                    // strand the UI in Checking/Installing. Preserve diagnostics and keep
                    // notifying the remaining observers.
                    CrashGuard.Record("app-update-property-changed:" + name, ex);
                }
            }
        }

        private static string UserFacingError(Exception error)
        {
            if (error is OperationCanceledException) return "请求已取消或超时";
            if (error is Win32Exception native && native.NativeErrorCode == 1223) return "已取消管理员授权";
            if (error is JsonException) return "更新服务器返回的数据无法识别";
            return error.Message;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Stop();
            _native?.Dispose();
            _client.Dispose();
            // An in-flight check still owns the semaphore and releases it in finally.
        }
    }
}
