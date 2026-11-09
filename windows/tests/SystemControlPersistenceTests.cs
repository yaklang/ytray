#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using YTray.Core;
using YTray.Core.SystemControl;
using YTray.Models;

namespace YTray.Tests
{
    [TestClass]
    public class SystemControlPersistenceTests
    {
        [TestMethod, TestCategory("ControlAuthorization")]
        public void DefaultControlAllowsAllDesktopOperationsWithoutLeaseTimers()
        {
            var grant = new ControlSessionOptions();
            Assert.IsFalse(grant.ReadOnly); Assert.IsTrue(grant.AllowForeground); Assert.IsTrue(grant.AllowDesktop);
            Assert.AreEqual(0, grant.Executables.Length);
            Assert.IsFalse(SystemControlController.UsesResourceManifest(grant), "The all-app switch must not generate an empty app allowlist");
            Assert.IsTrue(SystemControlController.UsesResourceManifest(new ControlSessionOptions { Executables = new[] { @"C:\fixture.exe" } }));
            var manifest = SystemControlController.CapabilityManifest(grant);
            Assert.IsNull(manifest["expires_after"]); Assert.IsNull(manifest["idle_timeout"]);
            Assert.IsTrue(manifest["resources"]!["desktop"]!.Value<bool>("display"));
            var tools = (JArray)manifest["allow"]!["tools"]!;
            foreach (var name in new[] { "get_window_state", "get_desktop_state", "click", "type_text", "press_key", "scroll" }) Assert.IsTrue(tools.ToString().Contains("\"" + name + "\""));
            Assert.IsNull(SystemControlController.ToolPolicy(grant)["resources"]);
        }
        [TestMethod, TestCategory("ControlAuthorization")]
        public void CredentialSurvivesReloadEncryptedAndExplicitCloseRotatesIt()
        {
            var root = Path.Combine(Path.GetTempPath(), "ytray-control-key-" + Guid.NewGuid().ToString("N"));
            var first = new ControlAccessStore(root); var token = first.Open(false);
            Assert.AreEqual(token, new ControlAccessStore(root).Open(true));
            Assert.IsFalse(Encoding.UTF8.GetString(File.ReadAllBytes(first.Path)).Contains(token), "Credential was saved as plaintext");
            first.Clear(); Assert.AreNotEqual(token, new ControlAccessStore(root).Open(true));
        }
        [TestMethod, TestCategory("ControlAuthorization")]
        public void MasterSwitchSurvivesSettingsSaveAndReload()
        {
            var root = Path.Combine(Path.GetTempPath(), "ytray-control-setting-" + Guid.NewGuid().ToString("N"));
            using (var store = new InstanceStore(root, false, false)) { store.Settings.ComputerControlEnabled = true; store.SaveSettings(); Assert.IsTrue(store.Settings.Clone().ComputerControlEnabled); }
            using (var restored = new InstanceStore(root, false, false)) Assert.IsTrue(restored.Settings.ComputerControlEnabled);
        }
        [TestMethod, TestCategory("ControlAuthorization")]
        public void PermissionRepairRequestsNativeUacForTheExactCurrentExecutable()
        {
            using var current = Process.GetCurrentProcess(); var start = ControlElevation.RepairStartInfo();
            Assert.IsTrue(start.UseShellExecute); Assert.AreEqual("runas", start.Verb);
            Assert.AreEqual(current.MainModule!.FileName, start.FileName);
            StringAssert.Contains(start.Arguments, "--repair-computer-control");
            StringAssert.Contains(start.Arguments, "--replace-process " + current.Id);
        }
        [TestMethod, TestCategory("ControlAlwaysOn")]
        public async Task IdleCrashAndApplicationRestartKeepConfigurationUntilNativeSwitchOff()
        {
            if (Environment.GetEnvironmentVariable("YTRAY_CONTROL_LONGRUN") != "1") Assert.Inconclusive("Set YTRAY_CONTROL_LONGRUN=1 for the real-runtime persistence test");
            var root = Path.Combine(Path.GetTempPath(), "ytray-control-persistent-" + Guid.NewGuid().ToString("N"));
            string configuration, bearer;
            using (var controller = new SystemControlController(root)) {
                await controller.EnableAsync(); Assert.IsTrue(controller.IsRunning, controller.LastError);
                configuration = controller.ConfigurationJSON!; bearer = Bearer(configuration);
                // No requests or synthetic keepalives during the former two-minute idle limit.
                await Task.Delay(TimeSpan.FromSeconds(130));
                Assert.IsTrue(controller.IsEnabled); Assert.IsTrue(controller.Authorized(bearer, null)); Assert.AreEqual(configuration, controller.ConfigurationJSON);
                Assert.IsFalse((await controller.CallAsync("check_permissions", new JObject())).Value<bool>("isError"));
                var previousGeneration = controller.Generation;
                using (var driver = Process.GetProcessById(controller.DriverProcessId!.Value)) { driver.Kill(); Assert.IsTrue(driver.WaitForExit(3000)); }
                await Ready(() => controller.IsRunning && controller.Generation != previousGeneration);
                Assert.AreEqual(configuration, controller.ConfigurationJSON); Assert.IsTrue(controller.Authorized(bearer, null));
                await controller.CallAsync("end_session", new JObject());
                Assert.IsTrue(controller.IsEnabled); await Ready(() => controller.IsRunning);
                Assert.AreEqual(configuration, controller.ConfigurationJSON);
            }
            using (var restored = new SystemControlController(root)) {
                await restored.RestoreAsync(); Assert.IsTrue(restored.IsRunning, restored.LastError);
                Assert.AreEqual(configuration, restored.ConfigurationJSON); Assert.IsTrue(restored.Authorized(bearer, null));
                restored.Stop(); Assert.IsFalse(restored.IsEnabled); Assert.IsFalse(restored.Authorized(bearer, null));
                await restored.EnableAsync(); Assert.IsTrue(restored.IsRunning, restored.LastError); Assert.AreNotEqual(configuration, restored.ConfigurationJSON);
                restored.Stop();
            }
        }
        private static string Bearer(string config) => (string)JObject.Parse(config)["mcpServers"]!["ytray-system"]!["headers"]!["Authorization"]!;
        private static async Task Ready(Func<bool> condition) { var timer = Stopwatch.StartNew(); while (timer.Elapsed < TimeSpan.FromSeconds(20)) { if (condition()) return; await Task.Delay(100); } Assert.Fail("The enabled control service did not recover automatically"); }
    }
}
