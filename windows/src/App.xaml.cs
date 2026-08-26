#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json;
using YTray.Core;
using YTray.Models;

namespace YTray
{
    public partial class App : Application
    {
        private InstanceStore? _store;
        private TrayApp? _tray;
        private LaunchAtLoginManager? _launchAtLogin;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var args = Environment.GetCommandLineArgs();
            string? designCaptureDirectory = null;
            bool captureSitePreviewOnly = false;
            bool startupLaunch = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--verify-standalone" && i + 1 < args.Length)
                {
                    VerifyStandalone(args[++i]);
                    Environment.Exit(0);
                    return;
                }
                if (args[i] == "--capture-design-review" && i + 1 < args.Length)
                {
                    designCaptureDirectory = args[++i];
                    continue;
                }
                if (args[i] == "--capture-site-preview" && i + 1 < args.Length)
                {
                    designCaptureDirectory = args[++i];
                    captureSitePreviewOnly = true;
                    continue;
                }
                if (args[i] == "--startup")
                {
                    startupLaunch = true;
                    continue;
                }
                if (args[i] == "--smoke-browser" && i + 1 < args.Length)
                {
                    // Do not synchronously block the WPF synchronization context around awaits.
                    Task.Run(() => SmokeBrowser.RunAsync(args[i + 1])).GetAwaiter().GetResult();
                    Environment.Exit(0);
                    return;
                }
                if (args[i] == "--probe-aumid" && i + 1 < args.Length)
                {
                    AumidProbe.Run(args[i + 1]);
                    Environment.Exit(0);
                    return;
                }
            }

            _store = new InstanceStore();
            CrashGuard.Install(this, _store.ApplicationDirectory);
            if (!string.IsNullOrWhiteSpace(designCaptureDirectory))
            {
                ThemeManager.Initialize(AppThemePreference.Light);
                CrashGuard.Observe(RunDesignCaptureAsync(designCaptureDirectory!, captureSitePreviewOnly), "design-capture");
                return;
            }
            var initialTheme = _store.Settings.ThemePreference;
            // Non-persistent rendering override used by visual smoke tests and support diagnostics.
            foreach (var arg in e.Args)
            {
                if (string.Equals(arg, "--theme-preview=dark", StringComparison.OrdinalIgnoreCase))
                    initialTheme = AppThemePreference.Dark;
                else if (string.Equals(arg, "--theme-preview=light", StringComparison.OrdinalIgnoreCase))
                    initialTheme = AppThemePreference.Light;
            }
            var previewTheme = Environment.GetEnvironmentVariable("YTRAY_CAPTURE_THEME");
            if (string.Equals(previewTheme, "dark", StringComparison.OrdinalIgnoreCase))
                initialTheme = AppThemePreference.Dark;
            else if (string.Equals(previewTheme, "light", StringComparison.OrdinalIgnoreCase))
                initialTheme = AppThemePreference.Light;
            ThemeManager.Initialize(initialTheme);
            _launchAtLogin = new LaunchAtLoginManager();
            _tray = new TrayApp(_store, _launchAtLogin);
            var firstLaunchOutcome = _launchAtLogin.EnableOnFirstLaunchIfNeeded(
                _store.Settings,
                _store.SaveSettings
            );
            if (firstLaunchOutcome == FirstLaunchAtLoginOutcome.Enabled)
            {
                MessageBox.Show(
                    "已默认开启开机启动。以后登录 Windows 时，YTray 会自动进入系统托盘；不会自动打开浏览器。你可以在“开机启动”页面随时管理。",
                    "YTray 开机启动",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
            }
            else if (firstLaunchOutcome == FirstLaunchAtLoginOutcome.Failed)
            {
                MessageBox.Show(
                    "暂时无法自动开启开机启动。这个问题不影响其他功能；你可以稍后在 YTray 的“开机启动”页面重试。\n\n" +
                    (_launchAtLogin.ErrorMessage ?? ""),
                    "YTray 开机启动",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
            }
            // A login launch stays in the tray. A normal launch opens the manager immediately.
            if (!startupLaunch) _tray.ShowManager();
        }

        private static void VerifyStandalone(string markerPath)
        {
            // App.InitializeComponent has already resolved HandyControl's merged WPF resource
            // dictionaries before OnStartup. Resolve one of our styles as an explicit smoke
            // assertion, then exercise Newtonsoft.Json so both embedded dependencies are loaded.
            if (Current.TryFindResource("YTrayButton") == null)
                throw new InvalidOperationException("WPF resources were not loaded.");
            if (string.IsNullOrWhiteSpace(markerPath))
                throw new ArgumentException("A standalone verification marker path is required.", nameof(markerPath));

            var fullPath = Path.GetFullPath(markerPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory!);
            var extractionRoot = Path.Combine(directory ?? Path.GetTempPath(),
                "bundled-extension-smoke-" + Guid.NewGuid().ToString("N"));
            string bundledVersion;
            string bundledName;
            try
            {
                if (!ExtensionInstaller.TryInstallBundled(extractionRoot, out var extensionDirectory,
                        out bundledVersion, ignoreOptOut: true))
                    throw new InvalidOperationException("The bundled browser extension is missing.");
                var manifestPath = Path.Combine(extensionDirectory, "manifest.json");
                var manifest = JsonConvert.DeserializeObject<PluginManifest>(File.ReadAllText(manifestPath));
                bundledName = manifest?.Name ?? "";
                if (!string.Equals(bundledName, ExtensionInstaller.ExtensionName, StringComparison.Ordinal))
                    throw new InvalidOperationException("The bundled browser extension manifest is invalid.");
            }
            finally
            {
                try { if (Directory.Exists(extractionRoot)) Directory.Delete(extractionRoot, true); } catch { }
            }
            var payload = JsonConvert.SerializeObject(new
            {
                application = "YTray",
                version = YTrayBuildInfo.Version,
                framework = ".NET Framework 4.8.1",
                jsonAssembly = typeof(JsonConvert).Assembly.GetName().Name,
                bundledExtensionVersion = bundledVersion,
                bundledExtensionName = bundledName,
            });
            File.WriteAllText(fullPath, payload);
        }

        private async Task RunDesignCaptureAsync(string outputDirectory, bool sitePreviewOnly)
        {
            try
            {
                var store = _store ?? throw new InvalidOperationException("Instance store is not initialized.");
                await DesignCaptureService.CaptureAsync(store, outputDirectory, sitePreviewOnly);
            }
            catch (Exception ex)
            {
                try
                {
                    Directory.CreateDirectory(outputDirectory);
                    File.WriteAllText(Path.Combine(outputDirectory, "capture-error.txt"), ex.ToString());
                }
                catch { }
            }
            finally
            {
                Shutdown();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _tray?.Dispose();
            _launchAtLogin = null;
            _store?.Dispose();
            ThemeManager.Shutdown();
            base.OnExit(e);
        }
    }
}
