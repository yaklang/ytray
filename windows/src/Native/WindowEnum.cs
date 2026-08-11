using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using YTray.Native;

namespace YTray.Native
{
    /// <summary>
    /// Enumerates top-level Chrome windows for a given process id and reads their AUMID.
    /// </summary>
    public static class WindowEnum
    {
        public const string ChromeWindowClass = "Chrome_WidgetWin_1";

        /// <summary>Find the first visible top-level window belonging to the given PID.</summary>
        public static IntPtr FindFirstVisibleWindow(int processId)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((hWnd, _) =>
            {
                Win32.GetWindowThreadProcessId(hWnd, out int pid);
                if (pid != processId || !Win32.IsWindowVisible(hWnd)) return true;
                var cls = new StringBuilder(64);
                Win32.GetClassName(hWnd, cls, cls.Capacity);
                if (cls.ToString() == ChromeWindowClass)
                {
                    // Prefer a window that has a non-empty title (the real browser frame).
                    var titleLen = Win32.GetWindowTextLength(hWnd);
                    if (titleLen > 0)
                    {
                        found = hWnd;
                        return false; // stop enumeration
                    }
                    if (found == IntPtr.Zero) found = hWnd; // fallback: any visible chrome window
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>Find the first visible window of any class for the PID (less strict).</summary>
        public static IntPtr FindAnyVisibleWindow(int processId)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((hWnd, _) =>
            {
                Win32.GetWindowThreadProcessId(hWnd, out int pid);
                if (pid != processId || !Win32.IsWindowVisible(hWnd)) return true;
                found = hWnd;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        public static bool EnumWindows(Win32.EnumWindowsProc callback, IntPtr lParam)
        {
            return Win32.EnumWindows(callback, lParam);
        }

        /// <summary>Poll until a Chrome window appears for the PID, then return its AUMID. Null on timeout.</summary>
        public static string PollForWindowAumid(int processId, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var hwnd = FindFirstVisibleWindow(processId);
                if (hwnd != IntPtr.Zero)
                {
                    var aumid = Win32.GetWindowAumid(hwnd);
                    if (!string.IsNullOrEmpty(aumid)) return aumid;
                    // Window found but AUMID not yet set; keep polling briefly.
                }
                System.Threading.Thread.Sleep(200);
            }
            return null;
        }
    }
}