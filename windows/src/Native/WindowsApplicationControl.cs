#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using YTray.Core;
using YTray.Models;

namespace YTray.Native
{
    internal static class WindowsApplicationControl
    {
        internal sealed class App
        {
            internal string Name = "", Path = "";
            internal string Id => System.IO.Path.GetFileNameWithoutExtension(Path).ToLowerInvariant();
            internal JObject Json(IEnumerable<WindowsControlTarget> windows) => new JObject {
                ["app"] = Id, ["name"] = Name, ["executable"] = Path,
                ["windows"] = new JArray(windows.Where(w => SamePath(w.Executable, Path)).Select(WindowJson))
            };
        }
        private static List<App>? _installed;
        private static DateTime _scanned;
        internal static List<App> Installed()
        {
            lock (typeof(WindowsApplicationControl)) {
                if (_installed != null && DateTime.UtcNow - _scanned < TimeSpan.FromMinutes(2)) return _installed.ToList();
                var apps = new Dictionary<string, App>(StringComparer.OrdinalIgnoreCase);
                void Add(string? path, string? name = null) {
                    if (path == null || string.IsNullOrWhiteSpace(path)) return;
                    path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
                    if (!System.IO.Path.IsPathRooted(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return;
                    path = System.IO.Path.GetFullPath(path);
                    if (!apps.ContainsKey(path)) apps[path] = new App { Path = path, Name = name ?? DisplayName(path) };
                }
                foreach (var browser in SystemBrowserDiscovery.Discover()) Add(browser.ExecutablePath, browser.DisplayTitle);
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
        internal static App Find(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) throw new ControlException("AppNotFound", "请提供应用名称，例如 chrome 或 notepad");
            if (System.IO.Path.IsPathRooted(query) && query.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(query))
                return new App { Path = System.IO.Path.GetFullPath(query), Name = DisplayName(query) };
            var normalized = System.IO.Path.GetFileNameWithoutExtension(query.Trim());
            var apps = Installed();
            var matches = apps.Where(a => string.Equals(a.Id, normalized, StringComparison.OrdinalIgnoreCase)
                || string.Equals(a.Name, query, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0) matches = apps.Where(a => a.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length > 1 && matches.All(a => string.Equals(a.Name, matches[0].Name, StringComparison.OrdinalIgnoreCase))) return matches[0];
            if (matches.Length > 1 && normalized.Equals("chrome", StringComparison.OrdinalIgnoreCase)) {
                var regular = matches.Where(a => a.Path.Contains(@"\Google\Chrome\", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (regular.Length == 1) return regular[0];
            }
            if (matches.Length != 1) throw new ControlException(matches.Length == 0 ? "AppNotFound" : "AppAmbiguous", matches.Length == 0
                ? "未找到应用，请先 list_apps(query) 查看名称，或提供应用的 EXE 路径" : "找到多个应用，请使用 list_apps 返回的 executable 指定应用");
            return matches[0];
        }
        internal static async Task<WindowsControlTarget> LaunchAsync(App app, bool newWindow, string? url = null, Action? requireActive = null)
        {
            requireActive?.Invoke();
            var before = WindowsControlTarget.Enumerate().Select(w => w.Handle).ToHashSet();
            var browser = new[] { "chrome", "msedge", "chromium", "firefox" }.Contains(app.Id);
            Uri? destination = null;
            if (url != null && (!browser || !Uri.TryCreate(url, UriKind.Absolute, out destination) || destination.UserInfo.Length != 0
                || destination.Scheme != "https" && destination.Scheme != "http"))
                throw new ControlException("InvalidArguments", "url 仅支持浏览器的 http/https 地址");
            var start = new ProcessStartInfo(app.Path) { UseShellExecute = true, WorkingDirectory = System.IO.Path.GetDirectoryName(app.Path)! };
            if (newWindow && browser) start.Arguments = "--new-window";
            if (destination != null) start.Arguments += " \"" + destination.AbsoluteUri.Replace("\"", "%22") + "\"";
            requireActive?.Invoke();
            using var launched = Process.Start(start);
            try {
                var elapsed = Stopwatch.StartNew();
                while (elapsed.Elapsed < TimeSpan.FromSeconds(12)) {
                    requireActive?.Invoke();
                    var windows = WindowsControlTarget.Enumerate().Where(w => SamePath(w.Executable, app.Path)
                        || System.IO.Path.GetFileNameWithoutExtension(w.Executable).Equals(app.Id, StringComparison.OrdinalIgnoreCase)
                        || app.Id == "calc" && (w.Title == "计算器" || w.Title == "Calculator")).ToArray();
                    if (browser) windows = windows.Where(w => w.Bounds.Right - w.Bounds.Left >= 400 && w.Bounds.Bottom - w.Bounds.Top >= 300).ToArray();
                    windows = windows.OrderByDescending(w => (long)(w.Bounds.Right - w.Bounds.Left) * (w.Bounds.Bottom - w.Bounds.Top)).ToArray();
                    var fresh = windows.FirstOrDefault(w => !before.Contains(w.Handle));
                    // A second native instance can take time to create its HWND. Do
                    // not bind immediately to an older window from the same exe.
                    var reused = !newWindow || !browser && launched?.HasExited == true && elapsed.ElapsedMilliseconds >= 750;
                    var target = fresh ?? (reused ? windows.OrderByDescending(w => w.Handle == GetForegroundWindow()).FirstOrDefault() : null);
                    if (target != null) { await ActivateAsync(target, requireActive).ConfigureAwait(false); return WindowsControlTarget.Resolve(target.Handle); }
                    await Task.Delay(150).ConfigureAwait(false);
                }
                throw new ControlException("WindowNotReady", "应用已启动，但尚未出现可操作窗口；请 list_windows 查看", "Unknown");
            } catch (ControlException error) when (error.ExecutionState == "NotStarted") {
                // Process.Start has already succeeded. Losing the grant or failing
                // to activate/read a window must not suggest launching a second copy.
                throw new ControlException(error.Code, error.Message, "Unknown");
            }
        }
        internal static async Task ActivateAsync(WindowsControlTarget target, Action? requireActive = null)
        {
            requireActive?.Invoke();
            target.Revalidate();
            // Our topmost popover can cover a real target while window capture
            // still shows the underlying app. Dismiss it before foreground work
            // so a click cannot activate YTray instead of the requested control.
            var application = System.Windows.Application.Current;
            if (application != null && application.Dispatcher.Thread.IsAlive && !application.Dispatcher.HasShutdownStarted) {
                if (application.Dispatcher.CheckAccess()) Views.WidgetView.DismissForComputerUse();
                else await application.Dispatcher.InvokeAsync(Views.WidgetView.DismissForComputerUse, System.Windows.Threading.DispatcherPriority.Send);
            }
            requireActive?.Invoke();
            try {
                if (IsIconic(target.Handle)) ShowWindowAsync(target.Handle, 9);
                if (IsForeground(target.Handle)) return;
                var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
                var currentThread = GetCurrentThreadId();
                var attached = foregroundThread != 0 && foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);
                try { BringWindowToTop(target.Handle); SetForegroundWindow(target.Handle); }
                finally { if (attached) AttachThreadInput(currentThread, foregroundThread, false); }
                for (var attempt = 0; attempt < 10; attempt++) {
                    requireActive?.Invoke();
                    if (IsForeground(target.Handle)) return;
                    await Task.Delay(50).ConfigureAwait(false);
                }
                throw new ControlException("WindowNotActive", "窗口未能切到前台，已返回当前窗口信息", "Unknown");
            } catch (ControlException error) when (error.ExecutionState == "NotStarted") {
                throw new ControlException(error.Code, error.Message, "Unknown");
            }
        }
        private static bool IsForeground(IntPtr window) => GetAncestor(GetForegroundWindow(), 2) == GetAncestor(window, 2);
        internal static long ForegroundWindow => GetForegroundWindow().ToInt64();
        internal static bool UsesWpfKeyboardMessages(IntPtr window)
        {
            var name = new StringBuilder(256); GetClassName(window, name, name.Capacity);
            return name.ToString().StartsWith("HwndWrapper", StringComparison.Ordinal);
        }
        internal static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        internal static string DisplayName(string path) { try { return FileVersionInfo.GetVersionInfo(path).FileDescription ?? System.IO.Path.GetFileNameWithoutExtension(path); } catch { return System.IO.Path.GetFileNameWithoutExtension(path); } }
        internal static JObject WindowJson(WindowsControlTarget target) => new JObject {
            ["window_id"] = target.Handle.ToInt64(), ["pid"] = target.Pid, ["title"] = target.Title,
            ["app"] = System.IO.Path.GetFileNameWithoutExtension(target.Executable), ["executable"] = target.Executable
        };
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    }
}
