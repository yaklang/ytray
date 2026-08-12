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
            pv.pwszVal = Marshal.StringToCoTaskMemUni(value);
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
            if (vt == VT_EMPTY) return;
            Win32.ClearPropVariant(this);
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

        // Canonical System.AppUserModel property keys from propkey.h.
        public static readonly PROPERTYKEY PKEY_AppUserModel_ID =
            new PROPERTYKEY(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
        public static readonly PROPERTYKEY PKEY_AppUserModel_RelaunchCommand =
            new PROPERTYKEY(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 2);
        public static readonly PROPERTYKEY PKEY_AppUserModel_RelaunchDisplayNameResource =
            new PROPERTYKEY(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 4);
        public static readonly PROPERTYKEY PKEY_AppUserModel_RelaunchIconResource =
            new PROPERTYKEY(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 3);
        public static readonly PROPERTYKEY PKEY_AppUserModel_PreventPinning =
            new PROPERTYKEY(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 9);

        [DllImport("shell32.dll")]
        public static extern int SHGetPropertyStoreForWindow(
            IntPtr hwnd,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IPropertyStore ppv);

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear([In, Out] PROPVARIANT pvar);

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

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr LoadImage(IntPtr hInstance, string name, uint type,
            int desiredWidth, int desiredHeight, uint loadFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        public const int SW_RESTORE = 9;
        public const uint IMAGE_ICON = 1;
        public const uint LR_LOADFROMFILE = 0x0010;
        public const uint WM_SETICON = 0x0080;
        public const uint WM_GETICON = 0x007F;
        public const int ICON_SMALL = 0;
        public const int ICON_BIG = 1;
        public const int ICON_SMALL2 = 2;

        internal static void ClearPropVariant(PROPVARIANT variant)
        {
            try { PropVariantClear(variant); }
            finally
            {
                variant.vt = PROPVARIANT.VT_EMPTY;
                variant.pwszVal = IntPtr.Zero;
            }
        }

        /// <summary>Read the AUMID set on a live top-level window (Chrome_WidgetWin_1).</summary>
        public static string GetWindowAumid(IntPtr hwnd)
        {
            try
            {
                var iid = IID_IPropertyStore;
                IPropertyStore store;
                var result = SHGetPropertyStoreForWindow(hwnd, ref iid, out store);
                if (result < 0 || store == null) return null;
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

        /// <summary>Assign taskbar grouping/relaunch metadata directly to a live browser window.</summary>
        public static bool SetWindowAppProperties(IntPtr hwnd, string aumid, string displayName,
            string iconResource, string relaunchCommand = null)
        {
            if (hwnd == IntPtr.Zero) return false;
            IPropertyStore store = null;
            try
            {
                var iid = IID_IPropertyStore;
                var result = SHGetPropertyStoreForWindow(hwnd, ref iid, out store);
                if (result < 0 || store == null) return false;
                SetString(store, PKEY_AppUserModel_RelaunchIconResource, iconResource);
                SetString(store, PKEY_AppUserModel_ID, aumid);
                SetString(store, PKEY_AppUserModel_RelaunchDisplayNameResource, displayName);
                if (!string.IsNullOrWhiteSpace(relaunchCommand))
                    SetString(store, PKEY_AppUserModel_RelaunchCommand, relaunchCommand);
                // Window property stores apply SetValue immediately. Commit is required for .lnk
                // property stores but is not consistently implemented for HWND property stores.
                store.Commit();
                return true;
            }
            catch { return false; }
            finally
            {
                if (store != null) Marshal.ReleaseComObject(store);
            }
        }

        /// <summary>
        /// Keep a Chrome window on the requested taskbar identity. During staged startup we wait
        /// until Chrome has written its own non-empty AUMID before replacing it; this is the point
        /// at which the browser's window initialization is far enough along to remain stable.
        /// </summary>
        public static bool EnsureWindowAppProperties(IntPtr hwnd, string aumid, string iconResource,
            bool requireExistingAumid, out bool changed)
        {
            changed = false;
            if (hwnd == IntPtr.Zero || string.IsNullOrWhiteSpace(aumid)
                || string.IsNullOrWhiteSpace(iconResource)) return false;

            IPropertyStore store = null;
            try
            {
                var iid = IID_IPropertyStore;
                var result = SHGetPropertyStoreForWindow(hwnd, ref iid, out store);
                if (result < 0 || store == null) return false;

                var currentAumid = GetString(store, PKEY_AppUserModel_ID);
                if (requireExistingAumid && string.IsNullOrEmpty(currentAumid)) return false;

                var currentIcon = GetString(store, PKEY_AppUserModel_RelaunchIconResource);
                if (!string.Equals(currentIcon, iconResource, StringComparison.OrdinalIgnoreCase))
                {
                    SetString(store, PKEY_AppUserModel_RelaunchIconResource, iconResource);
                    changed = true;
                }
                if (!string.Equals(currentAumid, aumid, StringComparison.Ordinal))
                {
                    SetString(store, PKEY_AppUserModel_ID, aumid);
                    changed = true;
                }

                if (changed) store.Commit(); // best-effort; SetValue is authoritative for HWND stores
                return true;
            }
            catch { return false; }
            finally
            {
                if (store != null) Marshal.ReleaseComObject(store);
            }
        }

        private static string GetString(IPropertyStore store, PROPERTYKEY propertyKey)
        {
            var variant = new PROPVARIANT();
            var key = propertyKey;
            try
            {
                var result = store.GetValue(ref key, variant);
                return unchecked((int)result) < 0 ? null : variant.AsString();
            }
            finally { variant.Dispose(); }
        }

        private static void SetString(IPropertyStore store, PROPERTYKEY propertyKey, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var key = propertyKey;
            using (var variant = PROPVARIANT.FromString(value))
                store.SetValue(ref key, variant);
        }
    }
}
