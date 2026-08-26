import AppKit
import Combine
import CryptoKit
import Darwin
import Foundation

enum AppUpdatePhase: Equatable {
    case idle
    case checking
    case upToDate
    case available
    case downloading
    case downloaded
    case installing
    case failed
}

struct AppReleaseManifest: Decodable {
    let schemaVersion: Int
    let product: String
    let version: String
    let assets: [AppReleaseAsset]

    enum CodingKeys: String, CodingKey {
        case schemaVersion = "schema_version"
        case product, version, assets
    }
}

struct AppReleaseAsset: Decodable {
    let platform: String
    let architecture: String
    let kind: String
    let filename: String
    let url: URL
    let sha256: String
    let size: Int64
}

enum AppUpdateError: LocalizedError {
    case message(String)

    var errorDescription: String? {
        switch self {
        case .message(let value): return value
        }
    }
}

@MainActor
final class AppUpdateManager: ObservableObject {
    static let shared = AppUpdateManager()
    static let manifestURL = URL(string: "https://aliyun-oss.yaklang.com/ytray/latest.json")!

    @Published private(set) var phase: AppUpdatePhase = .idle
    @Published private(set) var statusText: String
    @Published private(set) var downloadPercent = 0
    @Published private(set) var availableVersion: String?

    let currentVersion: String
    private var release: AppReleaseManifest?
    private var asset: AppReleaseAsset?
    private var downloadedDMG: URL?

    init(currentVersion: String? = nil) {
        let bundleVersion = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String
        self.currentVersion = currentVersion ?? bundleVersion ?? "0.0.0"
        self.statusText = "当前版本 v\(self.currentVersion)"
    }

    var isBusy: Bool {
        phase == .checking || phase == .downloading || phase == .installing
    }

    var isUpdateAvailable: Bool {
        guard let availableVersion else { return false }
        return Self.compareVersions(availableVersion, currentVersion) == .orderedDescending
    }

    var isDownloaded: Bool {
        guard phase == .downloaded, let downloadedDMG else { return false }
        return FileManager.default.fileExists(atPath: downloadedDMG.path)
    }

    var actionLabel: String {
        switch phase {
        case .checking: return "正在检查…"
        case .downloading: return "下载中 \(downloadPercent)%"
        case .installing: return "正在安装…"
        case .downloaded: return "立即安装并重启"
        default: return isUpdateAvailable ? "下载并安装" : "检查更新"
        }
    }

    func checkForUpdates() async {
        guard !isBusy else { return }
        setPhase(.checking, "正在检查 YTray 更新…")
        do {
            var request = URLRequest(url: Self.manifestURL)
            request.cachePolicy = .reloadIgnoringLocalAndRemoteCacheData
            request.timeoutInterval = 25
            request.setValue("no-cache, no-store", forHTTPHeaderField: "Cache-Control")
            request.setValue("YTray/\(currentVersion)", forHTTPHeaderField: "User-Agent")
            let (data, response) = try await URLSession.shared.data(for: request)
            guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else {
                throw AppUpdateError.message("更新服务器返回了异常状态")
            }
            let manifest = try JSONDecoder().decode(AppReleaseManifest.self, from: data)
            try Self.validate(manifest)
            let selected = Self.selectAsset(
                from: manifest,
                platform: "darwin",
                architecture: Self.architecture,
                kind: "dmg"
            )
            guard let selected else {
                throw AppUpdateError.message("最新版本没有适用于本机架构的 macOS 安装包")
            }
            try Self.validate(selected)
            release = manifest
            asset = selected
            availableVersion = manifest.version
            downloadedDMG = Self.existingVerifiedDownload(manifest: manifest, asset: selected)

            if Self.compareVersions(manifest.version, currentVersion) == .orderedDescending {
                if downloadedDMG != nil {
                    setPhase(.downloaded, "YTray v\(manifest.version) 已下载并校验，可以安装")
                } else {
                    setPhase(.available, "发现新版本 v\(manifest.version) · 当前 v\(currentVersion)")
                }
            } else {
                downloadedDMG = nil
                setPhase(.upToDate, "YTray v\(currentVersion) 已是最新版本")
            }
        } catch {
            setPhase(.failed, "检查更新失败 · \(Self.userFacing(error))")
        }
    }

