import AppKit
import Combine
import Foundation
import Sparkle

enum AppUpdatePhase: Equatable {
    case idle
    case checking
    case upToDate
    case available
    case installing
    case failed
}

struct AppReleaseManifest: Decodable {
    let schemaVersion: Int
    let product: String
    let version: String
    let assets: [AppReleaseAsset]
    var releaseNotes: String? = nil
    var releaseNotesText: String? = nil

    enum CodingKeys: String, CodingKey {
        case schemaVersion = "schema_version"
        case product, version, assets
        case releaseNotes = "release_notes"
        case releaseNotesText = "release_notes_text"
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
final class AppUpdateManager: NSObject, ObservableObject, SPUUpdaterDelegate {
    static let shared = AppUpdateManager()
    nonisolated static let baseURL = "https://aliyun-oss.yaklang.com/ytray"
    static let manifestURL = URL(string: baseURL + "/latest.json")!
    static let manifestTimeout: TimeInterval = 20
    nonisolated static let maximumManifestBytes = 524_288

    @Published private(set) var phase: AppUpdatePhase = .idle
    @Published private(set) var statusText: String
    @Published private(set) var availableVersion: String?
    @Published private(set) var releaseNotesText: String?
    @Published private(set) var lastCheck: Date?
    @Published var automaticallyChecks: Bool {
        didSet { if updatesEnabled { defaults.set(automaticallyChecks, forKey: "YTrayCheckUpdates") } }
    }
    let currentVersion: String
    let updatesEnabled: Bool
    var canInstall: () -> Bool = { true }
    private let defaults: UserDefaults
    private var controller: SPUStandardUpdaterController?
    private var timer: Task<Void, Never>?
    private var postponedRelaunch: Task<Void, Never>?

    init(currentVersion: String? = nil, updatesEnabled: Bool? = nil, defaults: UserDefaults = .standard) {
        self.currentVersion = currentVersion ?? Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.0.0"
        self.updatesEnabled = updatesEnabled ?? AppEnvironment.appUpdatesEnabled
        self.defaults = defaults
        automaticallyChecks = defaults.object(forKey: "YTrayCheckUpdates") as? Bool ?? true
        statusText = self.updatesEnabled ? "当前版本 v\(self.currentVersion)" : "开发与演示环境不检查或安装正式更新"
        super.init()
    }

    var isBusy: Bool { phase == .checking || phase == .installing }
    var isUpdateAvailable: Bool {
        availableVersion.map { Self.compareVersions($0, currentVersion) == .orderedDescending } ?? false
    }
    var actionLabel: String {
        guard updatesEnabled else { return "开发版不更新" }
        switch phase {
        case .checking: return "正在检查…"
        case .installing: return "更新中…"
        default: return isUpdateAvailable ? "更新" : "检查更新"
        }
    }

    func start() {
        guard updatesEnabled, timer == nil else { return }
        timer = Task { [weak self] in
            do {
                try await Task.sleep(for: .seconds(15))
                while !Task.isCancelled {
                    if self?.automaticallyChecks == true { await self?.checkForUpdates() }
                    try await Task.sleep(for: .seconds(6 * 60 * 60))
                }
            } catch { /* Cancellation ends the quiet check timer. */ }
        }
    }

    func stop() { timer?.cancel(); timer = nil; postponedRelaunch?.cancel(); postponedRelaunch = nil }

    func checkForUpdates() async {
        guard updatesEnabled else { setPhase(.upToDate, "开发与演示环境不检查或安装正式更新"); return }
        guard !isBusy else { return }
        setPhase(.checking, "正在检查 YTray 更新…")
        do {
            let configuration = URLSessionConfiguration.ephemeral
            configuration.httpShouldSetCookies = false
            configuration.httpCookieStorage = nil
            configuration.urlCredentialStorage = nil
            configuration.timeoutIntervalForRequest = Self.manifestTimeout
            configuration.timeoutIntervalForResource = 30
            let session = URLSession(configuration: configuration)
            defer { session.invalidateAndCancel() }
            var request = URLRequest(url: Self.manifestURL)
            request.cachePolicy = .reloadIgnoringLocalCacheData
            request.setValue("no-cache", forHTTPHeaderField: "Cache-Control")
            let (bytes, response) = try await session.bytes(for: request)
            guard (response as? HTTPURLResponse)?.statusCode == 200, response.url == request.url,
                  response.expectedContentLength <= Self.maximumManifestBytes else {
                throw AppUpdateError.message("更新服务器返回了异常状态")
            }
            var data = Data()
            for try await byte in bytes {
                guard data.count < Self.maximumManifestBytes else { throw AppUpdateError.message("更新信息过大") }
                data.append(byte)
            }
            let release = try Self.parseManifest(data, architecture: Self.architecture)
            availableVersion = release.version
            releaseNotesText = release.releaseNotesText
            lastCheck = Date()
            setPhase(isUpdateAvailable ? .available : .upToDate,
                isUpdateAvailable ? "发现新版本 v\(release.version) · 当前 v\(currentVersion)" : "YTray v\(currentVersion) 已是最新版本")
        } catch {
            DiagnosticLog.error("app.update.check", error)
            setPhase(.failed, "检查更新失败，请检查网络后重试。当前版本仍可使用。")
        }
    }

