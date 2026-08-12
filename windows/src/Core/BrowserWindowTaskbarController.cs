using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using YTray.Native;

namespace YTray.Core
{
    /// <summary>
    /// Owns the Windows taskbar identity of one launched Chromium instance.
    ///
    /// The hook is fully ready before chrome.exe is started. New Chrome windows are DWM-cloaked
    /// and temporarily removed from taskbar eligibility while Chrome completes its normal SHOW and
    /// GPU/DWM initialization. Once Chrome has produced a native AUMID, the controller installs the
    /// per-instance AUMID + ICO, waits for them to remain stable, then restores the window.
    ///
    /// This is the in-process YTray port of the previously verified Yakit launcher protocol v2.
    /// It deliberately does not use SW_HIDE/SW_RESTORE as a hiding mechanism because that sequence
    /// can leave Chromium's compositor surface black during first launch.
    /// </summary>
    public sealed class BrowserWindowTaskbarController : IDisposable
    {
        private const uint EventObjectCreate = 0x8000;
        private const uint EventObjectShow = 0x8002;
        private const int ObjidWindow = 0;
        private const uint WineventOutofcontext = 0x0000;
        private const uint WineventSkipownprocess = 0x0002;
        private const int GwlExstyle = -20;
        private const long WsExToolwindow = 0x00000080L;
        private const long WsExAppwindow = 0x00040000L;
        private const uint SwpNomove = 0x0002;
        private const uint SwpNosize = 0x0001;
        private const uint SwpNozorder = 0x0004;
        private const uint SwpNoactivate = 0x0010;
        private const uint SwpFramechanged = 0x0020;
        private const uint DwmwaCloak = 13;
        private const int PollIntervalMilliseconds = 5;
        private const int RevealDelayMilliseconds = 250;

        private readonly object _sync = new object();
        private readonly string _executablePath;
        private readonly string _iconResource;
        private readonly HashSet<IntPtr> _previousHandles;
        private readonly Dictionary<IntPtr, IntPtr> _stagedOriginalStyles = new Dictionary<IntPtr, IntPtr>();
        private readonly Dictionary<IntPtr, DateTime> _stagedShowTimes = new Dictionary<IntPtr, DateTime>();
        private readonly Dictionary<IntPtr, DateTime> _stagedPropertyReadyTimes = new Dictionary<IntPtr, DateTime>();
        private readonly HashSet<IntPtr> _completedHandles = new HashSet<IntPtr>();
        private readonly ManualResetEvent _eventReady = new ManualResetEvent(false);
        private readonly ManualResetEvent _processAttached = new ManualResetEvent(false);
        private readonly TaskCompletionSource<bool> _initialWindowReady =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private WinEventDelegate _winEventDelegate;
        private Thread _eventThread;
        private Thread _monitorThread;
        private IntPtr _eventHook;
        private int _targetProcessId;
        private int _stopRequested;
        private bool _disposed;

        public string AppUserModelId { get; }
        public string IconPath { get; }
        public bool StagingActive { get; private set; }

        public BrowserWindowTaskbarController(string executablePath, string appUserModelId, string iconPath)
        {
            if (!File.Exists(executablePath)) throw new FileNotFoundException("Browser executable was not found.", executablePath);
            if (!File.Exists(iconPath)) throw new FileNotFoundException("Taskbar icon was not found.", iconPath);
            if (string.IsNullOrWhiteSpace(appUserModelId) || appUserModelId.Length > 128
                || appUserModelId.IndexOf(' ') >= 0)
                throw new ArgumentException("The AppUserModelID must be non-empty, at most 128 characters, and contain no spaces.", nameof(appUserModelId));

            _executablePath = Path.GetFullPath(executablePath);
            AppUserModelId = appUserModelId;
            IconPath = Path.GetFullPath(iconPath);
            _iconResource = IconPath + ",0";
            _previousHandles = SnapshotChromeWindowHandles();

            try { StartWindowEventThread(); }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>Bind the already-ready hook to the browser process returned by Process.Start.</summary>
        public void AttachProcess(int processId)
        {
            if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(BrowserWindowTaskbarController));
                if (_targetProcessId != 0 && _targetProcessId != processId)
                    throw new InvalidOperationException("The taskbar controller is already attached to another process.");
                _targetProcessId = processId;
                RestoreForeignStagedWindows(processId);
                if (_monitorThread == null)
                {
                    _monitorThread = new Thread(MonitorWindows)
                    {
                        IsBackground = true,
                        Name = "ytray-chrome-taskbar-monitor",
                    };
                    _monitorThread.Start();
                }
            }
            _processAttached.Set();
        }

