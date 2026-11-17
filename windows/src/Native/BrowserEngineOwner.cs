#nullable enable
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace YTray.Native
{
    internal static class BrowserEngineOwner
    {
        internal static string? Find(int processId)
        {
            if (processId <= 0) return null;
            try
            {
                using var engine = Process.GetProcessById(processId);
                if (!string.Equals(engine.ProcessName, "yak", StringComparison.OrdinalIgnoreCase)) return null;
                var size = Marshal.SizeOf<ProcessInformation>();
                if (NtQueryInformationProcess(engine.Handle, 0, out var info, size, out _) != 0 || info.ParentId == IntPtr.Zero)
                    return null;
                using var owner = Process.GetProcessById(info.ParentId.ToInt32());
                if (owner.StartTime > engine.StartTime) return null;
                var product = owner.MainModule?.FileVersionInfo.ProductName;
                if (owner.ProcessName.Equals("Memfit AI", StringComparison.OrdinalIgnoreCase) && product == "Memfit AI")
                    return "memfit";
                if (owner.ProcessName.Equals("Yakit", StringComparison.OrdinalIgnoreCase) && product == "Yakit")
                    return "yakit";
            }
            catch (Exception error) when (error is ArgumentException || error is InvalidOperationException || error is Win32Exception) { }
            return null;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            internal IntPtr ExitStatus, Peb, AffinityMask, BasePriority, ProcessId, ParentId;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr handle, int infoClass, out ProcessInformation info, int length, out int returned);
    }
}
