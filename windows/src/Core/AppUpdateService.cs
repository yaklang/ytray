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
using Newtonsoft.Json;

namespace YTray.Core
{
    internal enum AppUpdatePhase
    {
        Idle,
        Checking,
        UpToDate,
        Available,
        Downloading,
        Downloaded,
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

    /// <summary>
    /// Downloads an immutable, versioned YTray installer from the public OSS release manifest.
    /// The installer is never executed until its exact byte count and SHA-256 both match the
    /// manifest. The official Inno Setup package performs the privileged replacement after the
    /// current process exits and relaunches YTray when installation completes.
    /// </summary>
    internal sealed class AppUpdateService : INotifyPropertyChanged, IDisposable
    {
        internal const string ManifestUrl = "https://aliyun-oss.yaklang.com/ytray/latest.json";
        internal const string InstallerArguments =
            "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /YTRAYAUTOUPDATE=1";
        internal static readonly TimeSpan DefaultCheckTimeout = TimeSpan.FromSeconds(10);
        private const int MaximumManifestBytes = 2 * 1024 * 1024;
        private static readonly Lazy<AppUpdateService> LazyShared =
            new Lazy<AppUpdateService>(() => new AppUpdateService());

        private readonly HttpClient _client;
        private readonly TimeSpan _checkTimeout;
        private readonly string _updatesDirectory;
        private readonly SemaphoreSlim _operationGate = new SemaphoreSlim(1, 1);
        private AppReleaseManifest? _release;
        private AppReleaseAsset? _asset;
        private string? _downloadedInstaller;
        private AppUpdatePhase _phase;
        private string _statusText;
        private int _downloadPercent;

        internal static AppUpdateService Shared => LazyShared.Value;

        internal AppUpdateService(
            HttpMessageHandler? handler = null,
            TimeSpan? checkTimeout = null,
            string? updatesDirectory = null)
        {
            _client = new HttpClient(handler ?? CreateDefaultHandler());
            _checkTimeout = checkTimeout ?? DefaultCheckTimeout;
            if (_checkTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(checkTimeout));
            _updatesDirectory = string.IsNullOrWhiteSpace(updatesDirectory)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "YTray",
                    "Updates")
                : Path.GetFullPath(updatesDirectory!);
            // Installer downloads may legitimately take a long time. Manifest checks use
            // their own short cancellation budget in CheckAsync so a blocked OSS endpoint
            // cannot leave the settings UI in the Checking phase for twenty minutes.
            _client.Timeout = TimeSpan.FromMinutes(20);
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("YTray/" + CurrentVersion);
            _statusText = $"当前版本 v{CurrentVersion}";
        }

        internal static HttpClientHandler CreateDefaultHandler() => new HttpClientHandler
        {
            // The OSS/CDN currently returns latest.json as gzip even for clients that do
            // not advertise Accept-Encoding. .NET Framework does not decompress it unless
            // explicitly configured, which otherwise feeds the 1F 8B bytes to Json.NET.
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };

        public event PropertyChangedEventHandler? PropertyChanged;

        internal string CurrentVersion => YTrayBuildInfo.Version;
        internal string? AvailableVersion => _release?.Version;
        internal AppUpdatePhase Phase => _phase;
        internal string StatusText => _statusText;
        internal int DownloadPercent => _downloadPercent;
        internal bool IsBusy => _phase == AppUpdatePhase.Checking
            || _phase == AppUpdatePhase.Downloading
            || _phase == AppUpdatePhase.Installing;
        internal bool IsUpdateAvailable => _release != null && _asset != null
            && CompareVersions(_release.Version, CurrentVersion) > 0;
        internal bool IsDownloaded => _phase == AppUpdatePhase.Downloaded
            && !string.IsNullOrWhiteSpace(_downloadedInstaller)
            && File.Exists(_downloadedInstaller);

