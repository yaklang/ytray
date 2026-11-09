#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using YTray.Core.SystemControl;
using YTray.Models;

namespace YTray.Tests
{
    [TestClass]
    public class SystemControlLifecycleTests
    {
        private static string Fake => Environment.GetEnvironmentVariable("YTRAY_FAKE_DRIVER_EXE") ?? Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "FakeDriver", "bin", "Debug", "net10.0-windows10.0.17763.0", "FakeDriver.exe"));
        private static string Host => IntPtr.Size == 8 ? Fake : Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "FakeDriver", "bin", "x86", "Debug", "FakeDriver.exe"));
        private static Dictionary<string, string> EnvironmentBlock() { var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); foreach (DictionaryEntry pair in Environment.GetEnvironmentVariables()) result[(string)pair.Key] = pair.Value as string ?? ""; return result; }
        [TestMethod, TestCategory("ControlLifecycle")]
        public async Task RealStdioCorrelatesOutOfOrderResponsesAndDrainsStderr()
        {
            Assert.IsTrue(File.Exists(Fake), "Build windows/tests/FakeDriver/FakeDriver.csproj first");
            using var transport = new CuaDriverConnection(); await transport.StartAsync(Fake, EnvironmentBlock(), CancellationToken.None);
            var slow = transport.RequestAsync("test/echo", new JObject { ["value"] = "中文🚀", ["delay"] = 150 }, CancellationToken.None);
            var fast = transport.RequestAsync("test/echo", new JObject { ["value"] = "fast" }, CancellationToken.None);
            Assert.AreEqual("fast", (string?)(await fast)["value"]); Assert.AreEqual("中文🚀", (string?)(await slow)["value"]);
            var flooded = await transport.RequestAsync("test/stderr", new JObject(), CancellationToken.None);
            Assert.IsTrue((bool)flooded["ok"]!); Assert.IsTrue(transport.DiagnosticTail.Length <= 65536);
        }
        [TestMethod, TestCategory("ControlLifecycle")]
        public async Task TimeoutCompletesPendingAndJobKillsChildWithoutKillingCaller()
        {
            using var transport = new CuaDriverConnection(); await transport.StartAsync(Fake, EnvironmentBlock(), CancellationToken.None);
            var response = await transport.RequestAsync("test/child", new JObject(), CancellationToken.None); using var child = Process.GetProcessById((int)response["pid"]!);
            var pending = transport.RequestAsync("test/hang", new JObject(), CancellationToken.None, 200);
            var error = await Assert.ThrowsExactlyAsync<ControlException>(async () => await pending);
            Assert.AreEqual("Unknown", error.ExecutionState); Assert.IsTrue(child.WaitForExit(3000), "Job left a helper behind");
        }
        [TestMethod, TestCategory("ControlLifecycle")]
        public async Task KillingHostAlsoKillsCreationBoundJobChild()
        {
            Assert.IsTrue(File.Exists(Host), "Build the matching architecture of the Job host harness first");
            using var host = Process.Start(new ProcessStartInfo(Host, "--job-host \"" + typeof(CuaDriverConnection).Assembly.Location + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            Assert.IsNotNull(host);
            try {
                var line = host!.StandardOutput.ReadLineAsync(); Assert.AreEqual(line, await Task.WhenAny(line, Task.Delay(5000)), "Job harness did not start");
                using var child = Process.GetProcessById(int.Parse((await line)!));
                host.Kill(); Assert.IsTrue(host.WaitForExit(3000)); Assert.IsTrue(child.WaitForExit(3000), "Hard-killed host left a suspended or running child behind");
            } finally { if (!host!.HasExited) { host.Kill(); host.WaitForExit(3000); } }
        }
        [TestMethod, TestCategory("ControlLifecycle")]
        public async Task BadFrameFailsAllPendingAndRuntimeCannotBeReused()
        {
            using var transport = new CuaDriverConnection(); await transport.StartAsync(Fake, EnvironmentBlock(), CancellationToken.None);
            var hanging = transport.RequestAsync("test/hang", new JObject(), CancellationToken.None);
            await Assert.ThrowsExactlyAsync<ControlException>(() => transport.RequestAsync("test/badframe", new JObject(), CancellationToken.None));
            await Assert.ThrowsExactlyAsync<ControlException>(async () => await hanging);
            await Assert.ThrowsExactlyAsync<ControlException>(() => transport.RequestAsync("test/echo", new JObject(), CancellationToken.None));
        }
    }
}
