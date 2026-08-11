using System;
using System.Runtime.InteropServices;

namespace YTray.Native
{
    /// <summary>
    /// Property key for System.AppUserModel.ID (canonical Windows GUID, per propkey.h).
    /// fmtid = 9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3, pid = 5.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct PROPERTYKEY
    {
        public Guid fmtid;
        public int pid;
        public PROPERTYKEY(Guid fmtid, int pid) { this.fmtid = fmtid; this.pid = pid; }
    }

    /// <summary>
    /// Minimal PROPVARIANT supporting VT_LPWSTR (string) and VT_EMPTY.
    /// Layout must match the native PROPVARIANT for GetValue/SetValue marshalling.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    public class PROPVARIANT : IDisposable
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(2)] public ushort wReserved1;
        [FieldOffset(4)] public ushort wReserved2;
        [FieldOffset(6)] public ushort wReserved3;
        [FieldOffset(8)] public IntPtr pwszVal;   // VT_LPWSTR / VT_BSTR
        [FieldOffset(8)] public IntPtr pVal;      // VT_UNKNOWN etc.

        public const ushort VT_EMPTY = 0;
        public const ushort VT_LPWSTR = 0x001F;

        public static PROPVARIANT FromString(string value)
        {
            var pv = new PROPVARIANT { vt = VT_LPWSTR };
            pv.pwszVal = Marshal.StringToHGlobalUni(value);
            return pv;
        }

        public string AsString()
        {
            if (vt == VT_LPWSTR && pwszVal != IntPtr.Zero)
                return Marshal.PtrToStringUni(pwszVal);
            return null;
        }

        public void Dispose()
        {
            Clear();
        }

        public void Clear()
        {
            if (vt == VT_LPWSTR && pwszVal != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(pwszVal);
                pwszVal = IntPtr.Zero;
            }
            vt = VT_EMPTY;
        }
    }

    /// <summary>IID = 886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99</summary>
    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        uint GetCount(out uint cProps);
        uint GetAt(uint iProp, out PROPERTYKEY pkey);
        [PreserveSig]
        uint GetValue(ref PROPERTYKEY key, [Out] PROPVARIANT pv);
        [PreserveSig]
        uint SetValue(ref PROPERTYKEY key, [In] PROPVARIANT pv);
        [PreserveSig]
        uint Commit();
    }

    /// <summary>IShellLinkW — minimal vtable for writing shortcuts.</summary>
    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellLinkW
    {
        uint GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cchMax, IntPtr pfd, uint fFlags);
        uint GetIDList(out IntPtr ppidl);
        uint SetIDList(IntPtr pidl);
        uint GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cchMax);
        uint SetDescription([In, MarshalAs(UnmanagedType.LPWStr)] string pszName);
        uint GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cchMax);
        uint SetWorkingDirectory([In, MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        uint GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cchMax);
        uint SetArguments([In, MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        uint GetShowCmd(out int piShowCmd);
        uint SetShowCmd(int iShowCmd);
        uint GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cchMax, out int piIcon);
        uint SetIconLocation([In, MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        uint SetRelativePath([In, MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        uint Resolve(IntPtr hwnd, uint fFlags);
        uint SetPath([In, MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    /// <summary>IPersistFile — load/save .lnk files.</summary>
    [ComImport]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPersistFile
    {
        uint GetClassID(out Guid pClassID);
        uint IsDirty();
        [PreserveSig]
        uint Load([In, MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        uint Save([In, MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [In, MarshalAs(UnmanagedType.Bool)] bool fRemember);
        uint SaveCompleted([In, MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        uint GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    /// <summary>CLSID_ShellLink = 00021401-0000-0000-C000-000000000046.</summary>
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    [ClassInterface(ClassInterfaceType.None)]
    public class ShellLinkClass { }

    public static class Win32
    {
        public static readonly Guid IID_IPropertyStore = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

        // Canonical System.AppUserModel.ID
        public static readonly PROPERTYKEY PKEY_AppUserModel_ID =
            new PROPERTYKEY(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
        public static readonly PROPERTYKEY PKEY_AppUserModel_RelaunchCommand =
            new PROPERTYKEY(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 2);
        public static readonly PROPERTYKEY PKEY_AppUserModel_RelaunchDisplayNameResource =
            new PROPERTYKEY(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 3);
        public static readonly PROPERTYKEY PKEY_AppUserModel_RelaunchIconResource =
            new PROPERTYKEY(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 4);
        public static readonly PROPERTYKEY PKEY_AppUserModel_PreventPinning =
            new PROPERTYKEY(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 9);

        [DllImport("shell32.dll", PreserveSig = false)]
        public static extern IPropertyStore SHGetPropertyStoreForWindow(
            IntPtr hwnd,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IPropertyStore ppv);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHGetPropertyStoreFromIDList(
            IntPtr pidl,
            int flags,
            ref Guid riid,
            out IPropertyStore ppv);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int GetWindowThreadProcessId(IntPtr hWnd, out int processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        public const int SW_RESTORE = 9;

        /// <summary>Read the AUMID set on a live top-level window (Chrome_WidgetWin_1).</summary>
        public static string GetWindowAumid(IntPtr hwnd)
        {
            try
            {
                var iid = IID_IPropertyStore;
                IPropertyStore store = SHGetPropertyStoreForWindow(hwnd, ref iid, out store);
                if (store == null) return null;
                var pv = new PROPVARIANT();
                var key = PKEY_AppUserModel_ID;
                try
                {
                    store.GetValue(ref key, pv);
                    return pv.AsString();
                }
                finally
                {
                    pv.Dispose();
                    Marshal.ReleaseComObject(store);
                }
            }
            catch
            {
                return null;
            }
        }
    }
}