#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using YTray.Core;

namespace YTray.Native
{
    internal static class WindowPresentation
    {
        internal static (int Group, string Framework) Classify(string windowClass)
        {
            if (windowClass.StartsWith("HwndWrapper", StringComparison.Ordinal)) return (0, "WPF");
            if (windowClass.StartsWith("WindowsForms10.", StringComparison.Ordinal)) return (0, "WinForms");
            if (windowClass is "WinUIDesktopWin32WindowClass" or "Windows.UI.Core.CoreWindow" or "ApplicationFrameWindow") return (0, "Windows XAML");
            if (windowClass is "#32770" or "CabinetWClass" or "ExploreWClass") return (0, "Win32");
            if (windowClass.StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal) || windowClass == "Chrome_RenderWidgetHostHWND") return (1, "Chromium");
            if (windowClass == "MozillaWindowClass") return (1, "Gecko");
            if (windowClass.StartsWith("Qt", StringComparison.Ordinal) && windowClass.Contains("QWindow", StringComparison.Ordinal)) return (1, "Qt");
            if (windowClass == "MMUIRenderSubWindowHW") return (1, "自绘界面");
            return (2, "框架未识别");
        }

        internal static (int Group, string Framework, string Class, BitmapSource? Icon) Read(WindowsControlTarget target, CancellationToken cancellation)
        {
            var windowClass = ReadClass(target.Handle);
            var evidence = windowClass;
            var kind = Classify(windowClass);
            if (kind.Group == 2) {
                var visited = 0;
                EnumChildWindows(target.Handle, (child, _) => {
                    if (cancellation.IsCancellationRequested || ++visited > 48) return false;
                    var childClass = ReadClass(child);
                    var candidate = Classify(childClass);
                    // A system dialog can be hosted inside any toolkit. Use
                    // the rendering child or a Windows XAML root as evidence.
                    if (candidate.Group == 2 || candidate.Framework == "Win32") return true;
                    kind = candidate;
                    evidence = childClass;
                    return false;
                }, IntPtr.Zero);
            }
            cancellation.ThrowIfCancellationRequested();
            return (kind.Group, kind.Framework, evidence, ReadIcon(target));
        }

        private static string ReadClass(IntPtr handle)
        {
            var name = new StringBuilder(256);
            Win32.GetClassName(handle, name, name.Capacity);
            return name.ToString();
        }

        private static BitmapSource? ReadIcon(WindowsControlTarget target)
        {
            try {
                _ = SendMessageTimeout(target.Handle, Win32.WM_GETICON, new IntPtr(Win32.ICON_SMALL2), IntPtr.Zero, 3, 40, out var handle);
                if (handle == IntPtr.Zero) handle = ClassIcon(target.Handle, -34);
                if (handle == IntPtr.Zero) handle = ClassIcon(target.Handle, -14);
                if (handle != IntPtr.Zero) return CopyIcon(handle);
            } catch (Exception error) when (error is ArgumentException || error is ExternalException) { }
            try {
                using var icon = BrowserProcessIcon.ExtractLargeIcon(target.Executable);
                return icon == null ? null : CopyIcon(icon.Handle);
            } catch (Exception error) when (error is ArgumentException || error is ExternalException) { return null; }
        }

        private static BitmapSource CopyIcon(IntPtr handle)
        {
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
            bitmap.Freeze();
            return bitmap;
        }

        private static IntPtr ClassIcon(IntPtr window, int index) => IntPtr.Size == 8
            ? GetClassLongPtr(window, index) : new IntPtr(unchecked((int)GetClassLong(window, index)));

        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr window, Win32.EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] private static extern IntPtr GetClassLongPtr(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "GetClassLongW")] private static extern uint GetClassLong(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")] private static extern IntPtr SendMessageTimeout(
            IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    }
}
