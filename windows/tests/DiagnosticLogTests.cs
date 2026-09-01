#nullable enable
using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YTray.Core;

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