        public async Task<bool> WaitForInitialWindowAsync(TimeSpan timeout)
        {
            var completed = await Task.WhenAny(_initialWindowReady.Task, Task.Delay(timeout)).ConfigureAwait(false);
            return completed == _initialWindowReady.Task && await _initialWindowReady.Task.ConfigureAwait(false);
        }

        private void StartWindowEventThread()
        {
            _winEventDelegate = HandleWindowEvent;
            _eventThread = new Thread(() =>
            {
                var hook = SetWinEventHook(
                    EventObjectCreate,
                    EventObjectShow,
                    IntPtr.Zero,
                    _winEventDelegate,
                    0,
                    0,
                    WineventOutofcontext | WineventSkipownprocess);
                lock (_sync) _eventHook = hook;
                _eventReady.Set();
                try
                {
                    while (Interlocked.CompareExchange(ref _stopRequested, 0, 0) == 0)
                    {
                        PumpWindowMessages();
                        Thread.Sleep(1);
                    }
                }
                finally
                {
                    if (hook != IntPtr.Zero) UnhookWinEvent(hook);
                }
            })
            {
                IsBackground = true,
                Name = "ytray-chrome-window-events",
            };
            _eventThread.SetApartmentState(ApartmentState.STA);
            _eventThread.Start();

            if (!_eventReady.WaitOne(3000))
                throw new TimeoutException("Timed out starting the Chrome window event hook.");
            lock (_sync) StagingActive = _eventHook != IntPtr.Zero;
        }

        private void HandleWindowEvent(IntPtr hook, uint eventType, IntPtr hwnd, int objectId,
            int childId, uint eventThread, uint eventTime)
        {
            if ((eventType != EventObjectCreate && eventType != EventObjectShow)
                || objectId != ObjidWindow || childId != 0 || hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
            if (!IsChromeWindow(hwnd)) return;

            Win32.GetWindowThreadProcessId(hwnd, out var processId);
            if (processId <= 0 || !MatchesExecutable(processId)) return;

            lock (_sync)
            {
                if (_disposed || _previousHandles.Contains(hwnd) || _completedHandles.Contains(hwnd)) return;
                if (_targetProcessId != 0 && processId != _targetProcessId) return;

                if (!_stagedOriginalStyles.ContainsKey(hwnd))
                {
                    var originalStyle = GetWindowExStyle(hwnd);
                    var cloak = 1;
                    DwmSetWindowAttribute(hwnd, DwmwaCloak, ref cloak, Marshal.SizeOf(typeof(int)));
                    var stagedStyle = (originalStyle.ToInt64() | WsExToolwindow) & ~WsExAppwindow;
                    _stagedOriginalStyles[hwnd] = originalStyle;
                    SetWindowExStyle(hwnd, new IntPtr(stagedStyle));
                    SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                        SwpNomove | SwpNosize | SwpNozorder | SwpNoactivate | SwpFramechanged);
                }

                if (eventType == EventObjectShow)
                    _stagedShowTimes[hwnd] = DateTime.UtcNow;
            }
        }