    func downloadUpdate() async -> Bool {
        guard !isBusy, isUpdateAvailable, let release, let asset else { return false }
        do {
            let destination = try Self.downloadPath(manifest: release, asset: asset)
            let partial = destination.appendingPathExtension("part")
            try FileManager.default.createDirectory(
                at: destination.deletingLastPathComponent(),
                withIntermediateDirectories: true
            )
            try? FileManager.default.removeItem(at: partial)
            downloadPercent = 0
            setPhase(.downloading, "正在下载 YTray v\(release.version)…")

            var request = URLRequest(url: asset.url)
            request.cachePolicy = .reloadIgnoringLocalCacheData
            request.timeoutInterval = 20 * 60
            request.setValue("identity", forHTTPHeaderField: "Accept-Encoding")
            request.setValue("YTray/\(currentVersion)", forHTTPHeaderField: "User-Agent")
            let delegate = AppUpdateDownloadDelegate(
                destination: partial,
                expectedSize: asset.size
            ) { [weak self] percent in
                Task { @MainActor in
                    guard let self, self.phase == .downloading else { return }
                    self.downloadPercent = percent
                }
            }
            _ = try await delegate.start(request: request)
            try await Task.detached(priority: .userInitiated) {
                try Self.verifyFile(partial, asset: asset)
            }.value
            try? FileManager.default.removeItem(at: destination)
            try FileManager.default.moveItem(at: partial, to: destination)
            downloadedDMG = destination
            downloadPercent = 100
            setPhase(.downloaded, "YTray v\(release.version) 下载完成，校验已通过")
            return true
        } catch {
            setPhase(.failed, "下载更新失败 · \(Self.userFacing(error))")
            return false
        }
    }

    func installDownloadedUpdate() async -> Bool {
        guard !isBusy, let release, let asset, let downloadedDMG else { return false }
        do {
            try Self.verifyFile(downloadedDMG, asset: asset)
            let currentApplication = Bundle.main.bundleURL
            guard currentApplication.pathExtension.lowercased() == "app",
                  !currentApplication.path.hasPrefix("/Volumes/"),
                  !currentApplication.path.contains("/AppTranslocation/") else {
                throw AppUpdateError.message("请先将 YTray.app 放入“应用程序”文件夹，再使用应用内更新")
            }
            setPhase(.installing, "正在准备更新助手，YTray 随后会自动重新启动…")
            let processID = ProcessInfo.processInfo.processIdentifier
            try await Task.detached(priority: .userInitiated) {
                try MacUpdateInstaller.prepareAndLaunch(
                    dmg: downloadedDMG,
                    expectedVersion: release.version,
                    currentApplication: currentApplication,
                    parentProcessID: processID,
                    consoleUserID: getuid()
                )
            }.value
            NSApplication.shared.terminate(nil)
            return true
        } catch {
            setPhase(.failed, "无法安装更新 · \(Self.userFacing(error))")
            return false
        }
    }

    nonisolated static func selectAsset(
        from manifest: AppReleaseManifest,
        platform: String,
        architecture: String,
        kind: String
    ) -> AppReleaseAsset? {
        manifest.assets.first {
            $0.platform.caseInsensitiveCompare(platform) == .orderedSame
                && $0.architecture.caseInsensitiveCompare(architecture) == .orderedSame
                && $0.kind.caseInsensitiveCompare(kind) == .orderedSame
        }
    }

    nonisolated static func compareVersions(_ left: String, _ right: String) -> ComparisonResult {
        let lhs = ParsedAppVersion(left)
        let rhs = ParsedAppVersion(right)
        for index in 0..<max(lhs.core.count, rhs.core.count) {
            let leftValue = index < lhs.core.count ? lhs.core[index] : 0
            let rightValue = index < rhs.core.count ? rhs.core[index] : 0
            if leftValue != rightValue { return leftValue > rightValue ? .orderedDescending : .orderedAscending }
        }
        if lhs.prerelease.isEmpty || rhs.prerelease.isEmpty {
            if lhs.prerelease.isEmpty == rhs.prerelease.isEmpty { return .orderedSame }
            return lhs.prerelease.isEmpty ? .orderedDescending : .orderedAscending
        }
        for index in 0..<max(lhs.prerelease.count, rhs.prerelease.count) {
            if index >= lhs.prerelease.count { return .orderedAscending }
            if index >= rhs.prerelease.count { return .orderedDescending }
            let comparison = compareIdentifier(lhs.prerelease[index], rhs.prerelease[index])
            if comparison != .orderedSame { return comparison }
        }
        return .orderedSame
    }

