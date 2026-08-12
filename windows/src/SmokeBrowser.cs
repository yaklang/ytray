using System;
using System.IO;
using System.Threading.Tasks;
using YTray.Core;
using YTray.Models;
using YTray.Native;

namespace YTray
{
    /// <summary>
    /// CLI smoke mode: launch a real Chrome instance and verify AUMID resolution + CDP screenshot.
    /// Mirrors macOS --smoke-browser.
    /// </summary>
    internal static class SmokeBrowser
    {
        public static async Task RunAsync(string executablePath)
        {
            var scratch = Path.Combine(Path.GetTempPath(), "ytray-smoke-" + Guid.NewGuid());
            Directory.CreateDirectory(scratch);
            var runtime = new BrowserRuntime
            {
                Name = "Smoke Chrome",
                Version = "local",
                Architecture = "windows",
                ExecutablePath = executablePath,
                Source = RuntimeSource.Local,
                BrowserKind = BrowserKind.Chrome,
            };
            var settings = new LaunchSettings
            {
                HomeURL = "data:text/html,%3Ctitle%3EYTray%20Smoke%3C/title%3E%3Ch1%3EYTray%3C/h1%3E",
                DebugPort = 17777,
            };

            try
            {
                var launched = BrowserLauncher.Launch(runtime, LaunchMode.Isolated, settings, new System.Collections.Generic.List<BrowserPlugin>(), scratch, 1, "A");
                Console.WriteLine($"launched pid={launched.Instance.ProcessID} port={launched.Instance.DebugPort} badge=A");

                var ready = await ScreenshotService.WaitUntilReadyAsync(launched.Instance.DebugPort, 60);
                Console.WriteLine($"debugger ready={ready}");
                if (!ready) { Console.Error.WriteLine("smoke failed: debugger not ready"); launched.Process.Kill(); return; }

                var title = await ScreenshotService.CurrentPageTitleAsync(launched.Instance.DebugPort, 30);
                var screenshotDir = Path.Combine(scratch, "Screenshots");
                var shot = await ScreenshotService.CaptureAsync(launched.Instance.DebugPort, launched.Instance.Id, screenshotDir);
                var size = new FileInfo(shot).Length;

                // AUMID resolution
                var aumid = await AumidResolver.ResolveAsync(launched.Instance.ProcessID, launched.Instance.ProfilePath, BrowserKind.Chrome);
                Console.WriteLine($"aumid={aumid}");

                var iconApplied = BrowserProcessIcon.ApplyToProcessWindow(launched.Instance.Id,
                    launched.Instance.ProcessID, scratch, aumid, "YTray Smoke · A");
                var hwnd = WindowEnum.FindFirstVisibleWindow(launched.Instance.ProcessID);
                var liveIcon = hwnd == IntPtr.Zero ? IntPtr.Zero : Win32.SendMessage(hwnd,
                    Win32.WM_GETICON, (IntPtr)Win32.ICON_BIG, IntPtr.Zero);
                Console.WriteLine($"badged taskbar icon applied={iconApplied && liveIcon != IntPtr.Zero}");

                var ok = title == "YTray Smoke" && size > 0 && !string.IsNullOrEmpty(aumid)
                    && iconApplied && liveIcon != IntPtr.Zero;
                Console.WriteLine($"browser smoke {(ok ? "passed" : "failed")}: title={title} screenshot={size} aumid={aumid}");
                launched.Process.Kill();
                BrowserProcessIcon.Remove(launched.Instance.Id, scratch);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"browser smoke failed: {ex.Message}");
            }
            finally
            {
                try { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); } catch { }
            }
        }
    }

    /// <summary>CLI probe mode: launch Chrome and print the AUMID of its first window.</summary>
    internal static class AumidProbe
    {
        public static void Run(string executablePath)
        {
            var tmpProfile = Path.Combine(Path.GetTempPath(), "ytray-aumid-probe-" + Guid.NewGuid());
            Directory.CreateDirectory(tmpProfile);
            var psi = new System.Diagnostics.ProcessStartInfo(executablePath)
            {
                UseShellExecute = false,
                Arguments = $"--user-data-dir=\"{tmpProfile}\" --no-first-run --no-default-browser-check about:blank",
                CreateNoWindow = true,
            };
            var p = System.Diagnostics.Process.Start(psi);
            try
            {
                var aumid = WindowEnum.PollForWindowAumid(p.Id, TimeSpan.FromSeconds(15));
                var expectedBase = ShellLink.ResolveBaseAumid(BrowserKind.Chrome);
                var profileId = AumidResolver.ComputeProfileId(tmpProfile);
                var expected = AumidResolver.BuildExpectedAumid(expectedBase, profileId);
                Console.WriteLine($"pid={p.Id} profile={tmpProfile}");
                Console.WriteLine($"window-aumid={aumid}");
                Console.WriteLine($"expected-aumid={expected}");
                Console.WriteLine($"match={(aumid == expected ? "yes" : "no")}");
            }
            finally
            {
                try { p.Kill(); } catch { }
                try { if (Directory.Exists(tmpProfile)) Directory.Delete(tmpProfile, true); } catch { }
            }
        }
    }
}
