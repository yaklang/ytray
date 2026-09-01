import CryptoKit
import Foundation

enum RuntimeInstallPhase: Equatable {
    case connecting
    case downloading
    case verifying
    case extracting
    case completed
}

struct RuntimeInstallProgress: Equatable {
    let phase: RuntimeInstallPhase
    let percent: Int
    let bytesReceived: Int64
    let totalBytes: Int64?
    let message: String

    var byteSummary: String? {
        guard bytesReceived > 0 else { return nil }
        if let totalBytes, totalBytes > 0 {
            return "\(RuntimeInstaller.formatBytes(bytesReceived)) / \(RuntimeInstaller.formatBytes(totalBytes))"
        }
        return RuntimeInstaller.formatBytes(bytesReceived)
    }
}

enum RuntimeInstaller {
    static let manifestURL = URL(string: "https://aliyun-oss.yaklang.com/browsers/chrome/manifest.json")!

    static var architecture: String {
        #if arch(arm64)
        return "arm64"
        #else
        return "x64"
        #endif
    }

    static var platform: String { "macos-\(architecture)" }

    static func fetchVersions() async throws -> [MirrorVersion] {
        DiagnosticLog.info("runtime.manifest", "requesting \(manifestURL.host ?? "runtime mirror")\(manifestURL.path)")
        do {
            var request = URLRequest(url: manifestURL)
            request.cachePolicy = .reloadIgnoringLocalAndRemoteCacheData
            request.timeoutInterval = 15
            request.setValue("no-cache, no-store", forHTTPHeaderField: "Cache-Control")
            let (data, response) = try await URLSession.shared.data(for: request)
            guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else {
                throw YTrayError.downloadFailed("镜像清单返回异常")
            }
            let versions = try JSONDecoder().decode(MirrorManifest.self, from: data).versions
            guard !versions.isEmpty else { throw YTrayError.downloadFailed("镜像清单中没有可用版本") }
            DiagnosticLog.info("runtime.manifest", "loaded \(versions.count) versions; latest=\(versions.first?.version ?? "none")")
            return versions
        } catch {
            DiagnosticLog.error("runtime.manifest", error)
            throw error
        }
    }