    /// Only Sparkle can download, authenticate, replace and relaunch the app.
    /// The JSON catalog is used solely for a quiet availability hint.
    func installUpdate() {
        guard updatesEnabled, !isBusy else { return }
        guard canInstall() else { setPhase(.failed, "请先完成浏览器启动、组件安装或当前弹窗，再更新 YTray。"); return }
        do {
            if controller == nil {
                let driver = SPUStandardUpdaterController(startingUpdater: false, updaterDelegate: self, userDriverDelegate: nil)
                try driver.updater.start()
                driver.updater.automaticallyChecksForUpdates = false
                driver.updater.automaticallyDownloadsUpdates = false
                controller = driver
            }
            setPhase(.installing, "请在更新窗口中下载并安装，完成后自动重新打开 YTray。")
            NSApp.activate(ignoringOtherApps: true)
            controller?.checkForUpdates(nil)
        } catch {
            setPhase(.failed, "更新组件启动失败：\(error.localizedDescription)。可重试或手动下载。")
        }
    }

    func feedURLString(for updater: SPUUpdater) -> String? {
        "\(Self.baseURL)/appcast-macos-\(Self.architecture).xml"
    }

    func updater(_ updater: SPUUpdater, didFinishUpdateCycleFor updateCheck: SPUUpdateCheck, error: Error?) {
        setPhase(error == nil ? (isUpdateAvailable ? .available : .upToDate) : .failed,
            error.map { "更新未完成：\($0.localizedDescription)。当前版本仍可使用，可重试或手动下载。" }
                ?? "更新窗口已关闭，可以继续使用 YTray。")
    }

    func updater(_ updater: SPUUpdater, shouldPostponeRelaunchForUpdate item: SUAppcastItem,
                 untilInvokingBlock installHandler: @escaping () -> Void) -> Bool {
        guard !canInstall() else { return false }
        statusText = "更新已验证，等待当前浏览器启动或组件安装完成后重启。"
        postponedRelaunch = Task { [weak self] in
            do {
                while self?.canInstall() == false { try await Task.sleep(for: .seconds(1)) }
                guard !Task.isCancelled, self != nil else { return }
                installHandler()
            } catch { }
        }
        return true
    }

    func openDownloads() {
        let version = isUpdateAvailable ? availableVersion! : currentVersion
        guard let url = URL(string: "\(Self.baseURL)/\(version)/YTray-\(version)-darwin-\(Self.architecture).dmg") else { return }
        if !NSWorkspace.shared.open(url) { setPhase(.failed, "无法打开浏览器，请前往 yaklang.io/ytray/ 下载。") }
    }

    nonisolated static func parseManifest(_ data: Data, architecture: String) throws -> AppReleaseManifest {
        guard data.count <= maximumManifestBytes else { throw AppUpdateError.message("更新信息过大") }
        let release = try JSONDecoder().decode(AppReleaseManifest.self, from: data)
        guard release.schemaVersion == 1, release.product == "ytray",
              release.version.range(of: #"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z"#, options: .regularExpression) != nil,
              release.version.split(separator: ".").allSatisfy({ Int($0) != nil }),
              release.releaseNotes == nil || release.releaseNotes == "https://github.com/yaklang/ytray/releases/tag/v\(release.version)",
              (release.releaseNotesText?.count ?? 0) <= 32_000,
              let asset = selectAsset(from: release, platform: "darwin", architecture: architecture, kind: "dmg") else {
            throw AppUpdateError.message("更新清单格式无效")
        }
        let filename = "YTray-\(release.version)-darwin-\(architecture).dmg"
        guard asset.filename == filename, asset.url.absoluteString == "\(baseURL)/\(release.version)/\(filename)",
              asset.size > 0, asset.size < 536_870_912,
              asset.sha256.range(of: #"\A[a-f0-9]{64}\z"#, options: .regularExpression) != nil else {
            throw AppUpdateError.message("更新包校验信息无效")
        }
        return release
    }

    nonisolated static func selectAsset(from manifest: AppReleaseManifest, platform: String, architecture: String, kind: String) -> AppReleaseAsset? {
        let matches = manifest.assets.filter { $0.platform == platform && $0.architecture == architecture && $0.kind == kind }
        return matches.count == 1 ? matches.first : nil
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

    private func setPhase(_ value: AppUpdatePhase, _ status: String) { phase = value; statusText = status }
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
