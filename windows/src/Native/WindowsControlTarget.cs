#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Newtonsoft.Json.Linq;
using YTray.Models;

namespace YTray.Native
{
    internal sealed class WindowsControlTarget
    {
        internal string Reference = Guid.NewGuid().ToString("N");
        internal IntPtr Handle;
        internal int Pid;
        internal long Created;
        internal string Executable = "";
        internal string Title = "";
        internal Rect Bounds;
        internal JObject Json() => new JObject { ["target_ref"] = Reference, ["pid"] = Pid, ["window_id"] = Handle.ToInt64().ToString(),
            ["title"] = Title, ["executable"] = Executable, ["bounds"] = new JObject { ["x"] = Bounds.Left, ["y"] = Bounds.Top, ["width"] = Bounds.Right - Bounds.Left, ["height"] = Bounds.Bottom - Bounds.Top } };
        internal static List<WindowsControlTarget> Enumerate()
        {
            var results = new List<WindowsControlTarget>();
            EnumWindows((handle, _) => {
                try {
                    if (!IsWindowVisible(handle) || GetWindowTextLength(handle) == 0) return true;
                    var target = Resolve(handle); if (target.Pid == Process.GetCurrentProcess().Id) return true;
                    var title = new StringBuilder(1024); GetWindowText(handle, title, title.Capacity); target.Title = title.ToString(); results.Add(target);
                } catch { } return true;
            }, IntPtr.Zero);
            return results;
        }
        internal static WindowsControlTarget Resolve(IntPtr handle)
        {
            if (!IsWindow(handle)) throw new ControlException("TargetChanged", "窗口已关闭，请重新发现目标");
            GetWindowThreadProcessId(handle, out var pid);
            using var process = Process.GetProcessById((int)pid);
            if (process.SessionId != Process.GetCurrentProcess().SessionId) throw new ControlException("DesktopUnavailable", "拒绝跨 Windows 会话控制");
            var native = OpenProcess(0x1000, false, pid); if (native == IntPtr.Zero) throw new Win32Exception();
            try {
                var path = new StringBuilder(32768); var size = path.Capacity;
                if (!QueryFullProcessImageName(native, 0, path, ref size)) throw new Win32Exception();
                if (!OpenProcessToken(native, 8, out var token)) throw new Win32Exception();
                try {
                    using var identity = new WindowsIdentity(token);
                    if (identity.User != WindowsIdentity.GetCurrent().User) throw new ControlException("ScopeDenied", "目标不属于当前用户");
                    var integrity = Integrity(token);
                    var ceiling = Core.SystemControl.ControlElevation.IsElevated ? 0x3000 : 0x2000;
                    if (integrity > ceiling) throw new ControlException("IntegrityLevelBlocked", "目标窗口权限较高，请在电脑控制页点击“修复控制权限”并确认 UAC。");
                } finally { CloseHandle(token); }
                GetWindowRect(handle, out var bounds);
                var title = new StringBuilder(1024); GetWindowText(handle, title, title.Capacity);
                return new WindowsControlTarget { Handle = handle, Pid = (int)pid, Created = process.StartTime.ToUniversalTime().Ticks, Executable = path.ToString(), Title = title.ToString(), Bounds = bounds };
            } finally { CloseHandle(native); }
        }
        internal void Revalidate(bool requireGeometry = false)
        {
            var current = Resolve(Handle);
            if (current.Pid != Pid || current.Created != Created || !string.Equals(current.Executable, Executable, StringComparison.OrdinalIgnoreCase)
                || requireGeometry && !current.Bounds.Equals(Bounds)) throw new ControlException("TargetChanged", "窗口身份或几何信息已改变，请重新观察");
        }
        internal bool IsObscured(IntPtr ignoreWindow) => FindOccluder(ignoreWindow) != IntPtr.Zero;
        internal IntPtr FindOccluder(IntPtr ignoreWindow)
        {
            var bounds = VisibleBounds(Handle);
            var above = GetWindow(Handle, 3); // GW_HWNDPREV: higher in Z order.
            for (var count = 0; above != IntPtr.Zero && count < 2048; count++, above = GetWindow(above, 3)) {
                if (above == ignoreWindow || !IsWindowVisible(above) || IsIconic(above)) continue;
                if (DwmGetWindowAttribute(above, 14, out var cloaked, 4) == 0 && cloaked != 0) continue;
                // Native recording/cursor overlays are transparent to input and
                // explicitly omitted by Windows capture. They do not cover the image.
                if ((GetWindowLong(above, -20) & 0x80020) == 0x80020 && GetWindowDisplayAffinity(above, out var affinity) && affinity == 0x11) continue;
                var other = VisibleBounds(above);
                if (Math.Min(bounds.Right, other.Right) - Math.Max(bounds.Left, other.Left) > 2 && Math.Min(bounds.Bottom, other.Bottom) - Math.Max(bounds.Top, other.Top) > 2) return above;
            }
            return IntPtr.Zero;
        }
        private static Rect VisibleBounds(IntPtr window) { if (DwmGetBounds(window, 9, out var bounds, Marshal.SizeOf(typeof(Rect))) == 0) return bounds; GetWindowRect(window, out bounds); return bounds; }
        internal static void RequireDesktop()
        {
            var session = Process.GetCurrentProcess().SessionId;
            if (!WTSQuerySessionInformation(IntPtr.Zero, session, 8, out var state, out var bytes)) throw new ControlException("DesktopUnavailable", "无法检查交互会话");
            try { if (bytes < 4 || Marshal.ReadInt32(state) != 0) throw new ControlException("DesktopUnavailable", "会话已锁定、断开或不可交互"); }
            finally { WTSFreeMemory(state); }
            var desktop = OpenInputDesktop(0, false, 1); if (desktop == IntPtr.Zero) throw new ControlException("DesktopUnavailable", "安全桌面或锁屏不支持电脑控制");
            try {
                var name = new StringBuilder(256);
                if (!GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _) || !string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase))
                    throw new ControlException("DesktopUnavailable", "当前不在用户交互桌面");
            } finally { CloseDesktop(desktop); }
        }
        private static int Integrity(IntPtr token)
        {
            GetTokenInformation(token, 25, IntPtr.Zero, 0, out var bytes); var buffer = Marshal.AllocHGlobal(bytes);
            try { if (!GetTokenInformation(token, 25, buffer, bytes, out _)) throw new Win32Exception(); var sid = Marshal.ReadIntPtr(buffer);
                var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid)); return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1))); }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
        private delegate bool EnumCallback(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr handle);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr handle);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr handle, uint command);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr handle, int index);
        [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(IntPtr handle, out uint affinity);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);
        [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmGetBounds(IntPtr handle, int attribute, out Rect value, int size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, StringBuilder text, int size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr handle);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint pid);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out Rect rect);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder path, ref int size);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int type, IntPtr buffer, int length, out int needed);
        [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
        [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(int flags, bool inherit, uint access);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int length, out int needed);
        [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("wtsapi32.dll")] private static extern bool WTSQuerySessionInformation(IntPtr server, int session, int type, out IntPtr buffer, out int bytes);
        [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
    }
}
