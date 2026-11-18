#nullable enable
using System;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Windows.Threading;

namespace YTray.Core
{
    internal enum TrayOpenTarget { Manager, ComputerControl, Mcp, Widget }

    // The desktop session owns the tray icon independently of the EXE's name or location.
    internal sealed class TrayInstance : IDisposable
    {
        private readonly Mutex _mutex;
        private readonly EventWaitHandle?[] _signals = new EventWaitHandle?[4];
        private readonly RegisteredWaitHandle?[] _listeners = new RegisteredWaitHandle?[4];
        internal bool IsPrimary { get; }

        internal TrayInstance() : this("YTray.Desktop") { }

        internal TrayInstance(string name)
        {
            using var identity = WindowsIdentity.GetCurrent();
            using var process = Process.GetCurrentProcess();
            var user = identity.User ?? throw new InvalidOperationException("无法确定 Windows 用户。");
            var prefix = $@"Local\{name}.{process.SessionId}.{user.Value}";
            var mutexSecurity = new MutexSecurity();
            mutexSecurity.AddAccessRule(new MutexAccessRule(user, MutexRights.FullControl, AccessControlType.Allow));
            var eventSecurity = new EventWaitHandleSecurity();
            eventSecurity.AddAccessRule(new EventWaitHandleAccessRule(user, EventWaitHandleRights.FullControl, AccessControlType.Allow));

            _mutex = new Mutex(false, prefix + ".Owner", out _, mutexSecurity);
            try
            {
                // Open the signals before competing for ownership. Early relaunches can
                // signal before the primary has finished starting its tray UI.
                for (var index = 0; index < _signals.Length; index++)
                    _signals[index] = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + ".Open." + index, out _, eventSecurity);
                try { IsPrimary = _mutex.WaitOne(0); }
                catch (AbandonedMutexException) { IsPrimary = true; }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal void RequestOpen(TrayOpenTarget target) => _signals[(int)target]!.Set();

        internal void Listen(Dispatcher dispatcher, Action<TrayOpenTarget> show)
        {
            for (var index = 0; index < _listeners.Length; index++)
            {
                var target = (TrayOpenTarget)index;
                _listeners[index] = ThreadPool.RegisterWaitForSingleObject(_signals[index]!,
                    (_, _) => dispatcher.BeginInvoke(new Action(() => show(target))),
                    null, Timeout.Infinite, false);
            }
        }

        public void Dispose()
        {
            foreach (var listener in _listeners) listener?.Unregister(null);
            foreach (var signal in _signals) signal?.Dispose();
            if (IsPrimary) _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }
}
