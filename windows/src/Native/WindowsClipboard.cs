#nullable enable
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using YTray.Core;

namespace YTray.Native
{
    internal static class WindowsClipboard
    {
        internal const int AccessDenied = 5;
        private const uint MoveableMemory = 0x0002;
        private const uint UnicodeText = 13;

        internal static void WriteText(IntPtr owner, string text)
        {
            if (owner == IntPtr.Zero) throw new InvalidOperationException("Clipboard copy requires a window handle");

            var bytes = Encoding.Unicode.GetBytes(text + '\0');
            var memory = GlobalAlloc(MoveableMemory, (UIntPtr)bytes.Length);
            if (memory == IntPtr.Zero) throw Failed("GlobalAlloc");

            try
            {
                var buffer = GlobalLock(memory);
                if (buffer == IntPtr.Zero) throw Failed("GlobalLock");
                try { Marshal.Copy(bytes, 0, buffer, bytes.Length); }
                finally { GlobalUnlock(memory); }

                if (!OpenClipboard(owner))
                {
                    var error = Marshal.GetLastWin32Error();
                    var holdingWindow = GetOpenClipboardWindow();
                    uint holdingProcessId = 0;
                    if (holdingWindow != IntPtr.Zero) GetWindowThreadProcessId(holdingWindow, out holdingProcessId);
                    throw new OpenException(error, holdingProcessId);
                }
                try
                {
                    if (!EmptyClipboard()) throw Failed("EmptyClipboard");
                    if (SetClipboardData(UnicodeText, memory) == IntPtr.Zero) throw Failed("SetClipboardData");
                    memory = IntPtr.Zero; // Windows owns the published buffer.
                }
                finally
                {
                    if (!CloseClipboard())
                        DiagnosticLog.Warning("mcp.clipboard", Failed("CloseClipboard").Message);
                }
            }
            finally
            {
                if (memory != IntPtr.Zero) GlobalFree(memory);
            }
        }

        private static Win32Exception Failed(string operation)
        {
            var error = Marshal.GetLastWin32Error();
            return new Win32Exception(error, $"{operation} failed (Win32 {error})");
        }

        internal sealed class OpenException : Win32Exception
        {
            internal OpenException(int error, uint holdingProcessId = 0)
                : base(error, $"OpenClipboard failed (Win32 {error}, holding PID {holdingProcessId})") { }
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr memory);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
        [DllImport("user32.dll")] private static extern IntPtr GetOpenClipboardWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    }
}