    nonisolated private static var architecture: String {
        #if arch(arm64)
        return "arm64"
        #else
        return "amd64"
        #endif
    }

    nonisolated private static func compareIdentifier(_ left: String, _ right: String) -> ComparisonResult {
        if let lhs = Int(left), let rhs = Int(right) {
            if lhs == rhs { return .orderedSame }
            return lhs > rhs ? .orderedDescending : .orderedAscending
        }
        if (Int(left) != nil) != (Int(right) != nil) {
            return Int(left) != nil ? .orderedAscending : .orderedDescending
        }
        if let lhs = numericSuffix(left), let rhs = numericSuffix(right),
           lhs.prefix.caseInsensitiveCompare(rhs.prefix) == .orderedSame {
            if lhs.number == rhs.number { return .orderedSame }
            return lhs.number > rhs.number ? .orderedDescending : .orderedAscending
        }
        return left.caseInsensitiveCompare(right)
    }

    nonisolated private static func numericSuffix(_ value: String) -> (prefix: String, number: Int)? {
        let suffix = value.reversed().prefix { $0.isNumber }.reversed()
        guard !suffix.isEmpty, let number = Int(String(suffix)) else { return nil }
        return (String(value.dropLast(suffix.count)), number)
    }

    nonisolated private static func validate(_ manifest: AppReleaseManifest) throws {
        guard manifest.schemaVersion == 1, manifest.product == "ytray",
              manifest.version.range(
                of: #"^[0-9]+\.[0-9]+\.[0-9]+(?:[-.][0-9A-Za-z][0-9A-Za-z.-]*)?$"#,
                options: .regularExpression
              ) != nil,
              !manifest.assets.isEmpty else {
            throw AppUpdateError.message("更新清单格式无效")
        }
    }

    nonisolated private static func validate(_ asset: AppReleaseAsset) throws {
        guard asset.url.scheme?.lowercased() == "https", asset.size > 0,
              asset.sha256.count == 64,
              asset.sha256.allSatisfy({ $0.isHexDigit }),
              asset.filename == URL(fileURLWithPath: asset.filename).lastPathComponent else {
            throw AppUpdateError.message("更新包校验信息无效")
        }
    }

    nonisolated private static func downloadPath(manifest: AppReleaseManifest, asset: AppReleaseAsset) throws -> URL {
        try validate(asset)
        let root = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("YTray/Updates", isDirectory: true)
        let safeVersion = manifest.version
            .replacingOccurrences(of: "/", with: "-")
            .replacingOccurrences(of: "\\", with: "-")
        return root
            .appendingPathComponent(safeVersion, isDirectory: true)
            .appendingPathComponent(asset.filename)
    }

    nonisolated private static func existingVerifiedDownload(
        manifest: AppReleaseManifest,
        asset: AppReleaseAsset
    ) -> URL? {
        guard let path = try? downloadPath(manifest: manifest, asset: asset),
              FileManager.default.fileExists(atPath: path.path),
              (try? verifyFile(path, asset: asset)) != nil else { return nil }
        return path
    }

    nonisolated private static func verifyFile(_ file: URL, asset: AppReleaseAsset) throws {
        let attributes = try FileManager.default.attributesOfItem(atPath: file.path)
        guard let size = attributes[.size] as? NSNumber, size.int64Value == asset.size else {
            throw AppUpdateError.message("更新包大小与发布清单不一致")
        }
        let handle = try FileHandle(forReadingFrom: file)
        defer { try? handle.close() }
        var digest = SHA256()
        while true {
            let data = handle.readData(ofLength: 1024 * 1024)
            if data.isEmpty { break }
            digest.update(data: data)
        }
        let actual = digest.finalize().map { String(format: "%02x", $0) }.joined()
        guard actual.caseInsensitiveCompare(asset.sha256) == .orderedSame else {
            throw AppUpdateError.message("更新包 SHA-256 校验失败")
        }
    }

