#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace YTray.Native
{
    internal static class FolderPicker
    {
        private const int Cancelled = unchecked((int)0x800704C7);

        public static IReadOnlyList<string> PickMultiple(Window? owner, string title)
        {
            var dialog = (IFileOpenDialog)new FileOpenDialog();
            try
            {
                dialog.GetOptions(out var options);
                dialog.SetOptions(options | FileOpenOptions.PickFolders | FileOpenOptions.ForceFileSystem
                    | FileOpenOptions.AllowMultiSelect | FileOpenOptions.PathMustExist);
                dialog.SetTitle(title);
                dialog.SetOkButtonLabel("添加");
                var result = dialog.Show(owner == null ? IntPtr.Zero : new WindowInteropHelper(owner).Handle);
                if (result == Cancelled) return Array.Empty<string>();
                Marshal.ThrowExceptionForHR(result);

                dialog.GetResults(out var items);
                try
                {
                    items.GetCount(out var count);
                    var paths = new List<string>((int)count);
                    for (uint index = 0; index < count; index++)
                    {
                        items.GetItemAt(index, out var item);
                        try
                        {
                            item.GetDisplayName(DisplayName.FileSystemPath, out var pointer);
                            try
                            {
                                var path = Marshal.PtrToStringUni(pointer);
                                if (!string.IsNullOrWhiteSpace(path)) paths.Add(path!);
                            }
                            finally { Marshal.FreeCoTaskMem(pointer); }
                        }
                        finally { Marshal.FinalReleaseComObject(item); }
                    }
                    return paths;
                }
                finally { Marshal.FinalReleaseComObject(items); }
            }
            finally { Marshal.FinalReleaseComObject(dialog); }
        }

        [Flags]
        private enum FileOpenOptions : uint
        {
            PickFolders = 0x20,
            ForceFileSystem = 0x40,
            AllowMultiSelect = 0x200,
            PathMustExist = 0x800,
        }

        private enum DisplayName : uint { FileSystemPath = 0x80058000 }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FilterSpec
        {
            public string Name;
            public string Spec;
        }

        [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
        private class FileOpenDialog { }

        [ComImport, Guid("D57C7288-D4AD-4768-BE02-9D969532D960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] FilterSpec[] filters);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(FileOpenOptions options);
            void GetOptions(out FileOpenOptions options);
            void SetDefaultFolder(IShellItem item);
            void SetFolder(IShellItem item);
            void GetFolder(out IShellItem item);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
            void AddPlace(IShellItem item, uint location);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
            void Close(int error);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr filter);
            void GetResults(out IShellItemArray items);
            void GetSelectedItems(out IShellItemArray items);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr context, ref Guid handler, ref Guid interfaceId, out IntPtr result);
            void GetParent(out IShellItem parent);
            void GetDisplayName(DisplayName name, out IntPtr value);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem other, uint hint, out int order);
        }

        [ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemArray
        {
            void BindToHandler(IntPtr context, ref Guid handler, ref Guid interfaceId, out IntPtr result);
            void GetPropertyStore(int flags, ref Guid interfaceId, out IntPtr store);
            void GetPropertyDescriptionList(IntPtr propertyKey, ref Guid interfaceId, out IntPtr descriptions);
            void GetAttributes(uint flags, uint mask, out uint attributes);
            void GetCount(out uint count);
            void GetItemAt(uint index, out IShellItem item);
        }
    }
}
