using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using YTray.Core;

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
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--smoke-browser" && i + 1 < args.Length)
                {
                    SmokeBrowser.RunAsync(args[i + 1]).GetAwaiter().GetResult();
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
            _tray = new TrayApp(_store);
            // Show the manager window on launch so the user sees the UI immediately.
            _tray.ShowManager();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _tray?.Dispose();
            base.OnExit(e);
        }
    }
}