    private func setPhase(_ phase: AppUpdatePhase, _ status: String) {
        self.phase = phase
        statusText = status
    }

    nonisolated private static func userFacing(_ error: Error) -> String {
        if let updateError = error as? AppUpdateError { return updateError.localizedDescription }
        if let urlError = error as? URLError, urlError.code == .cancelled { return "请求已取消" }
        if let urlError = error as? URLError, urlError.code == .timedOut { return "请求超时" }
        return error.localizedDescription
    }
}

private struct ParsedAppVersion {
    let core: [Int]
    let prerelease: [String]

    init(_ rawValue: String) {
        var value = rawValue.trimmingCharacters(in: .whitespacesAndNewlines)
        if value.lowercased().hasPrefix("v") { value.removeFirst() }
        value = String(value.split(separator: "+", maxSplits: 1).first ?? "")
        let parts = value.split(separator: "-", maxSplits: 1, omittingEmptySubsequences: false)
        core = parts.first?.split(separator: ".").map { Int($0) ?? -1 } ?? [-1]
        prerelease = parts.count > 1 ? parts[1].split(separator: ".").map(String.init) : []
    }
}

private final class AppUpdateDownloadDelegate: NSObject, URLSessionDownloadDelegate {
    private let destination: URL
    private let expectedSize: Int64
    private let progress: (Int) -> Void
    private var continuation: CheckedContinuation<URL, Error>?
    private var downloadedURL: URL?
    private var failure: Error?
    private var session: URLSession?

    init(destination: URL, expectedSize: Int64, progress: @escaping (Int) -> Void) {
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
        let total = expectedSize > 0 ? expectedSize : totalBytesExpectedToWrite
        guard total > 0 else { return }
        progress(max(0, min(100, Int(totalBytesWritten * 100 / total))))
    }

