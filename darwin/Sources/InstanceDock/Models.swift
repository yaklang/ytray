import Foundation

enum RuntimeSource: String, Codable, CaseIterable {
    case managed
    case local
    case system
    var title: String {
        switch self {
        case .managed: return "Instance Dock 安装"
        case .local: return "自定义路径"
        case .system: return "系统环境"
        }
    }
}

enum BrowserKind: String, Codable, CaseIterable {
    case chrome
    case chromeBeta
    case chromeCanary
    case chromeForTesting
    case chromium
    case edge

    var title: String {
        switch self {
        case .chrome: return "Google Chrome"
        case .chromeBeta: return "Chrome Beta"
        case .chromeCanary: return "Chrome Canary"
        case .chromeForTesting: return "Chrome for Testing"
        case .chromium: return "Chromium"
        case .edge: return "Microsoft Edge"
        }
    }

    var symbol: String {
        switch self {
        case .edge: return "wave.3.right.circle.fill"
        case .chromium: return "circle.hexagongrid.fill"
        default: return "globe.europe.africa.fill"
        }
    }

    static func infer(name: String, path: String) -> BrowserKind {
        let value = "\(name) \(path)".lowercased()
        if value.contains("for testing") { return .chromeForTesting }
        if value.contains("microsoft edge") || value.contains("/edge") { return .edge }
        if value.contains("canary") { return .chromeCanary }
        if value.contains("beta") { return .chromeBeta }
        if value.contains("chromium") { return .chromium }
        return .chrome
    }
}

struct BrowserRuntime: Identifiable, Codable, Equatable, Hashable {
    var id = UUID()
    var name: String
    var version: String
    var architecture: String
    var executablePath: String
    var source: RuntimeSource
    var browserKind: BrowserKind? = nil
    var createdAt = Date()

    var kind: BrowserKind { browserKind ?? .infer(name: name, path: executablePath) }
    var displayTitle: String { kind.title }
    var versionLabel: String { version.isEmpty ? "版本未知" : version }
    var isSystemEnvironment: Bool {
        source == .system || executablePath.hasPrefix("/Applications/")
            || executablePath.hasPrefix(FileManager.default.homeDirectoryForCurrentUser
                .appendingPathComponent("Applications").path + "/")
    }
}

struct BrowserPlugin: Identifiable, Codable, Equatable, Hashable {
    var id = UUID()
    var name: String
    var version: String
    var path: String
    var manifestVersion: Int
    var enabled = true
    var createdAt = Date()
}

enum LaunchMode: String, Codable, CaseIterable {
    case quick
    case isolated
    case custom
    var title: String {
        switch self {
        case .quick: return "启动新实例"
        case .isolated: return "启动新实例"
        case .custom: return "自定义启动"
        }
    }
}

struct LaunchSettings: Codable, Equatable {
    var defaultRuntimeID: UUID?
    var homeURL = "chrome://newtab"
    var proxyServer = ""
    var debugPort = 9222
    var restrictWebRTC = true
    var disableNotifications = true
    var ignoreCertificateErrors = false
    var additionalFlags = ""
    var defaultPluginIDs: [UUID] = []
    var dockBadge = ""

    static let blockedCustomPrefixes = [
        "--user-data-dir", "--remote-debugging-address", "--remote-debugging-port",
        "--load-extension", "--disable-extensions-except"
    ]

    private enum CodingKeys: String, CodingKey {
        case defaultRuntimeID, homeURL, proxyServer, debugPort, restrictWebRTC
        case disableNotifications, ignoreCertificateErrors, additionalFlags
        case defaultPluginIDs, dockBadge
    }
}

