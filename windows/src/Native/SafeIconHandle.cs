#nullable enable
using System;
using Microsoft.Win32.SafeHandles;

namespace YTray.Native
{
    /// <summary>
    /// Owns an HICON returned by LoadImage/GetHicon. SafeHandle guarantees DestroyIcon runs once
    /// even when construction or a later Win32 call throws before the normal cleanup path.
    /// </summary>
    internal sealed class SafeIconHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeIconHandle() : base(true) { }

        private SafeIconHandle(IntPtr handle) : base(true) => SetHandle(handle);

        internal static SafeIconHandle Own(IntPtr handle) => new SafeIconHandle(handle);

        protected override bool ReleaseHandle() => Win32.DestroyIcon(handle);
    }
}
