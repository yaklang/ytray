#nullable enable
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Windows.Threading;

namespace YTray.Core.SystemControl
{
    internal sealed class ControlElevation : IDisposable
    {
        private readonly EventWaitHandle _shutdown;
        private readonly RegisteredWaitHandle _registration;
        internal static bool IsElevated { get { using var identity = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); } }
        private static string EventName(Process process) => @"Local\YTray.Control.Restart." + process.Id + "." + process.StartTime.ToUniversalTime().Ticks;
        internal ControlElevation(Dispatcher dispatcher, Action shutdown)
        {
            var security = new EventWaitHandleSecurity();
            security.AddAccessRule(new EventWaitHandleAccessRule(WindowsIdentity.GetCurrent().User!, EventWaitHandleRights.FullControl, AccessControlType.Allow));
            using var process = Process.GetCurrentProcess();
            _shutdown = new EventWaitHandle(false, EventResetMode.AutoReset, EventName(process), out _, security);
            _registration = ThreadPool.RegisterWaitForSingleObject(_shutdown, (_, __) => dispatcher.BeginInvoke(shutdown), null, Timeout.Infinite, true);
        }
        internal static ProcessStartInfo RepairStartInfo()
        {
            using var process = Process.GetCurrentProcess();
            var executable = process.MainModule!.FileName!;
            return new ProcessStartInfo(executable, "--repair-computer-control --replace-process " + process.Id + " --replace-created " + process.StartTime.ToUniversalTime().Ticks) {
                UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(executable)!
            };
        }
        internal static void RequestRepair()
        {
            try { Process.Start(RepairStartInfo())?.Dispose(); }
            catch (Win32Exception error) when (error.NativeErrorCode == 1223) { throw new OperationCanceledException("已取消 UAC 提权，现有电脑控制保持不变。"); }
        }
        // The elevated replacement only asks the exact same-user YTray process to exit.
        // It never kills a process or accepts an arbitrary shutdown target.
        internal static bool CompleteHandoff(string[] args)
        {
            if (!args.Contains("--repair-computer-control")) return false;
            if (!IsElevated) throw new InvalidOperationException("请通过“修复控制权限”的 UAC 确认启动管理员模式。");
            var pidIndex = Array.IndexOf(args, "--replace-process"); var timeIndex = Array.IndexOf(args, "--replace-created");
            if (pidIndex < 0 || timeIndex < 0 || pidIndex + 1 >= args.Length || timeIndex + 1 >= args.Length
                || !int.TryParse(args[pidIndex + 1], out var pid) || !long.TryParse(args[timeIndex + 1], out var created)) throw new InvalidOperationException("电脑控制重启参数无效。");
            using var current = Process.GetCurrentProcess(); using var previous = Process.GetProcessById(pid);
            if (previous.StartTime.ToUniversalTime().Ticks != created || previous.SessionId != current.SessionId
                || !string.Equals(previous.MainModule!.FileName, current.MainModule!.FileName, StringComparison.OrdinalIgnoreCase) || !SameUser(previous))
                throw new InvalidOperationException("无法验证原 YTray 实例，请关闭它后以管理员身份重新打开。");
            using (var signal = EventWaitHandle.OpenExisting(EventName(previous), EventWaitHandleRights.Modify)) signal.Set();
            if (!previous.WaitForExit(10000)) throw new InvalidOperationException("原 YTray 仍在退出，请稍后重试。");
            return true;
        }
        private static bool SameUser(Process process)
        {
            if (!OpenProcessToken(process.Handle, 8, out var token)) return false;
            try { using var identity = new WindowsIdentity(token); return identity.User == WindowsIdentity.GetCurrent().User; }
            finally { CloseHandle(token); }
        }
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        public void Dispose() { _registration.Unregister(null); _shutdown.Dispose(); }
    }
}
