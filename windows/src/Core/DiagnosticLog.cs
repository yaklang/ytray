#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace YTray.Core
{
    /// <summary>
    /// Small, dependency-free application logger. It deliberately owns no long-lived file handle,
    /// so support staff can read, copy, archive, or delete logs while YTray is running.
    /// </summary>
    internal static class DiagnosticLog
    {
        private const long MainLogLimit = 5L * 1024 * 1024;
        private const long ErrorLogLimit = 2L * 1024 * 1024;
        private const int BackupCount = 3;
        private const int MaximumEntryLength = 64 * 1024;
        private static readonly object SyncRoot = new object();
        private static readonly UTF8Encoding Utf8WithoutBom = new UTF8Encoding(false);
        private static readonly Regex UrlCredentials = new Regex(
            @"(?<scheme>[a-z][a-z0-9+.-]*://)[^\s/@:]+:[^\s/@]+@",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex AuthorizationValues = new Regex(
            @"(?<key>proxy-authorization|authorization)(?<separator>\s*[:=]\s*)(?<value>[^\r\n]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SecretValues = new Regex(
            @"(?<key>password|passwd|pwd|token|secret)(?<keyquote>[\""']?)(?<separator>\s*[:=]\s*)(?<valuequote>[\""']?)(?<value>[^\s,;\]\}\""']+)(?<endquote>[\""']?)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static string? _logDirectory;
        private static string? _userProfile;
        private static bool _initialized;

        internal static string LogDirectory => _logDirectory
            ?? Path.Combine(StatePersistence.DefaultApplicationDirectory, "Logs");
        internal static string MainLogPath => Path.Combine(LogDirectory, "ytray.log");
        internal static string ErrorLogPath => Path.Combine(LogDirectory, "ytray-errors.log");

        internal static void Initialize(string applicationDirectory)
        {
            if (string.IsNullOrWhiteSpace(applicationDirectory))
                throw new ArgumentException("Application directory is required.", nameof(applicationDirectory));

            lock (SyncRoot)
            {
                var directory = Path.Combine(applicationDirectory, "Logs");
                if (_initialized && string.Equals(_logDirectory, directory, StringComparison.OrdinalIgnoreCase)) return;

                _logDirectory = directory;
                _userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                try
                {
                    Directory.CreateDirectory(directory);
                    RotateIfNeeded(MainLogPath, MainLogLimit, BackupCount);
                    RotateIfNeeded(ErrorLogPath, ErrorLogLimit, BackupCount);
                    Touch(MainLogPath);
                    Touch(ErrorLogPath);
                    StatePersistence.RestrictToCurrentUser(MainLogPath);
                    StatePersistence.RestrictToCurrentUser(ErrorLogPath);
                    _initialized = true;
                    WriteCore(MainLogPath, "INFO", "app.start",
                        $"session started; version={ApplicationVersion()}; os={Environment.OSVersion.VersionString}; " +
                        $"processArchitecture={ProcessArchitecture()}; is64BitOS={Environment.Is64BitOperatingSystem}");
                }
                catch
                {
                    // Diagnostics must never make application startup fail.
                    _initialized = false;
                }
            }
        }

        internal static void Info(string category, string message) =>
            Write("INFO", category, message, false);

        internal static void Warning(string category, string message) =>
            Write("WARN", category, message, false);

        internal static void Error(string category, Exception exception, string? message = null)
        {
            if (exception == null) return;
            var detail = string.IsNullOrWhiteSpace(message)
                ? exception.ToString()
                : message + Environment.NewLine + exception;
            Write("ERROR", category, detail, true);
        }

        internal static bool TryOpen(out string openedPath, out string? error)
        {
            openedPath = MainLogPath;
            error = null;
            try
            {
                if (!_initialized) Initialize(StatePersistence.DefaultApplicationDirectory);
                Touch(MainLogPath);
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "notepad.exe",
                    Arguments = "\"" + MainLogPath.Replace("\"", "") + "\"",
                    UseShellExecute = true,
                });
                if (process == null) throw new InvalidOperationException("Notepad did not start.");
                Info("diagnostics.open", "opened main diagnostic log");
                return true;
            }
            catch (Exception primary)
            {
                try
                {
                    Directory.CreateDirectory(LogDirectory);
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = LogDirectory,
                        UseShellExecute = true,
                    });
                    openedPath = LogDirectory;
                    Warning("diagnostics.open", "opening the log file failed; opened the log directory instead");
                    return true;
                }
                catch (Exception fallback)
                {
                    error = "无法打开诊断日志：" + fallback.Message;
                    Error("diagnostics.open", fallback, primary.Message);
                    return false;
                }
            }
        }

        private static void Write(string level, string category, string message, bool alsoErrorLog)
        {
            if (!_initialized) return;
            lock (SyncRoot)
            {
                try
                {
                    RotateIfNeeded(MainLogPath, MainLogLimit, BackupCount);
                    WriteCore(MainLogPath, level, category, message);
                    if (alsoErrorLog)
                    {
                        RotateIfNeeded(ErrorLogPath, ErrorLogLimit, BackupCount);
                        WriteCore(ErrorLogPath, level, category, message);
                    }
                }
                catch
                {
                    // Logging failures are intentionally swallowed to avoid recursive crashes.
                }
            }
        }

        private static void WriteCore(string path, string level, string category, string message)
        {
            var safeCategory = Sanitize(category).Replace("\r", " ").Replace("\n", " ");
            var safeMessage = Sanitize(message);
            if (safeMessage.Length > MaximumEntryLength)
                safeMessage = safeMessage.Substring(0, MaximumEntryLength) + Environment.NewLine + "[entry truncated]";
            var entry = $"[{DateTimeOffset.Now:O}] [{level}] [{safeCategory}] [pid:{Process.GetCurrentProcess().Id}] {safeMessage}{Environment.NewLine}";
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write,
                       FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan))
            using (var writer = new StreamWriter(stream, Utf8WithoutBom))
                writer.Write(entry);
        }

        internal static string Sanitize(string? value)
        {
            var result = value ?? "";
            result = UrlCredentials.Replace(result, "${scheme}***:***@");
            result = AuthorizationValues.Replace(result, "${key}${separator}***");
            result = SecretValues.Replace(result,
                "${key}${keyquote}${separator}${valuequote}***${endquote}");
            var userProfile = _userProfile;
            if (!string.IsNullOrWhiteSpace(userProfile))
                result = ReplaceOrdinalIgnoreCase(result, userProfile!, "%USERPROFILE%");
            return result;
        }

        private static string ReplaceOrdinalIgnoreCase(string source, string oldValue, string newValue)
        {
            var offset = 0;
            var index = source.IndexOf(oldValue, offset, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return source;
            var builder = new StringBuilder(source.Length);
            while (index >= 0)
            {
                builder.Append(source, offset, index - offset).Append(newValue);
                offset = index + oldValue.Length;
                index = source.IndexOf(oldValue, offset, StringComparison.OrdinalIgnoreCase);
            }
            return builder.Append(source, offset, source.Length - offset).ToString();
        }

        private static void Touch(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (new FileStream(path, FileMode.Append, FileAccess.Write,
                       FileShare.ReadWrite | FileShare.Delete)) { }
        }

        internal static void RotateIfNeeded(string path, long maximumBytes, int backupCount)
        {
            if (maximumBytes <= 0 || backupCount <= 0 || !File.Exists(path)) return;
            var file = new FileInfo(path);
            if (file.Length < maximumBytes) return;
            for (var index = backupCount; index >= 1; index--)
            {
                var destination = BackupPath(path, index);
                if (index == backupCount && File.Exists(destination)) File.Delete(destination);
                var source = index == 1 ? path : BackupPath(path, index - 1);
                if (File.Exists(source)) File.Move(source, destination);
            }
        }

        private static string BackupPath(string path, int index) =>
            Path.Combine(Path.GetDirectoryName(path)!,
                Path.GetFileNameWithoutExtension(path) + "." + index + Path.GetExtension(path));

        private static string ApplicationVersion()
        {
            var informational = typeof(DiagnosticLog).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return string.IsNullOrWhiteSpace(informational)
                ? typeof(DiagnosticLog).Assembly.GetName().Version?.ToString() ?? "unknown"
                : informational!;
        }

        private static string ProcessArchitecture()
        {
            try { return RuntimeInformation.ProcessArchitecture.ToString(); }
            catch { return Environment.Is64BitProcess ? "x64" : "x86"; }
        }

        internal static void ResetForTests()
        {
            lock (SyncRoot)
            {
                _initialized = false;
                _logDirectory = null;
                _userProfile = null;
            }
        }
    }
}
