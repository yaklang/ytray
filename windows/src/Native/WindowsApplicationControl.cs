#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using YTray.Core;
using YTray.Core.SystemControl;
using YTray.Models;

namespace YTray.Native
{
    internal static class WindowsApplicationControl
    {
        internal sealed class App
        {
            internal string Name = "", Path = "";
            internal bool IsBrowser;
            internal readonly HashSet<string> Labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal string Id => System.IO.Path.GetFileNameWithoutExtension(Path).ToLowerInvariant();
            internal string[] NotificationLabels()
            {
                var labels = new HashSet<string>(Labels, StringComparer.OrdinalIgnoreCase) { Name, Id };
                try {
                    var version = FileVersionInfo.GetVersionInfo(Path);
                    if (!string.IsNullOrWhiteSpace(version.FileDescription)) labels.Add(version.FileDescription!);
                    if (!string.IsNullOrWhiteSpace(version.ProductName)) labels.Add(version.ProductName!);
                } catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException or ArgumentException) { }
                return labels.Where(label => !string.IsNullOrWhiteSpace(label)).Select(label => label.Trim()).ToArray();
            }
            internal JObject Json(WindowsControlTarget[] windows, bool? running, bool trayIconAvailable)
            {
                var item = new JObject { ["app"] = Id, ["name"] = Name, ["executable"] = Path, ["windows"] = new JArray(windows.Select(WindowJson)) };
                if (running.HasValue) {
                    item["running"] = running.Value || windows.Length != 0;
                    item["window_status"] = windows.Length != 0 ? "window_available" : trayIconAvailable ? "in_tray" : running.Value ? "running_without_window" : "not_running";
                }
                return item;
            }
        }
        private static List<App>? _installed;
        private static DateTime _scanned;
        internal static List<App> Installed()
        {
            lock (typeof(WindowsApplicationControl)) {
                if (_installed != null && DateTime.UtcNow - _scanned < TimeSpan.FromMinutes(2)) return _installed.ToList();
                var apps = new Dictionary<string, App>(StringComparer.OrdinalIgnoreCase);
                void Add(string? path, string? name = null, bool browser = false) {
                    if (path == null || string.IsNullOrWhiteSpace(path)) return;
                    path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
                    if (!System.IO.Path.IsPathRooted(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return;
                    path = System.IO.Path.GetFullPath(path);
                    if (!apps.TryGetValue(path, out var app)) apps.Add(path, app = new App { Path = path, Name = name ?? DisplayName(path) });
                    if (!string.IsNullOrWhiteSpace(name)) app.Labels.Add(name!);
                    app.IsBrowser |= browser;
                }
                foreach (var browser in SystemBrowserDiscovery.Discover()) Add(browser.ExecutablePath, browser.DisplayTitle, browser: true);
                foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine }) foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 }) {
                    try {
                        using var root = RegistryKey.OpenBaseKey(hive, view);
                        using var paths = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
                        foreach (var keyName in paths?.GetSubKeyNames() ?? Array.Empty<string>()) {
                            using var key = paths!.OpenSubKey(keyName); Add(key?.GetValue(null) as string);
                        }
                    } catch { }
                }
                foreach (var folder in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms }) {
                    var directory = Environment.GetFolderPath(folder);
                    try {
                        foreach (var link in Directory.EnumerateFiles(directory, "*.lnk", SearchOption.AllDirectories).Take(1000)) {
                            var shortcut = ShellLink.ReadTargetAndArgs(link);
                            // A bare app executable is sufficient. Shortcut command lines never
                            // become an additional command-execution interface.
                            if (string.IsNullOrWhiteSpace(shortcut.arguments)) Add(shortcut.target, System.IO.Path.GetFileNameWithoutExtension(link));
                        }
                    } catch { }
                }
                foreach (var name in new[] { "notepad.exe", "calc.exe", "mspaint.exe", "explorer.exe" })
                    Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), name));
                Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"));
                Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "calc.exe"));
                _installed = apps.Values.OrderBy(a => a.Name).ToList(); _scanned = DateTime.UtcNow;
                return _installed.ToList();
            }
        }
        internal static List<App> Available(IEnumerable<WindowsControlTarget> windows)
        {
            var apps = Installed();
            var known = apps.Select(app => app.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var window in windows)
                if (known.Add(window.Executable))
                    apps.Add(new App { Name = DisplayName(window.Executable), Path = window.Executable });
            return apps;
        }
        internal static bool IsBrowserExecutable(string executable) => Installed().Any(app => app.IsBrowser && SamePath(app.Path, executable));

        internal static Dictionary<string, List<(int Pid, long Created)>> RunningProcesses(IEnumerable<string> executables)
        {
            var paths = new HashSet<string>(executables, StringComparer.OrdinalIgnoreCase);
            var result = new Dictionary<string, List<(int Pid, long Created)>>(StringComparer.OrdinalIgnoreCase);
            if (paths.Count == 0) return result;
            var names = new HashSet<string>(paths.Select(path => System.IO.Path.GetFileNameWithoutExtension(path)), StringComparer.OrdinalIgnoreCase);
            foreach (var process in Process.GetProcesses()) using (process) {
                try {
                    if (!names.Contains(process.ProcessName)) continue;
                    var path = WindowsControlTarget.ProcessExecutable(process);
                    if (!paths.Contains(path)) continue;
                    var instance = (process.Id, process.StartTime.ToUniversalTime().Ticks);
                    if (!result.TryGetValue(path, out var matches)) result.Add(path, matches = new List<(int, long)>());
                    matches.Add(instance);
                } catch (Exception error) when (error is Win32Exception or InvalidOperationException or ControlException or ArgumentException) { }
            }
            return result;
        }
        internal static App Find(string query, WindowsControlTarget[] windows)
        {
            if (string.IsNullOrWhiteSpace(query)) throw new ControlException("AppNotFound", "请提供应用名称，例如 chrome 或 notepad");
            var apps = Available(windows);
            if (System.IO.Path.IsPathRooted(query) && query.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) {
                var path = System.IO.Path.GetFullPath(query);
                var exact = apps.FirstOrDefault(app => SamePath(app.Path, path));
                if (exact != null) return exact;
                if (File.Exists(path)) return new App { Path = path, Name = DisplayName(path) };
            }
            var normalized = System.IO.Path.GetFileNameWithoutExtension(query.Trim());
            var matches = apps.Where(a => string.Equals(a.Id, normalized, StringComparison.OrdinalIgnoreCase)
                || string.Equals(a.Name, query, StringComparison.OrdinalIgnoreCase) || a.Labels.Contains(query)).ToArray();
            if (matches.Length == 0) matches = apps.Where(a => a.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length > 1) {
                var running = matches.Where(app => windows.Any(window => SamePath(window.Executable, app.Path))).ToArray();
                if (running.Length == 1) return running[0];
                if (running.Length == 0) {
                    var processes = RunningProcesses(matches.Select(app => app.Path));
                    var active = matches.Where(app => processes.ContainsKey(app.Path)).ToArray();
                    if (active.Length == 1) return active[0];
                    if (active.Length == 0 && matches.All(app => string.Equals(app.Name, matches[0].Name, StringComparison.OrdinalIgnoreCase))) return matches[0];
                }
            }
            if (matches.Length != 1) throw new ControlException(matches.Length == 0 ? "AppNotFound" : "AppAmbiguous", matches.Length == 0
                ? "未找到应用，请先 list_apps(query) 查看名称，或提供应用的 EXE 路径" : "找到多个应用，请使用 list_apps 返回的 executable 指定应用");
            return matches[0];
        }
        internal static ProcessStartInfo FolderStartInfo(string path)
        {
            const string invalidPath = "请输入 Windows 文件夹的绝对路径";
            string directory;
            try
            {
                var root = Path.GetPathRoot(path);
                if (root == null || root.Length < 3 || root.StartsWith(@"\\?\", StringComparison.Ordinal)
                    || root.StartsWith(@"\\.\", StringComparison.Ordinal))
                    throw new ControlException("InvalidPath", invalidPath);
                directory = Path.GetFullPath(path);
            }
            catch (Exception error) when (error is ArgumentException || error is NotSupportedException || error is PathTooLongException)
            {
                throw new ControlException("InvalidPath", invalidPath);
            }
            if (!Directory.Exists(directory))
                throw new ControlException("FolderUnavailable", "找不到可访问的文件夹，请检查路径");
            return new ProcessStartInfo { FileName = directory, Verb = "open", UseShellExecute = true };
        }

        internal static async Task<(WindowsControlTarget Target, bool Reused, bool RestoredFromTray)> LaunchAsync(App app, bool newWindow, string? url = null,
            CancellationToken cancellation = default, Action<string, WindowsControlTarget?>? progress = null,
            Func<App, List<(int Pid, long Created)>, Task<(WindowsControlTarget Target, bool Invoked)>>? restoreTray = null)
        {
            ControlCancellation.Check(cancellation);
            Uri? destination = null;
            if (url != null && (!app.IsBrowser || !Uri.TryCreate(url, UriKind.Absolute, out destination) || destination.UserInfo.Length != 0
                || destination.Scheme != "https" && destination.Scheme != "http"))
                throw new ControlException("InvalidArguments", "url 仅支持浏览器的 http/https 地址");
            var before = WindowsControlTarget.Enumerate(app.Path);
            if (!newWindow && destination == null) {
                var existing = OrderAppWindows(before).FirstOrDefault();
                if (existing != null) {
                    progress?.Invoke("activate", existing);
                    await ActivateAsync(existing, cancellation).ConfigureAwait(false);
                    return (WindowsControlTarget.Resolve(existing.Handle), true, false);
                }
                if (restoreTray != null && RunningProcesses(new[] { app.Path }).TryGetValue(app.Path, out var running)) {
                    (WindowsControlTarget Target, bool Invoked)? restored = null;
                    try {
                        restored = await restoreTray(app, running).ConfigureAwait(false);
                    } catch (ControlException error) when (error.ExecutionState == "NotStarted" && error.Code is ("TrayIconUnavailable" or "TrayScopeUnavailable")) { }
                    if (restored is { } result) try {
                        if (!IsForeground(result.Target.Handle)) {
                            progress?.Invoke("activate", result.Target);
                            await ActivateAsync(result.Target, cancellation).ConfigureAwait(false);
                        }
                        result.Target.Revalidate();
                        return (result.Target, true, result.Invoked);
                    } catch (ControlException error) when (result.Invoked && error.ExecutionState == "NotStarted") {
                        throw new ControlException(error.Code, error.Message, "Unknown");
                    }
                }
            }

            var existingHandles = before.Select(window => window.Handle).ToHashSet();
            var start = new ProcessStartInfo(app.Path) { UseShellExecute = true, WorkingDirectory = System.IO.Path.GetDirectoryName(app.Path)! };
            if (newWindow && app.IsBrowser) start.Arguments = "--new-window";
            if (destination != null) start.Arguments += " \"" + destination.AbsoluteUri.Replace("\"", "%22") + "\"";
            ControlCancellation.Check(cancellation);
            progress?.Invoke("launch", null);
            using var launched = Process.Start(start);
            (int Pid, long Created, string Path)? redirected = null;
            if (launched != null) try {
                var actualPath = WindowsControlTarget.ProcessExecutable(launched);
                if (!SamePath(actualPath, app.Path))
                    redirected = (launched.Id, launched.StartTime.ToUniversalTime().Ticks, actualPath);
            } catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException or ControlException) { }
            var elapsed = Stopwatch.StartNew();
            try { while (elapsed.Elapsed < TimeSpan.FromSeconds(12)) {
                ControlCancellation.Check(cancellation, "Unknown");
                IEnumerable<WindowsControlTarget> candidates = WindowsControlTarget.Enumerate(app.Path);
                if (redirected is { } instance)
                    candidates = candidates.Concat(WindowsControlTarget.Enumerate(instance.Path)
                        .Where(window => window.Pid == instance.Pid && window.Created == instance.Created));
                if (app.IsBrowser) candidates = candidates.Where(window => window.Bounds.Right - window.Bounds.Left >= 400 && window.Bounds.Bottom - window.Bounds.Top >= 300);
                var windows = OrderAppWindows(candidates).ToArray();
                var fresh = windows.FirstOrDefault(window => !existingHandles.Contains(window.Handle));
                // A second native instance can take time to create its HWND. Do
                // not bind immediately to an older window from the same exe.
                var reused = !newWindow || !app.IsBrowser && launched?.HasExited == true && elapsed.ElapsedMilliseconds >= 750;
                var target = fresh ?? (reused ? windows.OrderByDescending(window => window.Handle == GetForegroundWindow()).FirstOrDefault() : null);
                if (target != null) {
                    progress?.Invoke("activate", target);
                    await ActivateAsync(target, cancellation).ConfigureAwait(false);
                    return (WindowsControlTarget.Resolve(target.Handle), existingHandles.Contains(target.Handle), false);
                }
                await Task.Delay(150, cancellation).ConfigureAwait(false);
            } } catch (Exception error) when (cancellation.IsCancellationRequested && (error is OperationCanceledException || error is ControlException)) {
                throw ControlCancellation.Error("Unknown");
            } catch (ControlException error) when (error.ExecutionState == "NotStarted") {
                throw new ControlException(error.Code, "应用启动请求已提交。" + error.Message, "Unknown");
            }
            throw new ControlException("WindowNotReady", "应用已启动，但尚未出现可操作窗口；请 list_windows 查看", "Unknown");
        }
        private static IOrderedEnumerable<WindowsControlTarget> OrderAppWindows(IEnumerable<WindowsControlTarget> windows)
        {
            var foreground = GetForegroundWindow();
            return windows.OrderBy(window => GetWindow(window.Handle, 4) != IntPtr.Zero)
                .ThenByDescending(WindowArea).ThenByDescending(window => window.Handle == foreground);
        }
        internal static WindowsControlTarget? FindVisibleAppWindow(string path, HashSet<(int Pid, long Created)> processes) =>
            OrderAppWindows(WindowsControlTarget.Enumerate(path).Where(window =>
                processes.Contains((window.Pid, window.Created)) && window.IsDisplayed)).FirstOrDefault();
        private static long WindowArea(WindowsControlTarget window)
        {
            var bounds = window.Bounds;
            if (IsIconic(window.Handle)) {
                var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
                if (GetWindowPlacement(window.Handle, ref placement)) bounds = placement.NormalPosition;
            }
            return (long)(bounds.Right - bounds.Left) * (bounds.Bottom - bounds.Top);
        }
        internal static async Task ActivateAsync(WindowsControlTarget target, CancellationToken cancellation = default)
        {
            ControlCancellation.Check(cancellation);
            target.Revalidate();
            // Our topmost popover can cover a real target while window capture
            // still shows the underlying app. Dismiss it before foreground work
            // so a click cannot activate YTray instead of the requested control.
            var application = System.Windows.Application.Current;
            if (application != null && application.Dispatcher.Thread.IsAlive && !application.Dispatcher.HasShutdownStarted) {
                if (application.Dispatcher.CheckAccess()) Views.WidgetView.DismissForComputerUse();
                else await application.Dispatcher.InvokeAsync(Views.WidgetView.DismissForComputerUse, System.Windows.Threading.DispatcherPriority.Send, cancellation);
            }
            ControlCancellation.Check(cancellation);
            try
            {
                if (await TryBringToForegroundAsync(target.Handle, cancellation).ConfigureAwait(false)) return;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw ControlCancellation.Error("Unknown"); }
            throw new ControlException("WindowNotActive", "窗口未能切到前台，已返回当前窗口信息");
        }

        internal static async Task<bool> TryBringToForegroundAsync(IntPtr window, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (IsIconic(window)) ShowWindowAsync(window, 9);
            if (IsForeground(window)) return true;
            var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            var currentThread = GetCurrentThreadId();
            var attached = foregroundThread != 0 && foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);
            try { BringWindowToTop(window); SetForegroundWindow(window); }
            finally { if (attached) AttachThreadInput(currentThread, foregroundThread, false); }
            for (var attempt = 0; attempt < 10; attempt++) {
                if (IsForeground(window)) return true;
                await Task.Delay(50, cancellation).ConfigureAwait(false);
            }
            return false;
        }
        internal static bool IsForeground(IntPtr window)
        {
            var root = GetAncestor(window, 2);
            return root != IntPtr.Zero && GetAncestor(GetForegroundWindow(), 2) == root;
        }
        internal static bool IsMinimized(IntPtr window) => IsIconic(window);
        internal static long ForegroundWindow => GetForegroundWindow().ToInt64();
        internal static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        internal static string DisplayName(string path) { try { return FileVersionInfo.GetVersionInfo(path).FileDescription ?? System.IO.Path.GetFileNameWithoutExtension(path); } catch { return System.IO.Path.GetFileNameWithoutExtension(path); } }
        internal static JObject WindowJson(WindowsControlTarget target) => new JObject {
            ["window_id"] = target.Handle.ToInt64(), ["pid"] = target.Pid, ["title"] = target.Title,
            ["app"] = System.IO.Path.GetFileNameWithoutExtension(target.Executable), ["executable"] = target.Executable
        };
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr window, ref WindowPlacement placement);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [StructLayout(LayoutKind.Sequential)]
        private struct WindowPlacement
        {
            internal int Length, Flags, ShowCommand;
            internal WindowsAutomation.NativePoint MinimumPosition, MaximumPosition;
            internal WindowsControlTarget.Rect NormalPosition;
        }
    }
}
