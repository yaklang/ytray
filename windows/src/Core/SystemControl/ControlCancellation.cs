#nullable enable
using System;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using YTray.Models;

namespace YTray.Core.SystemControl
{
    internal static class ControlCancellation
    {
        private const string EventPrefix = @"Local\YTray.Control.Cancel.";

        internal static EventWaitHandle Create(out string id)
        {
            id = Guid.NewGuid().ToString("N");
            using var identity = WindowsIdentity.GetCurrent();
            var security = new EventWaitHandleSecurity();
            security.AddAccessRule(new EventWaitHandleAccessRule(identity.User!, EventWaitHandleRights.FullControl, AccessControlType.Allow));
            var signal = new EventWaitHandle(false, EventResetMode.ManualReset, EventPrefix + id, out var created, security);
            if (created) return signal;
            signal.Dispose();
            throw new ControlException("DriverFailure", "无法建立电脑控制取消信号");
        }

        internal static EventWaitHandle? Open(string? id)
        {
            if (id == null) return null;
            if (!Guid.TryParseExact(id, "N", out _))
                throw new ControlException("ProtocolViolation", "取消信号无效");
            if (!EventWaitHandle.TryOpenExisting(EventPrefix + id, EventWaitHandleRights.Synchronize, out var signal))
                throw Error();
            return signal;
        }

        internal static ControlException Error(string state = "NotStarted") =>
            new ControlException("Cancelled", "请求已取消，请重新观察当前界面。", state);

        internal static void Check(CancellationToken cancellation, string state = "NotStarted")
        {
            if (cancellation.IsCancellationRequested) throw Error(state);
        }

        internal static void Check(WaitHandle? cancellation, bool dispatched = false)
        {
            if (cancellation?.WaitOne(0) == true) throw Error(dispatched ? "Unknown" : "NotStarted");
        }
    }
}
