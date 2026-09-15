#nullable enable
using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;

namespace YTray.Core
{
    internal enum NativeUpdateResult { Failed, Cancelled, Current }

    /// <summary>YConnect's WinSparkle integration, with pinned x64 and x86 engines.</summary>
    internal sealed class NativeAppUpdater : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _stateTimer;
        private readonly Func<bool> _canInstall;
        private readonly Action<NativeUpdateResult, string> _finished;
        private volatile bool _canExit, _disposed;
        // Keep all delegates rooted until native cleanup completes.
        private readonly Callback _onError, _onCancelled, _onNoUpdate, _onShutdown;
        private readonly CanShutdown _canShutdown;
        private static IntPtr _library;

        internal NativeAppUpdater(string version, Func<bool> canInstall, Action<NativeUpdateResult, string> finished)
        {
            _dispatcher = Application.Current.Dispatcher;
            _canInstall = canInstall;
            _finished = finished;
            EnsureLoaded();
            win_sparkle_set_app_details("YakLang", "YTray", version);
            var architecture = Environment.Is64BitProcess ? "amd64" : "386";
            win_sparkle_set_appcast_url("https://aliyun-oss.yaklang.com/ytray/appcast-windows-" + architecture + ".xml");
            using (var stream = typeof(NativeAppUpdater).Assembly.GetManifestResourceStream("YTray.UpdatePublicKey")
                ?? throw new InvalidDataException("更新公钥缺失"))
            using (var reader = new StreamReader(stream))
                if (win_sparkle_set_eddsa_public_key(reader.ReadToEnd().Trim()) != 1) throw new InvalidDataException("更新公钥无效");
            win_sparkle_set_lang("zh_CN");
            win_sparkle_set_automatic_check_for_updates(0);
            _onError = () => Post(() => _finished(NativeUpdateResult.Failed, "下载或签名校验失败，未安装更新。请重试或手动下载。"));
            _onCancelled = () => Post(() => _finished(NativeUpdateResult.Cancelled, "已取消更新，可以继续使用当前版本。"));
            _onNoUpdate = () => Post(() => _finished(NativeUpdateResult.Current, "已经是最新版本。"));
            _canShutdown = () => _canExit && !_disposed ? 1 : 0;
            _onShutdown = () => Post(() =>
            {
                if (_canInstall()) Application.Current.Shutdown();
                else _finished(NativeUpdateResult.Failed, "请先完成当前操作，再重试安装更新。");
            });
            win_sparkle_set_error_callback(_onError);
            win_sparkle_set_update_cancelled_callback(_onCancelled);
            win_sparkle_set_did_not_find_update_callback(_onNoUpdate);
            win_sparkle_set_can_shutdown_callback(_canShutdown);
            win_sparkle_set_shutdown_request_callback(_onShutdown);
            _stateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _stateTimer.Tick += (sender, args) => _canExit = _canInstall();
            _canExit = _canInstall();
            _stateTimer.Start();
            win_sparkle_init();
        }

        internal void Install() { _canExit = _canInstall(); win_sparkle_check_update_with_ui_and_install(); }
        private void Post(Action action) { if (!_disposed && !_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(action); }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stateTimer.Stop();
            win_sparkle_cleanup();
            GC.KeepAlive(this);
        }

        internal static string EngineHash => Environment.Is64BitProcess
            ? "9b43b1c16ee39fb9a91b5bd75138767898779510e0836be2919250607cdbe8ab"
            : "6837653b02e2c3acf83ae5c76867c370c9bd83d14782d1f1e8a8c093c6c0fdf7";

        internal static byte[] VerifiedEngineBytes()
        {
            using (var resource = typeof(NativeAppUpdater).Assembly.GetManifestResourceStream("YTray.WinSparkle")
                ?? throw new InvalidDataException("更新组件缺失"))
            using (var memory = new MemoryStream())
            {
                resource.CopyTo(memory);
                var bytes = memory.ToArray();
                if (Hash(bytes) != EngineHash) throw new InvalidDataException("更新组件校验失败");
                return bytes;
            }
        }

        private static string Hash(byte[] bytes)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        internal static void EnsureLoaded()
        {
            if (_library != IntPtr.Zero) return;
            var bytes = VerifiedEngineBytes();
            // Extract only an embedded, pinned engine. Remote metadata never selects a DLL.
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "YTray", "UpdateEngine", EngineHash);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "WinSparkle.dll");
            if (!File.Exists(path))
            {
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllBytes(temporary, bytes);
                    try { File.Move(temporary, path); }
                    catch (IOException) when (File.Exists(path)) { }
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            if (Hash(File.ReadAllBytes(path)) != EngineHash) throw new InvalidDataException("本地更新组件校验失败");
            // Search dependencies only beside this absolute DLL and in System32.
            _library = LoadLibraryEx(path, IntPtr.Zero, 0x100 | 0x800);
            if (_library == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Callback();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CanShutdown();
        [DllImport("WinSparkle.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void win_sparkle_init();
        [DllImport("WinSparkle.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void win_sparkle_cleanup();
        [DllImport("WinSparkle.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] private static extern void win_sparkle_set_app_details(string company, string app, string version);
        [DllImport("WinSparkle.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private static extern void win_sparkle_set_appcast_url(string url);
        [DllImport("WinSparkle.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private static extern int win_sparkle_set_eddsa_public_key(string key);
        [DllImport("WinSparkle.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private static extern void win_sparkle_set_lang(string language);
        [DllImport("WinSparkle.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void win_sparkle_set_automatic_check_for_updates(int value);
        [DllImport("WinSparkle.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void win_sparkle_check_update_with_ui_and_install();
        [DllImport("WinSparkle.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void win_sparkle_set_error_callback(Callback callback);
        [DllImport("WinSparkle.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void win_sparkle_set_update_cancelled_callback(Callback callback);
        [DllImport("WinSparkle.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void win_sparkle_set_did_not_find_update_callback(Callback callback);
        [DllImport("WinSparkle.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void win_sparkle_set_can_shutdown_callback(CanShutdown callback);
        [DllImport("WinSparkle.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void win_sparkle_set_shutdown_request_callback(Callback callback);
    }
}