        private void MonitorWindows()
        {
            _processAttached.WaitOne();
            try
            {
                while (Interlocked.CompareExchange(ref _stopRequested, 0, 0) == 0)
                {
                    var processId = Interlocked.CompareExchange(ref _targetProcessId, 0, 0);
                    var windows = FindProcessWindows(processId);
                    foreach (var hwnd in windows)
                    {
                        var completed = IsCompleted(hwnd);
                        if (!completed && StagingActive && Win32.IsWindowVisible(hwnd))
                            MarkWindowShown(hwnd);

                        bool changed;
                        var propertiesReady = Win32.EnsureWindowAppProperties(
                            hwnd,
                            AppUserModelId,
                            _iconResource,
                            StagingActive && !completed,
                            out changed);
                        if (!completed && propertiesReady)
                            MarkStagedPropertiesReady(hwnd, changed);

                        if (!completed && propertiesReady && (!StagingActive || IsReadyToReveal(hwnd)))
                        {
                            RestoreStagedWindow(hwnd);
                            ShowWindowAsync(hwnd, Win32.SW_RESTORE);
                            Win32.SetForegroundWindow(hwnd);
                            _initialWindowReady.TrySetResult(true);
                        }
                    }

                    if (!IsProcessRunning(processId) && windows.Count == 0) break;
                    Thread.Sleep(PollIntervalMilliseconds);
                }
            }
            finally
            {
                _initialWindowReady.TrySetResult(false);
            }
        }

        private bool IsReadyToReveal(IntPtr hwnd)
        {
            lock (_sync)
            {
                DateTime showTime;
                DateTime propertyReadyTime;
                var now = DateTime.UtcNow;
                return _stagedShowTimes.TryGetValue(hwnd, out showTime)
                    && _stagedPropertyReadyTimes.TryGetValue(hwnd, out propertyReadyTime)
                    && now.Subtract(showTime).TotalMilliseconds >= RevealDelayMilliseconds
                    && now.Subtract(propertyReadyTime).TotalMilliseconds >= RevealDelayMilliseconds;
            }
        }

        private void MarkWindowShown(IntPtr hwnd)
        {
            lock (_sync)
            {
                if (_stagedOriginalStyles.ContainsKey(hwnd) && !_stagedShowTimes.ContainsKey(hwnd))
                    _stagedShowTimes[hwnd] = DateTime.UtcNow;
            }
        }

        private void MarkStagedPropertiesReady(IntPtr hwnd, bool changed)
        {
            lock (_sync)
            {
                if (changed || !_stagedPropertyReadyTimes.ContainsKey(hwnd))
                    _stagedPropertyReadyTimes[hwnd] = DateTime.UtcNow;
            }
        }

        private bool IsCompleted(IntPtr hwnd)
        {
            lock (_sync) return _completedHandles.Contains(hwnd);
        }

        private bool IsStaged(IntPtr hwnd)
        {
            lock (_sync) return _stagedOriginalStyles.ContainsKey(hwnd);
        }

