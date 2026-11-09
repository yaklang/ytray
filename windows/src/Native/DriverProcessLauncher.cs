#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace YTray.Native
{
    // The child is assigned to a non-inheritable kill-on-close Job AT CREATION.
    // Starting first and assigning later leaves an orphan window if the host dies.
    internal sealed class DriverProcessLauncher : IDisposable
    {
        private IntPtr _job;
        internal Process Process { get; private set; } = null!;
        internal Stream Input { get; private set; } = null!;
        internal Stream Output { get; private set; } = null!;
        internal Stream Error { get; private set; } = null!;
        internal static DriverProcessLauncher Start(string executable, IDictionary<string, string> environment, string arguments = "mcp")
        {
            var owner = new DriverProcessLauncher();
            var childHandles = new List<SafeFileHandle>(); var parentHandles = new List<SafeFileHandle>();
            IntPtr attributes = IntPtr.Zero, jobValue = IntPtr.Zero, handlesValue = IntPtr.Zero, environmentBlock = IntPtr.Zero;
            var info = new ProcessInformation();
            try {
                owner._job = CreateJobObject(IntPtr.Zero, null); Check(owner._job != IntPtr.Zero);
                var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } };
                Check(SetInformationJobObject(owner._job, 9, ref limits, (uint)Marshal.SizeOf(limits)));
                var security = new SecurityAttributes { Length = Marshal.SizeOf(typeof(SecurityAttributes)), Inherit = true };
                SafeFileHandle Pipe(bool stdin) {
                    Check(CreatePipe(out var read, out var write, ref security, 0));
                    var child = stdin ? read : write; var parent = stdin ? write : read;
                    childHandles.Add(child); parentHandles.Add(parent);
                    Check(SetHandleInformation(parent, 1, 0)); return child;
                }
                var start = new StartupInfoEx(); start.Startup.Size = Marshal.SizeOf(start);
                start.Startup.Flags = 0x100; start.Startup.Input = Pipe(true).DangerousGetHandle();
                start.Startup.Output = Pipe(false).DangerousGetHandle(); start.Startup.Error = Pipe(false).DangerousGetHandle();
                IntPtr size = IntPtr.Zero; InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
                attributes = Marshal.AllocHGlobal(size); Check(InitializeProcThreadAttributeList(attributes, 2, 0, ref size));
                jobValue = Marshal.AllocHGlobal(IntPtr.Size); Marshal.WriteIntPtr(jobValue, owner._job);
                Check(UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x2000D), jobValue, new IntPtr(IntPtr.Size), IntPtr.Zero, IntPtr.Zero));
                handlesValue = Marshal.AllocHGlobal(3 * IntPtr.Size);
                for (var i = 0; i < 3; i++) Marshal.WriteIntPtr(handlesValue, i * IntPtr.Size, childHandles[i].DangerousGetHandle());
                Check(UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x20002), handlesValue, new IntPtr(3 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero));
                start.Attributes = attributes;
                var env = string.Join("\0", environment.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase).Select(e => e.Key + "=" + e.Value)) + "\0\0";
                environmentBlock = Marshal.StringToHGlobalUni(env);
                Check(CreateProcess(executable, new System.Text.StringBuilder("\"" + executable + "\" " + arguments), IntPtr.Zero, IntPtr.Zero, true,
                    0x08000000 | 0x00080000 | 0x00000400, environmentBlock, Path.GetDirectoryName(executable)!, ref start, out info));
                owner.Process = Process.GetProcessById((int)info.ProcessId);
                owner.Input = new FileStream(parentHandles[0], FileAccess.Write, 4096, false);
                owner.Output = new FileStream(parentHandles[1], FileAccess.Read, 4096, false);
                owner.Error = new FileStream(parentHandles[2], FileAccess.Read, 4096, false);
                parentHandles.Clear(); return owner;
            } catch { owner.Dispose(); throw; }
            finally {
                foreach (var handle in childHandles) handle.Dispose(); foreach (var handle in parentHandles) handle.Dispose();
                if (info.Thread != IntPtr.Zero) CloseHandle(info.Thread); if (info.Process != IntPtr.Zero) CloseHandle(info.Process);
                if (attributes != IntPtr.Zero) { DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
                if (jobValue != IntPtr.Zero) Marshal.FreeHGlobal(jobValue); if (handlesValue != IntPtr.Zero) Marshal.FreeHGlobal(handlesValue);
                if (environmentBlock != IntPtr.Zero) Marshal.FreeHGlobal(environmentBlock);
            }
        }
        public void Dispose()
        {
            var job = System.Threading.Interlocked.Exchange(ref _job, IntPtr.Zero);
            if (job != IntPtr.Zero) { TerminateJobObject(job, 1); CloseHandle(job); }
            try { Input?.Dispose(); } catch { } try { Output?.Dispose(); } catch { } try { Error?.Dispose(); } catch { }
            Process?.Dispose();
        }
        private static void Check(bool ok) { if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo { public int Size; public string? Reserved, Desktop, Title; public int X, Y, Width, Height, XCount, YCount, Fill, Flags; public short Show, ReservedSize; public IntPtr ReservedPointer, Input, Output, Error; }
        [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Startup; public IntPtr Attributes; }
        [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long ProcessTime, JobTime; public uint LimitFlags; public UIntPtr Minimum, Maximum; public uint Active; public UIntPtr Affinity; public uint Priority, Scheduling; }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcess, PeakJob; }
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr security, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int type, ref ExtendedLimits info, uint size);
        [DllImport("kernel32.dll")] private static extern bool TerminateJobObject(IntPtr job, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
        [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CreateProcess(string file, System.Text.StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation info);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