    static func install(
        version: MirrorVersion,
        into applicationDirectory: URL,
        progress: @escaping (RuntimeInstallProgress) -> Void = { _ in }
    ) async throws -> BrowserRuntime {
        guard let artifact = version.artifacts.first(where: {
            $0.os == "macos" && $0.arch == architecture && $0.format == "zip"
        }), let url = URL(string: artifact.url) else {
            throw YTrayError.downloadFailed("版本 \(version.version) 没有 \(platform) ZIP")
        }
        DiagnosticLog.info("runtime.install", "starting Chrome for Testing \(version.version); platform=\(platform)")
        progress(RuntimeInstallProgress(
            phase: .connecting,
            percent: 0,
            bytesReceived: 0,
            totalBytes: artifact.size,
            message: "正在连接下载镜像…"
        ))

        let temporary = FileManager.default.temporaryDirectory
            .appendingPathComponent("ytray-runtime-\(UUID().uuidString).zip")
        defer { try? FileManager.default.removeItem(at: temporary) }
        do {
            var request = URLRequest(url: url)
            request.cachePolicy = .reloadIgnoringLocalCacheData
            request.timeoutInterval = 20 * 60
            request.setValue("identity", forHTTPHeaderField: "Accept-Encoding")
            request.setValue("YTray/\(applicationVersion)", forHTTPHeaderField: "User-Agent")
            let delegate = RuntimeDownloadDelegate(
                destination: temporary,
                expectedSize: artifact.size
            ) { received, total in
                progress(RuntimeInstallProgress(
                    phase: .downloading,
                    percent: downloadPercent(bytesReceived: received, totalBytes: total),
                    bytesReceived: received,
                    totalBytes: total,
                    message: "正在下载 Chrome for Testing \(version.version)…"
                ))
            }
            _ = try await delegate.start(request: request)

            progress(RuntimeInstallProgress(
                phase: .verifying,
                percent: 100,
                bytesReceived: artifact.size ?? 0,
                totalBytes: artifact.size,
                message: "下载完成，正在校验 SHA-256…"
            ))
            let actualHash = try await Task.detached(priority: .userInitiated) {
                try sha256(of: temporary)
            }.value
            guard actualHash.caseInsensitiveCompare(artifact.sha256) == .orderedSame else {
                throw YTrayError.downloadFailed("SHA-256 校验失败")
            }

            let destination = applicationDirectory
                .appendingPathComponent("Runtimes/\(version.version)/\(platform)", isDirectory: true)
            try? FileManager.default.removeItem(at: destination)
            try FileManager.default.createDirectory(at: destination, withIntermediateDirectories: true)
            progress(RuntimeInstallProgress(
                phase: .extracting,
                percent: 100,
                bytesReceived: artifact.size ?? 0,
                totalBytes: artifact.size,
                message: "校验通过，正在解压浏览器…"
            ))
            try await extractArchive(temporary, to: destination)
            guard let executable = locateChrome(in: destination) else {
                throw YTrayError.downloadFailed("ZIP 内未找到 Google Chrome for Testing 可执行文件")
            }
            progress(RuntimeInstallProgress(
                phase: .completed,
                percent: 100,
                bytesReceived: artifact.size ?? 0,
                totalBytes: artifact.size,
                message: "Chrome for Testing \(version.version) 安装完成"
            ))
            DiagnosticLog.info("runtime.install", "installed Chrome for Testing \(version.version)")
            return BrowserRuntime(
                name: "Chrome for Testing \(version.version)",
                version: version.version,
                architecture: platform,
                executablePath: executable.path,
                source: .managed,
                browserKind: .chromeForTesting
            )
        } catch {
            DiagnosticLog.error("runtime.install", error, message: "Chrome for Testing \(version.version) installation failed")
            throw error
        }
    }

    private static func extractArchive(_ archive: URL, to destination: URL) async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            let unzip = Process()
            unzip.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
            unzip.arguments = ["-x", "-k", archive.path, destination.path]
            let errorPipe = Pipe()
            unzip.standardError = errorPipe
            unzip.terminationHandler = { process in
                let data = errorPipe.fileHandleForReading.readDataToEndOfFile()
                guard process.terminationStatus == 0 else {
                    continuation.resume(throwing: YTrayError.downloadFailed(
                        String(data: data, encoding: .utf8) ?? "ZIP 解压失败"
                    ))
                    return
                }
                continuation.resume()
            }
            do {
                try unzip.run()
            } catch {
                unzip.terminationHandler = nil
                continuation.resume(throwing: error)
            }
        }
    }

    static func formatBytes(_ bytes: Int64) -> String {
        guard bytes >= 1_024 else { return "\(bytes) B" }
        let value = Double(bytes)
        if bytes < 1_024 * 1_024 { return String(format: "%.1f KB", value / 1_024) }
        if bytes < 1_024 * 1_024 * 1_024 { return String(format: "%.1f MB", value / 1_024 / 1_024) }
        return String(format: "%.2f GB", value / 1_024 / 1_024 / 1_024)
    }

    static func downloadPercent(bytesReceived: Int64, totalBytes: Int64?) -> Int {
        guard let totalBytes, totalBytes > 0 else { return 0 }
        return max(0, min(100, Int(bytesReceived * 100 / totalBytes)))
    }

    static func normalizeExecutable(_ selected: URL) -> URL? {
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: selected.path, isDirectory: &isDirectory) else { return nil }
        if selected.pathExtension.lowercased() == "app" {
            let macOS = selected.appendingPathComponent("Contents/MacOS", isDirectory: true)
            if let items = try? FileManager.default.contentsOfDirectory(at: macOS, includingPropertiesForKeys: nil),
               let executable = items.first(where: { FileManager.default.isExecutableFile(atPath: $0.path) }) {
                return executable
            }
        }
        if isDirectory.boolValue { return locateChrome(in: selected) }
        return FileManager.default.isExecutableFile(atPath: selected.path) ? selected : nil
    }

    static func locateChrome(in root: URL) -> URL? {
        guard let enumerator = FileManager.default.enumerator(
            at: root,
            includingPropertiesForKeys: [.isRegularFileKey],
            options: [.skipsHiddenFiles]
        ) else { return nil }
        for case let candidate as URL in enumerator {
            if candidate.path.contains(".app/Contents/MacOS/")
                && candidate.lastPathComponent.localizedCaseInsensitiveContains("Chrome")
                && FileManager.default.isExecutableFile(atPath: candidate.path) {
                return candidate
            }
        }
        return nil
    }

    private static func sha256(of url: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var hasher = SHA256()
        while true {
            guard let chunk = try handle.read(upToCount: 1024 * 1024), !chunk.isEmpty else { break }
            hasher.update(data: chunk)
        }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }

    private static var applicationVersion: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "development"
    }
}