        private void RestoreStagedWindow(IntPtr hwnd)
        {
            lock (_sync)
            {
                IntPtr originalStyle;
                if (_stagedOriginalStyles.TryGetValue(hwnd, out originalStyle) && IsWindow(hwnd))
                {
                    SetWindowExStyle(hwnd, originalStyle);
                    SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                        SwpNomove | SwpNosize | SwpNozorder | SwpNoactivate | SwpFramechanged);
                    var cloak = 0;
                    DwmSetWindowAttribute(hwnd, DwmwaCloak, ref cloak, Marshal.SizeOf(typeof(int)));
                    DwmFlush();
                }
                _stagedOriginalStyles.Remove(hwnd);
                _stagedShowTimes.Remove(hwnd);
                _stagedPropertyReadyTimes.Remove(hwnd);
                _completedHandles.Add(hwnd);
            }
        }

        private void RestoreForeignStagedWindows(int processId)
        {
            var handles = new List<IntPtr>(_stagedOriginalStyles.Keys);
            foreach (var hwnd in handles)
            {
                Win32.GetWindowThreadProcessId(hwnd, out var ownerProcessId);
                if (ownerProcessId != processId) RestoreStagedWindow(hwnd);
            }
        }

        private void RestoreAllStagedWindows()
        {
            IntPtr[] handles;
            lock (_sync)
            {
                handles = new IntPtr[_stagedOriginalStyles.Count];
                _stagedOriginalStyles.Keys.CopyTo(handles, 0);
            }
            foreach (var hwnd in handles)
            {
                RestoreStagedWindow(hwnd);
                if (IsWindow(hwnd)) ShowWindowAsync(hwnd, Win32.SW_RESTORE);
            }
        }

        private HashSet<IntPtr> SnapshotChromeWindowHandles()
        {
            var result = new HashSet<IntPtr>();
            Win32.EnumWindows((hwnd, _) =>
            {
                if (IsChromeWindow(hwnd)) result.Add(hwnd);
                return true;
            }, IntPtr.Zero);
            return result;
        }

        private List<IntPtr> FindProcessWindows(int processId)
        {
            var result = new List<IntPtr>();
            if (processId <= 0) return result;
            Win32.EnumWindows((hwnd, _) =>
            {
                Win32.GetWindowThreadProcessId(hwnd, out var ownerProcessId);
                if (ownerProcessId != processId || !IsChromeWindow(hwnd)) return true;
                if (Win32.IsWindowVisible(hwnd) || IsStaged(hwnd)) result.Add(hwnd);
                return true;
            }, IntPtr.Zero);
            return result;
        }

        private static bool IsChromeWindow(IntPtr hwnd)
        {
            var className = new StringBuilder(256);
            Win32.GetClassName(hwnd, className, className.Capacity);
            return className.ToString().StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal);
        }

        private bool MatchesExecutable(int processId)
        {
            try
            {
                var actualPath = Process.GetProcessById(processId).MainModule.FileName;
                return string.Equals(_executablePath, Path.GetFullPath(actualPath), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static bool IsProcessRunning(int processId)
        {
            try { return processId > 0 && !Process.GetProcessById(processId).HasExited; }
            catch { return false; }
        }

        private static void PumpWindowMessages()
        {
            NativeMessage message;
            while (PeekMessage(out message, IntPtr.Zero, 0, 0, 1))
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
            }
            Interlocked.Exchange(ref _stopRequested, 1);
            _processAttached.Set();
            if (_eventThread != null && _eventThread.IsAlive && Thread.CurrentThread != _eventThread)
                _eventThread.Join(3000);
            if (_monitorThread != null && _monitorThread.IsAlive && Thread.CurrentThread != _monitorThread)
                _monitorThread.Join(3000);
            RestoreAllStagedWindows();
            _initialWindowReady.TrySetResult(false);
            _eventReady.Dispose();
            _processAttached.Dispose();
            _winEventDelegate = null;
        }

        private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd, int objectId,
            int childId, uint eventThread, uint eventTime);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMessage
        {
            public IntPtr Hwnd;
            public uint Message;
            public UIntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public NativePoint Point;
            public uint Private;
        }

        private static IntPtr GetWindowExStyle(IntPtr hwnd) =>
            IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, GwlExstyle) : new IntPtr(GetWindowLong32(hwnd, GwlExstyle));

        private static IntPtr SetWindowExStyle(IntPtr hwnd, IntPtr value) =>
            IntPtr.Size == 8
                ? SetWindowLongPtr64(hwnd, GwlExstyle, value)
                : new IntPtr(SetWindowLong32(hwnd, GwlExstyle, value.ToInt32()));

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module,
            WinEventDelegate callback, uint processId, uint threadId, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PeekMessage(out NativeMessage message, IntPtr hwnd,
            uint filterMin, uint filterMax, uint remove);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TranslateMessage(ref NativeMessage message);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref NativeMessage message);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y,
            int width, int height, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindowAsync(IntPtr hwnd, int command);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attribute,
            ref int value, int valueSize);

        [DllImport("dwmapi.dll")]
        private static extern int DwmFlush();
    }
}
