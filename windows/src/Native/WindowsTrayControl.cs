#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using Newtonsoft.Json.Linq;
using YTray.Core.SystemControl;
using YTray.Models;

namespace YTray.Native
{
    internal static class WindowsTrayControl
    {
        private const string NotifyIconId = "NotifyItemIcon";
        private const string OverflowClass = "TopLevelWindowForOverflowXamlIsland";
        private const string OpenManually = "可在 Windows 通知区点击该应用的图标打开主窗口。";

        internal static JObject Run(JObject arguments, bool restore, WaitHandle? cancellation, Action<string> stage)
        {
            ControlCancellation.Check(cancellation);
            WindowsControlTarget.RequireDesktop();
            var path = (string?)arguments["executable"] ?? "";
            var labels = (arguments["labels"] as JArray ?? new JArray()).Values<string>().OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool MatchesLabel(string name) => labels.Contains(name);
            var processes = (arguments["processes"] as JArray ?? new JArray()).OfType<JObject>()
                .Select(item => (item.Value<int>("pid"), item.Value<long>("created"))).ToHashSet();
            if (!InstancesStillRunning(path, processes)) throw new ControlException("TargetChanged", "原有应用进程已退出，请重新查找应用。");

            var taskbar = FindWindow("Shell_TrayWnd", null);
            GetWindowThreadProcessId(GetShellWindow(), out var shellPid);
            if (!ShellWindowIsVisible(taskbar, shellPid)) {
                if (!restore) return Availability(false);
                throw new ControlException("TrayIconUnavailable", "Windows 通知区暂不可用。" + OpenManually);
            }

            InvokePattern? overflowToggle = null;
            var invoked = false;
            try {
                var overflow = VisibleOverflow(shellPid);
                var matches = FindButtons(taskbar, NotifyIconId, MatchesLabel).Concat(FindButtons(overflow, NotifyIconId, MatchesLabel)).ToArray();
                if (!restore) return Availability(matches.Length == 1 && InvokePatternFor(matches[0], MatchesLabel) != null);
                if (matches.Length == 0 && overflow == IntPtr.Zero) {
                    var toggles = FindButtons(taskbar, "SystemTrayIcon", IsOverflowName);
                    if (toggles.Length == 1 && InvokePatternFor(toggles[0], IsOverflowName) is InvokePattern toggle) {
                        stage("open_tray_overflow");
                        ControlCancellation.Check(cancellation);
                        overflowToggle = toggle;
                        toggle.Invoke();
                        var wait = Stopwatch.StartNew();
                        do {
                            ControlCancellation.Check(cancellation);
                            matches = FindButtons(VisibleOverflow(shellPid), NotifyIconId, MatchesLabel);
                            if (matches.Length != 0) break;
                            Thread.Sleep(40);
                        } while (wait.ElapsedMilliseconds < 750);
                    }
                }

                // A user can have opened the window while the notification area was being read.
                if (WindowsApplicationControl.FindVisibleAppWindow(path, processes) is WindowsControlTarget open) return Window(open, false);
                if (matches.Length > 1) throw new ControlException("TrayIconAmbiguous", "通知区有多个同名图标。" + OpenManually);
                if (matches.Length == 0 || InvokePatternFor(matches[0], MatchesLabel) is not InvokePattern invoke)
                    throw new ControlException("TrayIconUnavailable", "暂未找到与应用标签匹配且可调用的托盘图标。" + OpenManually);
                if (!InstancesStillRunning(path, processes)) throw new ControlException("TargetChanged", "原有应用进程已退出，请重新查找应用。");

                ControlCancellation.Check(cancellation);
                stage("restore_tray");
                invoked = true;
                invoke.Invoke();
                stage("wait_tray_window");
                var clock = Stopwatch.StartNew();
                do {
                    ControlCancellation.Check(cancellation, dispatched: true);
                    if (WindowsApplicationControl.FindVisibleAppWindow(path, processes) is WindowsControlTarget window) return Window(window, true);
                    Thread.Sleep(45);
                } while (clock.ElapsedMilliseconds < 2500);
                throw new ControlException("TrayWindowNotReady", "托盘打开动作已发出，暂未确认原进程的主窗口。请先用 list_windows 核对。", "Unknown");
            } catch (Exception error) when (error is ElementNotAvailableException or InvalidOperationException or COMException or ArgumentException) {
                if (!restore) return Availability(false);
                throw new ControlException(invoked ? "TrayWindowNotReady" : "TrayIconUnavailable", invoked
                    ? "已尝试打开托盘图标，请先用 list_windows 核对窗口。"
                    : "Windows 通知区控件已变化。" + OpenManually, invoked ? "Unknown" : "NotStarted");
            } finally {
                if (overflowToggle != null && VisibleOverflow(shellPid) != IntPtr.Zero) {
                    try { overflowToggle.Invoke(); }
                    catch (Exception error) when (error is ElementNotAvailableException or InvalidOperationException or COMException) { }
                }
            }
        }

        private static bool InstancesStillRunning(string path, HashSet<(int, long)> processes) =>
            WindowsApplicationControl.RunningProcesses(new[] { path }).TryGetValue(path, out var current) && current.Any(processes.Contains);

        private static bool IsOverflowName(string name) => name == "显示隐藏的图标" || name == "Show hidden icons";

        private static AutomationElement[] FindButtons(IntPtr window, string automationId, Func<string, bool> matchName)
        {
            if (window == IntPtr.Zero) return Array.Empty<AutomationElement>();
            var request = new CacheRequest();
            request.Add(AutomationElement.NameProperty);
            request.Add(AutomationElement.IsEnabledProperty);
            request.Add(AutomationElement.IsOffscreenProperty);
            using (request.Activate()) {
                return AutomationElement.FromHandle(window).FindAll(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.AutomationIdProperty, automationId),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.ClassNameProperty, "SystemTray.NormalButton")))
                    .Cast<AutomationElement>().Where(element => element.Cached.IsEnabled && !element.Cached.IsOffscreen && matchName(element.Cached.Name.Trim())).ToArray();
            }
        }

        private static InvokePattern? InvokePatternFor(AutomationElement button, Func<string, bool> matchName)
        {
            var current = button.Current;
            if (current.IsOffscreen || !current.IsEnabled || !matchName(current.Name.Trim())) return null;
            return button.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern) ? pattern as InvokePattern : null;
        }
        private static JObject Availability(bool available) => ControlJson.Result(new JObject { ["available"] = available });
        private static JObject Window(WindowsControlTarget target, bool invoked) => ControlJson.Result(new JObject {
            ["window_id"] = target.Handle.ToInt64(), ["pid"] = target.Pid, ["created"] = target.Created, ["invoked"] = invoked
        });

        private static IntPtr VisibleOverflow(uint shellPid)
        {
            var window = FindWindow(OverflowClass, null);
            return ShellWindowIsVisible(window, shellPid) ? window : IntPtr.Zero;
        }

        private static bool ShellWindowIsVisible(IntPtr window, uint shellPid)
        {
            GetWindowThreadProcessId(window, out var pid);
            return window != IntPtr.Zero && shellPid != 0 && pid == shellPid && IsWindowVisible(window);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? title);
        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    }
}