    func urlSession(
        _ session: URLSession,
        downloadTask: URLSessionDownloadTask,
        didFinishDownloadingTo location: URL
    ) {
        do {
            guard let response = downloadTask.response as? HTTPURLResponse,
                  (200..<300).contains(response.statusCode) else {
                throw AppUpdateError.message("更新服务器返回了异常状态")
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
        else { continuation?.resume(throwing: AppUpdateError.message("更新包下载没有产生文件")) }
    }
}

private enum MacUpdateInstaller {
    static func prepareAndLaunch(
        dmg: URL,
        expectedVersion: String,
        currentApplication: URL,
        parentProcessID: Int32,
        consoleUserID: uid_t
    ) throws {
        let fileManager = FileManager.default
        let mountPoint = fileManager.temporaryDirectory
            .appendingPathComponent("ytray-update-\(UUID().uuidString)", isDirectory: true)
        try fileManager.createDirectory(at: mountPoint, withIntermediateDirectories: true)
        var helperOwnsMount = false
        defer {
            if !helperOwnsMount {
                _ = try? run("/usr/bin/hdiutil", ["detach", mountPoint.path, "-force"])
                try? fileManager.removeItem(at: mountPoint)
            }
        }

        _ = try run("/usr/bin/hdiutil", [
            "attach", dmg.path, "-mountpoint", mountPoint.path, "-nobrowse", "-readonly"
        ])
        let sourceApplication = mountPoint.appendingPathComponent("YTray.app", isDirectory: true)
        guard fileManager.fileExists(atPath: sourceApplication.path),
              let bundle = Bundle(url: sourceApplication),
              bundle.bundleIdentifier == "io.yaklang.ytray",
              bundle.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String == expectedVersion else {
            throw AppUpdateError.message("DMG 中的 YTray.app 身份或版本不正确")
        }

        _ = try run("/usr/bin/codesign", ["--verify", "--deep", "--strict", sourceApplication.path])
        let currentTeam = try signingTeam(currentApplication)
        let updateTeam = try signingTeam(sourceApplication)
        guard !currentTeam.isEmpty, currentTeam != "not set", currentTeam == updateTeam else {
            throw AppUpdateError.message("新版本的开发者签名与当前 YTray 不一致")
        }

        let updateDirectory = dmg.deletingLastPathComponent()
        let script = updateDirectory.appendingPathComponent("apply-ytray-update.sh")
        let log = updateDirectory.appendingPathComponent("install.log")
        try installerScript.write(to: script, atomically: true, encoding: .utf8)
        try fileManager.setAttributes([.posixPermissions: 0o700], ofItemAtPath: script.path)

        let arguments = [
            String(parentProcessID),
            sourceApplication.path,
            currentApplication.path,
            mountPoint.path,
            String(consoleUserID),
        ]
        let parentDirectory = currentApplication.deletingLastPathComponent()
        if fileManager.isWritableFile(atPath: parentDirectory.path) {
            let process = Process()
            process.executableURL = URL(fileURLWithPath: "/bin/sh")
            process.arguments = [script.path] + arguments
            _ = fileManager.createFile(atPath: log.path, contents: nil)
            let output = try FileHandle(forWritingTo: log)
            process.standardOutput = output
            process.standardError = output
            try process.run()
        } else {
            let command = (["/bin/sh", script.path] + arguments)
                .map(shellQuote).joined(separator: " ")
                + " > " + shellQuote(log.path) + " 2>&1 &"
            let appleScript = "do shell script \(appleScriptQuote(command)) with administrator privileges"
            _ = try run("/usr/bin/osascript", ["-e", appleScript])
        }
        helperOwnsMount = true
    }

    private static let installerScript = #"""
#!/bin/sh
set -eu

PARENT_PID="$1"
SOURCE_APP="$2"
TARGET_APP="$3"
MOUNT_POINT="$4"
CONSOLE_UID="$5"
BACKUP_APP="${TARGET_APP}.ytray-update-backup"

open_target() {
  if [ "$(/usr/bin/id -u)" -eq 0 ]; then
    /bin/launchctl asuser "$CONSOLE_UID" /usr/bin/open "$TARGET_APP"
  else
    /usr/bin/open "$TARGET_APP"
  fi
}

while kill -0 "$PARENT_PID" 2>/dev/null; do
  sleep 0.2
done

rm -rf "$BACKUP_APP"
if [ -e "$TARGET_APP" ]; then
  mv "$TARGET_APP" "$BACKUP_APP"
fi

if /usr/bin/ditto "$SOURCE_APP" "$TARGET_APP" && \
   /usr/bin/codesign --verify --deep --strict "$TARGET_APP"; then
  rm -rf "$BACKUP_APP"
  /usr/bin/hdiutil detach "$MOUNT_POINT" -force >/dev/null 2>&1 || true
  open_target
  rm -f "$0"
  exit 0
fi

rm -rf "$TARGET_APP"
if [ -e "$BACKUP_APP" ]; then
  mv "$BACKUP_APP" "$TARGET_APP"
  open_target || true
fi
/usr/bin/hdiutil detach "$MOUNT_POINT" -force >/dev/null 2>&1 || true
exit 1
"""#

    private static func signingTeam(_ application: URL) throws -> String {
        let result = try run(
            "/usr/bin/codesign",
            ["-dv", "--verbose=4", application.path],
            includeStandardError: true
        )
        for line in result.split(whereSeparator: \.isNewline) {
            if line.hasPrefix("TeamIdentifier=") {
                return String(line.dropFirst("TeamIdentifier=".count))
            }
        }
        return ""
    }

    @discardableResult
    private static func run(
        _ executable: String,
        _ arguments: [String],
        includeStandardError: Bool = false
    ) throws -> String {
        let process = Process()
        let standardOutput = Pipe()
        let standardError = Pipe()
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = arguments
        process.standardOutput = standardOutput
        process.standardError = standardError
        try process.run()
        process.waitUntilExit()
        let output = standardOutput.fileHandleForReading.readDataToEndOfFile()
        let error = standardError.fileHandleForReading.readDataToEndOfFile()
        let message = String(data: includeStandardError ? output + error : output, encoding: .utf8) ?? ""
        guard process.terminationStatus == 0 else {
            let detail = String(data: error, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines)
            throw AppUpdateError.message(detail?.isEmpty == false ? detail! : "更新助手执行失败")
        }
        return message
    }

    private static func shellQuote(_ value: String) -> String {
        "'" + value.replacingOccurrences(of: "'", with: "'\\''") + "'"
    }

    private static func appleScriptQuote(_ value: String) -> String {
        "\"" + value
            .replacingOccurrences(of: "\\", with: "\\\\")
            .replacingOccurrences(of: "\"", with: "\\\"") + "\""
    }
}
