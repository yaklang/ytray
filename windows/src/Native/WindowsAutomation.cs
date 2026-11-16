#nullable enable
using System;
using System.Runtime.InteropServices;

namespace YTray.Native
{
    // Windows SDK UIAutomationClient.h interfaces. Only the used vtable prefixes
    // are declared; empty methods preserve unused slots, never called by YTray.
    internal static class WindowsAutomation
    {
        internal static IAutomation Create()
        {
            var automation = (IAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("e22ad333-b25f-460c-83d0-0581107395c9"))!)!;
            // IUIAutomation2 inherits 55 IUIAutomation methods after IUnknown.
            // Native connection/transaction deadlines also bound a hung provider.
            var unknown = Marshal.GetIUnknownForObject(automation); var extended = IntPtr.Zero;
            try {
                var iid = new Guid("34723aff-0c9d-49d0-9896-7ab52df8cd8a");
                if (Marshal.QueryInterface(unknown, ref iid, out extended) == 0) {
                    var table = Marshal.ReadIntPtr(extended);
                    var autoFocus = (SetTimeout)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(table, 59 * IntPtr.Size), typeof(SetTimeout));
                    Marshal.ThrowExceptionForHR(autoFocus(extended, 0));
                    foreach (var slot in new[] { 61, 63 }) {
                        var setter = (SetTimeout)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(table, slot * IntPtr.Size), typeof(SetTimeout));
                        Marshal.ThrowExceptionForHR(setter(extended, 500));
                    }
                }
            } finally { if (extended != IntPtr.Zero) Marshal.Release(extended); Marshal.Release(unknown); }
            return automation;
        }
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetTimeout(IntPtr self, uint milliseconds);

        [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IAutomation
        {
            [return: MarshalAs(UnmanagedType.Bool)] bool CompareElements(IElement first, IElement second);
            void CompareRuntimeIds();
            IElement GetRootElement();
            IElement ElementFromHandle(IntPtr window);
            IElement ElementFromPoint(NativePoint point);
            IElement GetFocusedElement();
            IElement GetRootElementBuildCache(ICache cache);
            IElement ElementFromHandleBuildCache(IntPtr window, ICache cache);
            void ElementFromPointBuildCache(); IElement? GetFocusedElementBuildCache(ICache cache); void CreateTreeWalker();
            IWalker ControlViewWalker();
            void ContentViewWalker(); IWalker RawViewWalker(); void RawViewCondition(); void ControlViewCondition(); void ContentViewCondition();
            ICache CreateCacheRequest();
        }
        [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IElement
        {
            void SetFocus();
            [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)] int[] GetRuntimeId();
            void FindFirst(); void FindAll(); void FindFirstBuildCache(); void FindAllBuildCache();
            IElement BuildUpdatedCache(ICache cache);
            [return: MarshalAs(UnmanagedType.Struct)] object Current(int property);
            void GetCurrentPropertyValueEx();
            [return: MarshalAs(UnmanagedType.Struct)] object Cached(int property);
            void GetCachedPropertyValueEx(); void GetCurrentPatternAs(); void GetCachedPatternAs();
            [return: MarshalAs(UnmanagedType.IUnknown)] object Pattern(int pattern);
        }
        [ComImport, Guid("b32a92b5-bc25-4078-9c08-d7ee95c48e03"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface ICache { void AddProperty(int property); void AddPattern(int pattern); }
        [ComImport, Guid("4042c624-389c-4afc-a630-9df854a541fc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IWalker
        {
            IElement Parent(IElement element); IElement FirstChild(IElement element); void LastChild();
            IElement NextSibling(IElement element); void PreviousSibling(); void Normalize(); void ParentBuildCache();
            IElement FirstChildBuildCache(IElement element, ICache cache); void LastChildBuildCache();
            IElement NextSiblingBuildCache(IElement element, ICache cache);
        }
        [ComImport, Guid("fb377fbe-8ea6-46d5-9c73-6499642d3059"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IInvoke { void Invoke(); }
        [ComImport, Guid("a94cd8b1-0844-4cd6-9d2d-640537ab39e9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IValue { void SetValue([MarshalAs(UnmanagedType.LPWStr)] string value); }
        [ComImport, Guid("94cf8058-9b8d-4ab9-8bfd-4cd0a33c8c70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IToggle { void Toggle(); }
        [ComImport, Guid("a8efa66a-0fda-421a-9194-38021f3578ea"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface ISelectionItem { void Select(); }
        [ComImport, Guid("88f4d42a-e881-459d-a77c-73bbbb7e02dc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IScroll { void Scroll(int horizontal, int vertical); }
        [ComImport, Guid("32eba289-3583-42c9-9c59-3b6d9a1e9b6a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface ITextPattern
        {
            void RangeFromPoint(); void RangeFromChild();
            ITextRangeArray? GetSelection();
            void GetVisibleRanges(); void DocumentRange();
            int SupportedTextSelection();
        }
        [ComImport, Guid("ce4ae76a-e717-4c98-81ea-47371d028eb6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface ITextRangeArray
        {
            int Length();
            ITextRange? GetElement(int index);
        }
        [ComImport, Guid("a543cc6a-f4ae-494b-8239-c814481187a8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface ITextRange
        {
            void Clone(); void Compare(); void CompareEndpoints(); void ExpandToEnclosingUnit();
            void FindAttribute(); void FindText(); void GetAttributeValue(); void GetBoundingRectangles(); void GetEnclosingElement();
            [return: MarshalAs(UnmanagedType.BStr)] string? GetText(int maxLength);
        }
        [ComImport, Guid("b488300f-d015-4f19-9c29-bb595e3645ef"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IScrollItem { void ScrollIntoView(); }
        [ComImport, Guid("828055ad-355b-4435-86d5-3b51c14a9b1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface ILegacy { void Select(int flags); void DoDefaultAction(); void SetValue([MarshalAs(UnmanagedType.LPWStr)] string value); }
        [StructLayout(LayoutKind.Sequential)] internal struct NativePoint { internal int X, Y; }
    }
}
