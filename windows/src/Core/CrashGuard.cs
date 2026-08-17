#nullable enable
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace YTray.Core
{
    /// <summary>
    /// Last-resort diagnostics and recovery for UI/template failures. Expected errors still belong
    /// in their owning workflow; this prevents a single WPF binding/template defect from silently
    /// terminating the tray process and leaves an actionable stack trace for support.
    /// </summary>
    internal static class CrashGuard
    {
        private static readonly object LogLock = new object();
        private static string? _logDirectory;
        private static int _installed;

        internal static void Install(Application application, string applicationDirectory)
        {
            if (application == null) throw new ArgumentNullException(nameof(application));
            _logDirectory = Path.Combine(applicationDirectory, "Logs");
            if (Interlocked.Exchange(ref _installed, 1) != 0) return;
            application.DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                Write("fatal", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown fatal error"));
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                Write("unobserved-task", e.Exception);
                e.SetObserved();
            };
        }

        /// <summary>
        /// Explicitly observes a fire-and-forget task. Every background workflow must either be
        /// awaited by its owner or registered here so faults cannot disappear until finalization.
        /// </summary>
        internal static void Observe(Task? task, string? operation)
        {
            if (task == null) return;
            task.ContinueWith(
                completed => Write("background:" + (operation ?? "unknown"),
                    completed.Exception?.GetBaseException() ?? new Exception("Unknown background task failure")),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        internal static void Record(string? category, Exception? exception)
        {
            if (exception == null) return;
            Write(category ?? "diagnostic", exception);
        }

        private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Write("dispatcher", e.Exception);
            if (IsRecoverablePresentationFailure(e.Exception))
            {
                e.Handled = true;
                try
                {
                    MessageBox.Show(
                        "界面组件发生异常，YTray 已阻止程序退出。详细信息已写入 Logs。",
                        "YTray 已恢复",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                catch { }
            }
        }

        private static bool IsRecoverablePresentationFailure(Exception exception)
        {
            for (Exception? current = exception; current != null; current = current.InnerException)
                if (current is System.Windows.Markup.XamlParseException
                    || current is InvalidOperationException && current.StackTrace?.Contains("MS.Internal.Data") == true)
                    return true;
            return false;
        }

        private static void Write(string category, Exception exception)
        {
            try
            {
                var directory = string.IsNullOrWhiteSpace(_logDirectory)
                    ? Path.Combine(StatePersistence.DefaultApplicationDirectory, "Logs")
                    : _logDirectory;
                lock (LogLock)
                {
                    Directory.CreateDirectory(directory);
                    var text = new StringBuilder()
                        .AppendLine($"[{DateTime.Now:O}] {category}")
                        .AppendLine(exception.ToString())
                        .AppendLine()
                        .ToString();
                    File.AppendAllText(Path.Combine(directory, "ytray-errors.log"), text);
                }
            }
            catch { }
        }
    }
}
