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

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var args = Environment.GetCommandLineArgs();
            string? designCaptureDirectory = null;
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
                CrashGuard.Observe(RunDesignCaptureAsync(designCaptureDirectory!), "design-capture");
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
            _tray = new TrayApp(_store);
            // Show the manager window on launch so the user sees the UI immediately.
            _tray.ShowManager();
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
                        out bundledVersion))
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
                framework = ".NET Framework 4.8.1",
                jsonAssembly = typeof(JsonConvert).Assembly.GetName().Name,
                bundledExtensionVersion = bundledVersion,
                bundledExtensionName = bundledName,
            });
            File.WriteAllText(fullPath, payload);
        }

        private async Task RunDesignCaptureAsync(string outputDirectory)
        {
            try
            {
                var store = _store ?? throw new InvalidOperationException("Instance store is not initialized.");
                await DesignCaptureService.CaptureAsync(store, outputDirectory);
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
            _store?.Dispose();
            ThemeManager.Shutdown();
            base.OnExit(e);
        }
    }
}
