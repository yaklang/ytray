import AppKit
import Foundation

/// Persistent support diagnostics shared by the macOS application workflows. The logger keeps no
/// long-lived file handle, so logs remain readable and copyable while YTray is running.
enum DiagnosticLog {
    private static let lock = NSLock()
    private static let mainLogLimit: UInt64 = 5 * 1024 * 1024
    private static let errorLogLimit: UInt64 = 2 * 1024 * 1024
    private static let backupCount = 3
    private static let maximumEntryCharacters = 64 * 1024
    private static var applicationDirectory: URL?
    private static var initialized = false

    static var logDirectory: URL {
        let base = applicationDirectory ?? AppEnvironment.defaultApplicationDirectory
        return base.appendingPathComponent("Logs", isDirectory: true)
    }

    static var mainLogURL: URL { logDirectory.appendingPathComponent("ytray.log") }
    static var errorLogURL: URL { logDirectory.appendingPathComponent("ytray-errors.log") }

    static func initialize(applicationDirectory: URL) {
        lock.lock()
        defer { lock.unlock() }
        let normalized = applicationDirectory.standardizedFileURL
        if initialized, self.applicationDirectory?.standardizedFileURL == normalized { return }
        self.applicationDirectory = normalized
        do {
            try FileManager.default.createDirectory(
                at: logDirectory,
                withIntermediateDirectories: true
            )
            try rotateIfNeeded(mainLogURL, maximumBytes: mainLogLimit, backupCount: backupCount)
            try rotateIfNeeded(errorLogURL, maximumBytes: errorLogLimit, backupCount: backupCount)
            try touch(mainLogURL)
            try touch(errorLogURL)
            initialized = true
            try writeCore(
                to: mainLogURL,
                level: "INFO",
                category: "app.start",
                message: "session started; version=\(applicationVersion); os=\(ProcessInfo.processInfo.operatingSystemVersionString); architecture=\(architecture)"
            )
        } catch {
            // Diagnostics must never prevent the application from starting.
            initialized = false
        }
    }

    static func info(_ category: String, _ message: String) {
        write(level: "INFO", category: category, message: message, alsoToErrorLog: false)
    }

    static func warning(_ category: String, _ message: String) {
        write(level: "WARN", category: category, message: message, alsoToErrorLog: false)
    }

    static func error(_ category: String, _ error: Error, message: String? = nil) {
        let heading = message.map { $0 + "\n" } ?? ""
        let detail = "\(heading)\(String(reflecting: type(of: error))): \(error.localizedDescription)\n"
            + Thread.callStackSymbols.joined(separator: "\n")
        write(level: "ERROR", category: category, message: detail, alsoToErrorLog: true)
    }

    @MainActor
    static func openMainLog(applicationDirectory: URL) throws {
        initialize(applicationDirectory: applicationDirectory)
        try touch(mainLogURL)
        if NSWorkspace.shared.open(mainLogURL) {
            info("diagnostics.open", "opened main diagnostic log")
            return
        }
        if NSWorkspace.shared.open(logDirectory) {
            warning("diagnostics.open", "opening the log file failed; opened the log directory instead")
            return
        }
        throw DiagnosticLogError.openFailed
    }

    private static func write(
        level: String,
        category: String,
        message: String,
        alsoToErrorLog: Bool
    ) {
        lock.lock()
        defer { lock.unlock() }
        guard initialized else { return }
        do {
            try rotateIfNeeded(mainLogURL, maximumBytes: mainLogLimit, backupCount: backupCount)
            try writeCore(to: mainLogURL, level: level, category: category, message: message)
            if alsoToErrorLog {
                try rotateIfNeeded(errorLogURL, maximumBytes: errorLogLimit, backupCount: backupCount)
                try writeCore(to: errorLogURL, level: level, category: category, message: message)
            }
        } catch {
            // Logging failures are intentionally swallowed to avoid recursive failures.
        }
    }

