#nullable enable
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using YTray.Core;
using YTray.Native;

namespace YTray.Views
{
    internal static class McpClipboard
    {
        private const int MaxOpenAttempts = 5;
        private static int _copyGeneration;

        internal const string FailureMessage = "无法写入剪贴板，详情请查看诊断日志。";

        internal static Task<bool?> TryCopyAsync(Visual view, Func<string?> readConfiguration) =>
            TryCopyAsync(readConfiguration, text =>
            {
                var owner = (PresentationSource.FromVisual(view) as HwndSource)?.Handle ?? IntPtr.Zero;
                WindowsClipboard.WriteText(owner, text);
            });

        internal static async Task<bool?> TryCopyAsync(Func<string?> readConfiguration, Action<string> writeText)
        {
            // All callers run on the UI dispatcher. A newer click supersedes a pending copy.
            var generation = ++_copyGeneration;
            for (var attempt = 1; ; attempt++)
            {
                if (generation != _copyGeneration || readConfiguration() is not string text) return null;
                try
                {
                    writeText(text);
                    return true;
                }
                catch (WindowsClipboard.OpenException error) when (error.NativeErrorCode == WindowsClipboard.AccessDenied && attempt < MaxOpenAttempts)
                {
                    await Task.Delay(75);
                }
                catch (Exception error)
                {
                    DiagnosticLog.Error("mcp.clipboard", error, $"MCP configuration copy failed after {attempt} attempt(s)");
                    return false;
                }
            }
        }

    }
}