        internal string ActionLabel
        {
            get
            {
                switch (_phase)
                {
                    case AppUpdatePhase.Checking: return "正在检查…";
                    case AppUpdatePhase.Downloading: return $"下载中 {_downloadPercent}%";
                    case AppUpdatePhase.Installing: return "正在启动安装…";
                    case AppUpdatePhase.Downloaded: return "立即安装";
                    default: return IsUpdateAvailable ? "下载并安装" : "检查更新";
                }
            }
        }

        internal async Task CheckAsync()
        {
            if (!await _operationGate.WaitAsync(0).ConfigureAwait(false)) return;
            try
            {
                SetPhase(AppUpdatePhase.Checking, "正在检查 YTray 更新…");
                using (var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    ManifestUrl + "?app_update=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
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
                        response.EnsureSuccessStatusCode();
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
                        ValidateAsset(asset);

                        _release = release;
                        _asset = asset;
                        _downloadedInstaller = ExistingVerifiedDownload(release, asset);
                        if (CompareVersions(release.Version, CurrentVersion) > 0)
                        {
                            if (_downloadedInstaller != null)
                                SetPhase(AppUpdatePhase.Downloaded, $"YTray v{release.Version} 已下载并校验，可以安装");
                            else
                                SetPhase(AppUpdatePhase.Available, $"发现新版本 v{release.Version} · 当前 v{CurrentVersion}");
                        }
                        else
                        {
                            _downloadedInstaller = null;
                            SetPhase(AppUpdatePhase.UpToDate, $"YTray v{CurrentVersion} 已是最新版本");
                        }
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

        internal async Task<bool> DownloadAsync()
        {
            if (!IsUpdateAvailable || _release == null || _asset == null) return false;
            if (!await _operationGate.WaitAsync(0).ConfigureAwait(false)) return false;
            var release = _release;
            var asset = _asset;
            string? partialPath = null;
            try
            {
                ValidateAsset(asset);
                var destination = DownloadPath(release, asset);
                partialPath = destination + ".part";
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                TryDelete(partialPath);
                SetDownloadProgress(0);
                SetPhase(AppUpdatePhase.Downloading, $"正在下载 YTray v{release.Version}…");

                using (var request = new HttpRequestMessage(HttpMethod.Get, asset.Url))
                {
                    request.Headers.AcceptEncoding.Clear();
                    using (var response = await _client.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                    {
                        response.EnsureSuccessStatusCode();
                        using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var output = new FileStream(
                            partialPath,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None,
                            81920,
                            useAsync: true))
                        {
                            var buffer = new byte[81920];
                            long received = 0;
                            int read;
                            while ((read = await source.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                            {
                                await output.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                                received += read;
                                if (asset.Size > 0)
                                    SetDownloadProgress((int)Math.Min(100, received * 100 / asset.Size));
                            }
                            await output.FlushAsync().ConfigureAwait(false);
                        }
                    }
                }

                VerifyFile(partialPath, asset);
                TryDelete(destination);
                File.Move(partialPath, destination);
                partialPath = null;
                _downloadedInstaller = destination;
                SetDownloadProgress(100);
                SetPhase(AppUpdatePhase.Downloaded, $"YTray v{release.Version} 下载完成，校验已通过");
                return true;
            }
            catch (Exception ex)
            {
                if (partialPath != null) TryDelete(partialPath);
                DiagnosticLog.Error("app.update.download", ex);
                SetPhase(AppUpdatePhase.Failed, "下载更新失败 · " + UserFacingError(ex));
                return false;
            }
            finally
            {
                _operationGate.Release();
            }
        }

        internal bool StartInstaller()
        {
            try
            {
                var asset = _asset;
                var installer = _downloadedInstaller;
                if (asset == null || string.IsNullOrWhiteSpace(installer))
                    throw new InvalidOperationException("安装包尚未下载");
                VerifyFile(installer!, asset);
                SetPhase(AppUpdatePhase.Installing, "正在启动安装程序，YTray 将自动重启…");
                var process = Process.Start(CreateInstallerStartInfo(installer!));
                if (process == null) throw new InvalidOperationException("无法启动安装程序");
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Error("app.update.install", ex);
                SetPhase(AppUpdatePhase.Failed, "无法安装更新 · " + UserFacingError(ex));
                return false;
            }
        }

        internal static ProcessStartInfo CreateInstallerStartInfo(string installer)
        {
            if (string.IsNullOrWhiteSpace(installer)) throw new ArgumentException("安装包路径为空", nameof(installer));
            return new ProcessStartInfo
            {
                FileName = installer,
                Arguments = InstallerArguments,
                UseShellExecute = true,
                // Do not set Verb=runas here. The signed Inno Setup executable declares
                // PrivilegesRequired=admin and performs its own UAC transition while retaining
                // the original user's token. Pre-elevating Setup prevents runasoriginaluser
                // from dropping the relaunched YTray process back to normal integrity.
                WorkingDirectory = Path.GetDirectoryName(installer),
            };
        }

        private string? ExistingVerifiedDownload(AppReleaseManifest release, AppReleaseAsset asset)
        {
            try
            {
                var path = DownloadPath(release, asset);
                if (!File.Exists(path)) return null;
                VerifyFile(path, asset);
                return path;
            }
            catch
            {
                return null;
            }
        }

        private string DownloadPath(AppReleaseManifest release, AppReleaseAsset asset)
        {
            var safeVersion = release.Version.Replace('/', '-').Replace('\\', '-');
            var safeFilename = Path.GetFileName(asset.Filename);
            if (string.IsNullOrWhiteSpace(safeFilename)
                || !string.Equals(safeFilename, asset.Filename, StringComparison.Ordinal))
                throw new InvalidDataException("更新清单中的文件名不安全");
            return Path.Combine(
                _updatesDirectory,
                safeVersion,
                safeFilename);
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
            if (release.SchemaVersion != 1 || !string.Equals(release.Product, "ytray", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(release.Version) || release.Assets == null)
                throw new InvalidDataException("更新清单格式无效");
        }

        private static void ValidateAsset(AppReleaseAsset asset)
        {
            if (!Uri.TryCreate(asset.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("更新下载地址必须使用 HTTPS");
            if (asset.Size <= 0 || asset.SHA256.Length != 64
                || asset.SHA256.Any(character => !Uri.IsHexDigit(character)))
                throw new InvalidDataException("更新包校验信息无效");
            if (string.IsNullOrWhiteSpace(asset.Filename)
                || !string.Equals(Path.GetFileName(asset.Filename), asset.Filename, StringComparison.Ordinal))
                throw new InvalidDataException("更新包文件名无效");
        }

        internal static AppReleaseAsset? SelectAsset(
            AppReleaseManifest release,
            string platform,
            string architecture,
            string kind) => release.Assets.FirstOrDefault(asset =>
                string.Equals(asset.Platform, platform, StringComparison.OrdinalIgnoreCase)
                && string.Equals(asset.Architecture, architecture, StringComparison.OrdinalIgnoreCase)
                && string.Equals(asset.Kind, kind, StringComparison.OrdinalIgnoreCase));

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

        private static void VerifyFile(string path, AppReleaseAsset asset)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != asset.Size)
                throw new InvalidDataException("更新包大小与发布清单不一致");
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                var actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                if (!string.Equals(actual, asset.SHA256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("更新包 SHA-256 校验失败");
            }
        }

        private void SetDownloadProgress(int value)
        {
            value = Math.Max(0, Math.Min(100, value));
            if (_downloadPercent == value) return;
            _downloadPercent = value;
            OnPropertyChanged(nameof(DownloadPercent));
            OnPropertyChanged(nameof(ActionLabel));
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
            OnPropertyChanged(nameof(IsDownloaded));
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
                    // strand the UI in Checking/Downloading. Preserve diagnostics and keep
                    // notifying the remaining observers.
                    CrashGuard.Record("app-update-property-changed:" + name, ex);
                }
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
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
            _client.Dispose();
            _operationGate.Dispose();
        }
    }
}
