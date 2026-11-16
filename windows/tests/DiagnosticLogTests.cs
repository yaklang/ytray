#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YTray.Core;
using YTray.Core.SystemControl;
using YTray.Native;
using YTray.Views;

namespace YTray.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class DiagnosticLogTests
    {
        private string _applicationDirectory = "";

        [TestInitialize]
        public void Setup()
        {
            DiagnosticLog.ResetForTests();
            _applicationDirectory = Path.Combine(Path.GetTempPath(), "ytray-log-test-" + Guid.NewGuid().ToString("N"));
        }

        [TestCleanup]
        public void Cleanup()
        {
            DiagnosticLog.ResetForTests();
            try { if (Directory.Exists(_applicationDirectory)) Directory.Delete(_applicationDirectory, true); }
            catch { }
        }

        [TestMethod]
        public void InitializeAlwaysCreatesSupportLogsAndWritesSessionMetadata()
        {
            DiagnosticLog.Initialize(_applicationDirectory);

            Assert.IsTrue(File.Exists(DiagnosticLog.MainLogPath));
            Assert.IsTrue(File.Exists(DiagnosticLog.ErrorLogPath));
            StringAssert.Contains(File.ReadAllText(DiagnosticLog.MainLogPath), "[app.start]");
            Assert.AreEqual(0L, new FileInfo(DiagnosticLog.ErrorLogPath).Length);
        }

        [TestMethod]
        public void ErrorsGoToBothLogsAndSensitiveValuesAreRedacted()
        {
            DiagnosticLog.Initialize(_applicationDirectory);
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var exception = new InvalidOperationException(
                $"path={profile}\\secret password=hunter2 token:abc123 url=https://alice:private@example.test/file\n" +
                "Authorization: Bearer private-bearer\njson={\"secret\":\"private-json\"}");

            DiagnosticLog.Error("test.error", exception);

            var main = File.ReadAllText(DiagnosticLog.MainLogPath);
            var errors = File.ReadAllText(DiagnosticLog.ErrorLogPath);
            StringAssert.Contains(main, "[test.error]");
            StringAssert.Contains(errors, "[test.error]");
            StringAssert.Contains(errors, "%USERPROFILE%");
            StringAssert.Contains(errors, "password=***");
            StringAssert.Contains(errors, "token:***");
            StringAssert.Contains(errors, "https://***:***@example.test/file");
            Assert.IsFalse(errors.Contains("hunter2"));
            Assert.IsFalse(errors.Contains("abc123"));
            Assert.IsFalse(errors.Contains("private"));
            Assert.IsFalse(errors.Contains("private-bearer"));
            Assert.IsFalse(errors.Contains("private-json"));
        }

        [TestMethod]
        public async Task McpCopyRetriesWithCurrentConfigurationWhenClipboardIsBusy()
        {
            var configuration = "old fixture";
            string? copiedText = null;
            var attempts = 0;
            var result = await McpClipboard.TryCopyAsync(() => configuration, text =>
            {
                if (++attempts == 1)
                {
                    configuration = "current fixture";
                    throw new WindowsClipboard.OpenException(WindowsClipboard.AccessDenied);
                }
                copiedText = text;
            });

            Assert.AreEqual((bool?)true, result);
            Assert.AreEqual(2, attempts);
            Assert.AreEqual("current fixture", copiedText);
        }

        [TestMethod]
        public async Task McpCopyFailureAndAuthorizationRejectionsAreLoggedWithoutCredentials()
        {
            DiagnosticLog.Initialize(_applicationDirectory);
            const string fixtureCredential = "fixture-credential";
            var attempts = 0;
            var result = await McpClipboard.TryCopyAsync(() => fixtureCredential, _ =>
            {
                attempts++;
                throw new Win32Exception(WindowsClipboard.AccessDenied, "SetClipboardData failed");
            });
            Assert.AreEqual((bool?)false, result);
            Assert.AreEqual(1, attempts);
            var errors = File.ReadAllText(DiagnosticLog.ErrorLogPath);
            StringAssert.Contains(errors, "[mcp.clipboard]");
            StringAssert.Contains(errors, "SetClipboardData failed");

            using var controller = new SystemControlController(_applicationDirectory);
            using var gateway = new ControlGateway(controller);
            var request = new ControlGateway.HttpRequest("POST", "/system/mcp",
                new Dictionary<string, string> { ["host"] = "127.0.0.1:51373" }, "");
            Assert.AreEqual(401, (await gateway.HandleRequestAsync(request)).Status);
            request.Headers["authorization"] = "Bearer " + fixtureCredential;
            Assert.AreEqual(401, (await gateway.HandleRequestAsync(request)).Status);

            var log = File.ReadAllText(DiagnosticLog.MainLogPath);
            StringAssert.Contains(log, "[control.mcp]");
            StringAssert.Contains(log, "HTTP 401 Authorization header missing");
            StringAssert.Contains(log, "HTTP 401 Authorization header rejected");
            Assert.IsFalse(log.Contains(fixtureCredential));
        }

        [TestMethod]
        public void RotationKeepsBoundedNumberedBackups()
        {
            var logDirectory = Path.Combine(_applicationDirectory, "Logs");
            Directory.CreateDirectory(logDirectory);
            var path = Path.Combine(logDirectory, "ytray.log");
            File.WriteAllText(path, new string('a', 32));
            File.WriteAllText(Path.Combine(logDirectory, "ytray.1.log"), "older");

            DiagnosticLog.RotateIfNeeded(path, 16, 2);

            Assert.IsFalse(File.Exists(path));
            Assert.AreEqual(new string('a', 32), File.ReadAllText(Path.Combine(logDirectory, "ytray.1.log")));
            Assert.AreEqual("older", File.ReadAllText(Path.Combine(logDirectory, "ytray.2.log")));
        }
    }
}
