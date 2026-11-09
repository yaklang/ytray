#nullable enable
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using YTray.Models;

namespace YTray.Native
{
    internal static class WindowsControlProtection
    {
        // Screen input may never land on the host's authorization/configuration UI.
        internal static void RequireSafeInput(JObject args, int width, int height)
        {
            var window = GetForegroundWindow();
            if (args["x"] != null && args["y"] != null) {
                var screen = (System.Windows.Forms.Screen.PrimaryScreen ?? throw new ControlException("DesktopUnavailable", "当前没有可用的桌面显示器")).Bounds;
                window = WindowFromPoint(new Point { X = screen.Left + (int)(args["x"]!.Value<double>() * screen.Width / width), Y = screen.Top + (int)(args["y"]!.Value<double>() * screen.Height / height) });
            }
            GetWindowThreadProcessId(window, out var pid);
            if (pid == Process.GetCurrentProcess().Id) throw new ControlException("ScopeDenied", "电脑控制不能操作 YTray 的授权或配置界面");
            if (window != IntPtr.Zero) WindowsControlTarget.Resolve(GetAncestor(window, 2));
        }
        [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    }
}