    private static func writeCore(to url: URL, level: String, category: String, message: String) throws {
        let safeCategory = sanitize(category)
            .replacingOccurrences(of: "\r", with: " ")
            .replacingOccurrences(of: "\n", with: " ")
        var safeMessage = sanitize(message)
        if safeMessage.count > maximumEntryCharacters {
            safeMessage = String(safeMessage.prefix(maximumEntryCharacters)) + "\n[entry truncated]"
        }
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let entry = "[\(formatter.string(from: Date()))] [\(level)] [\(safeCategory)] [pid:\(ProcessInfo.processInfo.processIdentifier)] \(safeMessage)\n"
        let handle = try FileHandle(forWritingTo: url)
        defer { try? handle.close() }
        try handle.seekToEnd()
        try handle.write(contentsOf: Data(entry.utf8))
    }

    static func sanitize(_ value: String) -> String {
        var result = value
        result = replacing(
            in: result,
            pattern: #"(?i)([a-z][a-z0-9+.-]*://)[^\s/@:]+:[^\s/@]+@"#,
            with: "$1***:***@"
        )
        result = replacing(
            in: result,
            pattern: #"(?im)(proxy-authorization|authorization)(\s*[:=]\s*)[^\r\n]+"#,
            with: "$1$2***"
        )
        result = replacing(
            in: result,
            pattern: #"(?i)(password|passwd|pwd|token|secret)(["']?)(\s*[:=]\s*)(["']?)[^\s,;\]\}"']+(["']?)"#,
            with: "$1$2$3$4***$5"
        )
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        if !home.isEmpty {
            result = replacing(
                in: result,
                pattern: NSRegularExpression.escapedPattern(for: home),
                with: "%USERPROFILE%",
                options: [.caseInsensitive]
            )
        }
        return result
    }

    private static func replacing(
        in value: String,
        pattern: String,
        with replacement: String,
        options: NSRegularExpression.Options = []
    ) -> String {
        guard let expression = try? NSRegularExpression(pattern: pattern, options: options) else { return value }
        return expression.stringByReplacingMatches(
            in: value,
            range: NSRange(value.startIndex..., in: value),
            withTemplate: replacement
        )
    }

    private static func touch(_ url: URL) throws {
        try FileManager.default.createDirectory(
            at: url.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        if !FileManager.default.fileExists(atPath: url.path) {
            guard FileManager.default.createFile(atPath: url.path, contents: nil) else {
                throw CocoaError(.fileWriteUnknown)
            }
        }
        try? FileManager.default.setAttributes(
            [.posixPermissions: 0o600],
            ofItemAtPath: url.path
        )
    }

    static func rotateIfNeeded(_ url: URL, maximumBytes: UInt64, backupCount: Int) throws {
        guard maximumBytes > 0, backupCount > 0,
              let size = try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize,
              UInt64(size) >= maximumBytes else { return }
        let fileManager = FileManager.default
        for index in stride(from: backupCount, through: 1, by: -1) {
            let destination = backupURL(for: url, index: index)
            if index == backupCount { try? fileManager.removeItem(at: destination) }
            let source = index == 1 ? url : backupURL(for: url, index: index - 1)
            if fileManager.fileExists(atPath: source.path) {
                try fileManager.moveItem(at: source, to: destination)
            }
        }
    }

    private static func backupURL(for url: URL, index: Int) -> URL {
        let stem = url.deletingPathExtension().lastPathComponent
        let ext = url.pathExtension
        return url.deletingLastPathComponent()
            .appendingPathComponent("\(stem).\(index).\(ext)")
    }

    private static var applicationVersion: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "development"
    }

    private static var architecture: String {
        #if arch(arm64)
        return "arm64"
        #elseif arch(x86_64)
        return "x86_64"
        #else
        return "unknown"
        #endif
    }

    static func resetForTesting() {
        lock.lock()
        defer { lock.unlock() }
        initialized = false
        applicationDirectory = nil
    }
}

private enum DiagnosticLogError: LocalizedError {
    case openFailed

    var errorDescription: String? {
        "无法打开诊断日志"
    }
}
