using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using YTray.Core;
using YTray.Models;

namespace YTray
{
    public partial class App : Application
    {
        private InstanceStore _store;
        private TrayApp _tray;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var args = Environment.GetCommandLineArgs();
            string designCaptureDirectory = null;
            for (int i = 0; i < args.Length; i++)
            {
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
            if (!string.IsNullOrWhiteSpace(designCaptureDirectory))
            {
                ThemeManager.Initialize(AppThemePreference.Light);
                _ = RunDesignCaptureAsync(designCaptureDirectory);
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

        private async Task RunDesignCaptureAsync(string outputDirectory)
        {
            try
            {
                await DesignCaptureService.CaptureAsync(_store, outputDirectory);
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
