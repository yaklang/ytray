#nullable enable
using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace YTray.Native
{
    internal readonly struct WindowsGuiFocus
    {
        internal readonly IntPtr Window, CaretWindow;
        internal readonly Rectangle CaretBounds;
        internal readonly bool CaretVisible;

        private WindowsGuiFocus(IntPtr window, IntPtr caretWindow, Rectangle bounds, bool visible)
        {
            Window = window; CaretWindow = caretWindow; CaretBounds = bounds; CaretVisible = visible;
        }

        internal static WindowsGuiFocus? Read(IntPtr window)
        {
            var thread = GetWindowThreadProcessId(window, out _);
            var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf(typeof(GuiThreadInfo)) };
            var root = GetAncestor(window, 2);
            if (thread == 0 || root == IntPtr.Zero || !GetGUIThreadInfo(thread, ref info)
                || info.Focus == IntPtr.Zero || GetAncestor(info.Focus, 2) != root) return null;
            if (info.Caret == IntPtr.Zero || GetAncestor(info.Caret, 2) != root)
                return new WindowsGuiFocus(info.Focus, IntPtr.Zero, Rectangle.Empty, false);

            var topLeft = new WindowsAutomation.NativePoint { X = info.Bounds.Left, Y = info.Bounds.Top };
            var bottomRight = new WindowsAutomation.NativePoint { X = info.Bounds.Right, Y = info.Bounds.Bottom };
            var bounds = ClientToScreen(info.Caret, ref topLeft) && ClientToScreen(info.Caret, ref bottomRight)
                ? Rectangle.FromLTRB(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y) : Rectangle.Empty;
            return new WindowsGuiFocus(info.Focus, info.Caret, bounds, (info.Flags & 1) != 0);
        }

        internal static int BlinkWaitMilliseconds()
        {
            var blink = GetCaretBlinkTime();
            return blink == 0 || blink == uint.MaxValue ? 550 : (int)Math.Min(600u, blink) + 100;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GuiThreadInfo
        {
            internal uint Size, Flags;
            internal IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
            internal WindowsControlTarget.Rect Bounds;
        }

        [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint relation);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref WindowsAutomation.NativePoint point);
        [DllImport("user32.dll")] private static extern uint GetCaretBlinkTime();
    }
}