private final class RuntimeDownloadDelegate: NSObject, URLSessionDownloadDelegate {
    private let destination: URL
    private let expectedSize: Int64?
    private let progress: (Int64, Int64?) -> Void
    private var continuation: CheckedContinuation<URL, Error>?
    private var downloadedURL: URL?
    private var failure: Error?
    private var session: URLSession?
    private var lastReportedPercent = -1
    private var lastReportedAt = Date.distantPast

    init(destination: URL, expectedSize: Int64?, progress: @escaping (Int64, Int64?) -> Void) {
        self.destination = destination
        self.expectedSize = expectedSize
        self.progress = progress
    }

    func start(request: URLRequest) async throws -> URL {
        try await withCheckedThrowingContinuation { continuation in
            self.continuation = continuation
            let configuration = URLSessionConfiguration.ephemeral
            configuration.timeoutIntervalForRequest = 60
            configuration.timeoutIntervalForResource = 20 * 60
            let session = URLSession(configuration: configuration, delegate: self, delegateQueue: nil)
            self.session = session
            session.downloadTask(with: request).resume()
        }
    }

    func urlSession(
        _ session: URLSession,
        downloadTask: URLSessionDownloadTask,
        didWriteData bytesWritten: Int64,
        totalBytesWritten: Int64,
        totalBytesExpectedToWrite: Int64
    ) {
        let total = expectedSize.flatMap { $0 > 0 ? $0 : nil }
            ?? (totalBytesExpectedToWrite > 0 ? totalBytesExpectedToWrite : nil)
        let percent = total.flatMap { $0 > 0 ? Int(totalBytesWritten * 100 / $0) : nil } ?? 0
        let now = Date()
        let finished = total.map { totalBytesWritten >= $0 } ?? false
        guard finished || percent != lastReportedPercent && now.timeIntervalSince(lastReportedAt) >= 0.09 else { return }
        lastReportedPercent = percent
        lastReportedAt = now
        progress(totalBytesWritten, total)
    }

    func urlSession(
        _ session: URLSession,
        downloadTask: URLSessionDownloadTask,
        didFinishDownloadingTo location: URL
    ) {
        do {
            guard let response = downloadTask.response as? HTTPURLResponse,
                  (200..<300).contains(response.statusCode) else {
                throw YTrayError.downloadFailed("ZIP 下载返回异常")
            }
            try? FileManager.default.removeItem(at: destination)
            try FileManager.default.moveItem(at: location, to: destination)
            downloadedURL = destination
        } catch {
            failure = error
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        defer {
            continuation = nil
            self.session?.finishTasksAndInvalidate()
            self.session = nil
        }
        if let error { continuation?.resume(throwing: error) }
        else if let failure { continuation?.resume(throwing: failure) }
        else if let downloadedURL { continuation?.resume(returning: downloadedURL) }
        else { continuation?.resume(throwing: YTrayError.downloadFailed("ZIP 下载没有产生文件")) }
    }
}