extension LaunchSettings {
    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        defaultRuntimeID = try container.decodeIfPresent(UUID.self, forKey: .defaultRuntimeID)
        homeURL = try container.decodeIfPresent(String.self, forKey: .homeURL) ?? "chrome://newtab"
        proxyServer = try container.decodeIfPresent(String.self, forKey: .proxyServer) ?? ""
        debugPort = try container.decodeIfPresent(Int.self, forKey: .debugPort) ?? 9222
        restrictWebRTC = try container.decodeIfPresent(Bool.self, forKey: .restrictWebRTC) ?? true
        disableNotifications = try container.decodeIfPresent(Bool.self, forKey: .disableNotifications) ?? true
        ignoreCertificateErrors = try container.decodeIfPresent(Bool.self, forKey: .ignoreCertificateErrors) ?? false
        additionalFlags = try container.decodeIfPresent(String.self, forKey: .additionalFlags) ?? ""
        defaultPluginIDs = try container.decodeIfPresent([UUID].self, forKey: .defaultPluginIDs) ?? []
        dockBadge = try container.decodeIfPresent(String.self, forKey: .dockBadge) ?? ""
    }
}

enum InstanceStatus: String, Codable {
    case running
    case stopped
    case failed
    var title: String {
        switch self {
        case .running: return "运行中"
        case .stopped: return "已停止"
        case .failed: return "启动失败"
        }
    }
}

enum BrowserLaunchPhase: Equatable {
    case idle
    case preparing
    case waiting
    case succeeded
}

struct BrowserInstance: Identifiable, Codable, Equatable {
    var id = UUID()
    var name: String
    var runtimeID: UUID
    var runtimeName: String
    var runtimeVersion: String? = nil
    var runtimeKind: BrowserKind? = nil
    var runtimeSource: RuntimeSource? = nil
    var mode: LaunchMode
    var processID: Int32
    var debugPort: Int
    var profilePath: String
    var startURL: String
    var startedAt = Date()
    var status: InstanceStatus = .running
    var lastScreenshotPath: String?
    var lastPageTitle: String? = nil
    var lastPageURL: String? = nil
    var dockBadge: String? = nil
    var settingsSnapshot: LaunchSettings? = nil
    var pluginIDs: [UUID]? = nil
}

struct PersistedState: Codable {
    var runtimes: [BrowserRuntime] = []
    var plugins: [BrowserPlugin] = []
    var instances: [BrowserInstance] = []
    var settings = LaunchSettings()
}

struct MirrorManifest: Decodable {
    let generatedAt: String?
    let versions: [MirrorVersion]
}

struct MirrorVersion: Decodable, Identifiable {
    let version: String
    let artifacts: [MirrorArtifact]
    var id: String { version }
}

struct MirrorArtifact: Decodable {
    let os: String
    let arch: String
    let format: String
    let url: String
    let size: Int64?
    let sha256: String
}

struct PluginManifest: Decodable {
    let name: String
    let version: String
    let manifestVersion: Int
    enum CodingKeys: String, CodingKey {
        case name, version
        case manifestVersion = "manifest_version"
    }
}

enum InstanceDockError: LocalizedError {
    case noRuntime
    case invalidExecutable(String)
    case invalidPlugin(String)
    case invalidURL(String)
    case invalidFlag(String)
    case launchFailed(String)
    case downloadFailed(String)
    case screenshotFailed(String)

    var errorDescription: String? {
        switch self {
        case .noRuntime: return "没有可用浏览器；请选择本机浏览器，或安装一个 Chrome for Testing 版本"
        case .invalidExecutable(let value): return "找不到可执行的 Chrome：\(value)"
        case .invalidPlugin(let value): return "插件目录无效：\(value)（目录根部必须包含 manifest.json）"
        case .invalidURL(let value): return "启动地址无效：\(value)"
        case .invalidFlag(let value): return "不允许覆盖实例隔离或调试参数：\(value)"
        case .launchFailed(let value): return "浏览器启动失败：\(value)"
        case .downloadFailed(let value): return "运行时安装失败：\(value)"
        case .screenshotFailed(let value): return "快速截图失败：\(value)"
        }
    }
}
