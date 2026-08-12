#nullable enable
using System;
using System.Runtime.InteropServices;

namespace YTray.Native
{
    internal static class NativeWindowStyles
    {
        internal const long WsExTransparent = 0x00000020L;
        internal const long WsExToolWindow = 0x00000080L;
        internal const long WsExNoActivate = 0x08000000L;
        private const int GwlExStyle = -20;

        internal static long GetExtendedStyle(IntPtr hwnd) =>
            hwnd == IntPtr.Zero ? throw new ArgumentException("A valid window handle is required.", nameof(hwnd))
                : (IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, GwlExStyle).ToInt64() : GetWindowLong32(hwnd, GwlExStyle));

        internal static void SetExtendedStyle(IntPtr hwnd, long value)
        {
            if (hwnd == IntPtr.Zero) throw new ArgumentException("A valid window handle is required.", nameof(hwnd));
            if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, GwlExStyle, new IntPtr(value));
            else SetWindowLong32(hwnd, GwlExStyle, unchecked((int)value));
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong32(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);
    }
}
