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
        internal IntPtr Owner;
        internal int Pid;
        internal long Created;
        internal string Executable = "";
        internal string Title = "";
        internal Rect Bounds;
        internal bool IsOwned => Owner != IntPtr.Zero;
        internal bool IsNonActivating => (GetWindowLong(Handle, -20) & 0x08000000) != 0; // GWL_EXSTYLE, WS_EX_NOACTIVATE
        internal JObject Json() => new JObject { ["target_ref"] = Reference, ["pid"] = Pid, ["window_id"] = Handle.ToInt64().ToString(),
            ["title"] = Title, ["executable"] = Executable, ["bounds"] = new JObject { ["x"] = Bounds.Left, ["y"] = Bounds.Top, ["width"] = Bounds.Right - Bounds.Left, ["height"] = Bounds.Bottom - Bounds.Top } };
        internal static List<WindowsControlTarget> Enumerate(string? executable = null)
        {
            HashSet<uint>? processes = null;
            if (executable != null) {
                processes = new HashSet<uint>();
                foreach (var process in Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(executable)))
                    using (process) processes.Add((uint)process.Id);
                if (processes.Count == 0) return new List<WindowsControlTarget>();
            }
            using var currentProcess = Process.GetCurrentProcess();
            var currentPid = (uint)currentProcess.Id;
            var desktop = GetShellWindow();
            var results = new List<WindowsControlTarget>();
            EnumWindows((handle, _) => {
                try {
                    if (handle == desktop || !IsWindowVisible(handle) || GetWindowTextLength(handle) == 0) return true;
                    GetWindowThreadProcessId(handle, out var pid);
                    if (pid == 0 || pid == currentPid || processes != null && !processes.Contains(pid)) return true;
                    var target = Resolve(handle);
                    if (executable == null || WindowsApplicationControl.SamePath(target.Executable, executable)) results.Add(target);
                } catch { } return true;
            }, IntPtr.Zero);
            return results;
        }
        internal static WindowsControlTarget Resolve(IntPtr handle)
        {
            if (!IsWindow(handle)) throw new ControlException("TargetChanged", "窗口已关闭，请重新发现目标");
            GetWindowThreadProcessId(handle, out var pid);
            using var process = Process.GetProcessById((int)pid);
            var executable = ProcessExecutable(process);
            var bounds = ReadBounds(handle);
            var title = new StringBuilder(1024); GetWindowText(handle, title, title.Capacity);
            return new WindowsControlTarget { Handle = handle, Owner = SameProcessOwner(handle, pid), Pid = process.Id,
                Created = process.StartTime.ToUniversalTime().Ticks, Executable = executable, Title = title.ToString(), Bounds = bounds };
        }

        internal List<IntPtr> VisibleOwnedHandles()
        {
            var windows = new List<IntPtr>();
            if (!IsDisplayed) return windows;
            EnumWindows((handle, _) => {
                if (handle != Handle && IsWindowVisible(handle) && Owns(handle)) windows.Add(handle);
                return true;
            }, IntPtr.Zero);
            return windows;
        }

        internal List<WindowsControlTarget> FindOwnedWindows(int maximum)
        {
            var windows = new List<WindowsControlTarget>();
            foreach (var handle in VisibleOwnedHandles()) {
                try {
                    var candidate = Resolve(handle);
                    if (candidate.Pid == Pid && candidate.Created == Created && candidate.IsDisplayed && candidate.IsOwnedBy(Handle)
                        && candidate.Bounds.Right > candidate.Bounds.Left && candidate.Bounds.Bottom > candidate.Bounds.Top)
                        windows.Add(candidate);
                } catch (Exception error) when (error is ControlException or Win32Exception or ArgumentException or InvalidOperationException) { }
                if (windows.Count >= maximum) break;
            }
            return windows;
        }

        internal bool IsOwnedBy(IntPtr owner) => IsOwnedBy(Handle, owner, (uint)Pid);
        internal bool Owns(IntPtr window) => GetWindowThreadProcessId(window, out var pid) != 0 && pid == (uint)Pid && IsOwnedBy(window, Handle, pid);

        private static bool IsOwnedBy(IntPtr handle, IntPtr owner, uint pid)
        {
            if (owner == IntPtr.Zero) return false;
            for (var depth = 0; depth < 16; depth++) {
                handle = SameProcessOwner(handle, pid);
                if (handle == owner) return true;
                if (handle == IntPtr.Zero) break;
            }
            return false;
        }

        private static IntPtr SameProcessOwner(IntPtr handle, uint pid)
        {
            var owner = GetWindow(handle, 4); // GW_OWNER also works for untitled tool windows.
            return owner != IntPtr.Zero && GetWindowThreadProcessId(owner, out var ownerPid) != 0 && ownerPid == pid
                ? owner : IntPtr.Zero;
        }

        internal void RequireVisibleOwned()
        {
            if (IsOwned && (!IsDisplayed || SameProcessOwner(Handle, (uint)Pid) != Owner))
                throw PopupChanged();
        }

        internal static ControlException PopupChanged() =>
            new ControlException("PopupChanged", "关联弹层已关闭或归属已变化，请重新观察主窗口。");
        internal static string ProcessExecutable(Process process)
        {
            if (process.SessionId != Process.GetCurrentProcess().SessionId) throw new ControlException("DesktopUnavailable", "拒绝跨 Windows 会话控制");
            var native = OpenProcess(0x1000, false, (uint)process.Id); if (native == IntPtr.Zero) throw new Win32Exception();
            try {
                var path = new StringBuilder(32768); var size = path.Capacity;
                if (!QueryFullProcessImageName(native, 0, path, ref size)) throw new Win32Exception();
                if (!OpenProcessToken(native, 8, out var token)) throw new Win32Exception();
                try {
                    using var identity = new WindowsIdentity(token);
                    using var current = WindowsIdentity.GetCurrent();
                    if (identity.User != current.User) throw new ControlException("ScopeDenied", "目标不属于当前用户");
                    var integrity = Integrity(token);
                    var ceiling = Core.SystemControl.ControlElevation.IsElevated ? 0x3000 : 0x2000;
                    if (integrity > ceiling) throw new ControlException("IntegrityLevelBlocked", "目标窗口权限较高，请在电脑控制页点击“修复控制权限”并确认 UAC。");
                } finally { CloseHandle(token); }
                return path.ToString();
            } finally { CloseHandle(native); }
        }
        internal void Revalidate(bool requireGeometry = false)
        {
            if (IsOwned && SameProcessOwner(Handle, (uint)Pid) != Owner) throw PopupChanged();
            var current = Resolve(Handle);
            if (IsOwned && current.Owner != Owner) throw PopupChanged();
            if (current.Pid != Pid || current.Created != Created || !string.Equals(current.Executable, Executable, StringComparison.OrdinalIgnoreCase)
                || requireGeometry && !current.Bounds.Equals(Bounds)) throw new ControlException("TargetChanged", "窗口身份或几何信息已改变，请重新观察");
        }
        internal static Rect ReadBounds(IntPtr window)
        {
            if (!GetWindowRect(window, out var bounds)) throw new ControlException("TargetChanged", "窗口已关闭，请重新观察");
            return bounds;
        }
        internal bool IsObscured(IntPtr ignoreWindow) => FindOccluder(ignoreWindow) != IntPtr.Zero;
        internal bool IsDisplayed => IsWindowVisible(Handle) && !IsIconic(Handle)
            && (DwmGetWindowAttribute(Handle, 14, out var cloaked, 4) != 0 || cloaked == 0);
        internal bool IsCaptureRestricted => GetWindowDisplayAffinity(Handle, out var affinity) && affinity != 0;
        internal IntPtr FindOccluder(IntPtr ignoreWindow)
        {
            var bounds = VisibleBounds(Handle);
            var above = GetWindow(Handle, 3); // GW_HWNDPREV: higher in Z order.
            for (var count = 0; above != IntPtr.Zero && count < 2048; count++, above = GetWindow(above, 3)) {
                if (above == ignoreWindow || !IsWindowVisible(above) || IsIconic(above)) continue;
                if (WindowsActionOverlay.IsOverlayWindow(above)) continue;
                if (DwmGetWindowAttribute(above, 14, out var cloaked, 4) == 0 && cloaked != 0) continue;
                // Native recording/cursor overlays are transparent to input and
                // explicitly omitted by Windows capture. They do not cover the image.
                if ((GetWindowLong(above, -20) & 0x80020) == 0x80020 && GetWindowDisplayAffinity(above, out var affinity) && affinity == 0x11) continue;
                var other = VisibleBounds(above);
                if (Math.Min(bounds.Right, other.Right) - Math.Max(bounds.Left, other.Left) > 2 && Math.Min(bounds.Bottom, other.Bottom) - Math.Max(bounds.Top, other.Top) > 2) return above;
            }
            return IntPtr.Zero;
        }
        internal static Rect VisibleBounds(IntPtr window) { if (DwmGetBounds(window, 9, out var bounds, Marshal.SizeOf(typeof(Rect))) == 0) return bounds; GetWindowRect(window, out bounds); return bounds; }
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
        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
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
