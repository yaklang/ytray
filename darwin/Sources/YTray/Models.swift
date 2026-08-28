import Foundation

enum RuntimeSource: String, Codable, CaseIterable {
    case managed
    case local
    case system
    var title: String {
        switch self {
        case .managed: return "YTray 安装"
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

struct ProxyPreset: Identifiable, Codable, Equatable, Hashable {
    var id = UUID()
    var server: String
    var remark: String
    var username: String = ""
    var password: String = ""
    var lastUsedAt = Date()

    private enum CodingKeys: String, CodingKey {
        case id, server, remark, username, password, lastUsedAt
    }

    init(
        id: UUID = UUID(),
        server: String,
        remark: String,
        username: String = "",
        password: String = "",
        lastUsedAt: Date = Date()
    ) {
        self.id = id
        self.server = server
        self.remark = remark
        self.username = username
        self.password = password
        self.lastUsedAt = lastUsedAt
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decodeIfPresent(UUID.self, forKey: .id) ?? UUID()
        server = try container.decode(String.self, forKey: .server)
        remark = try container.decodeIfPresent(String.self, forKey: .remark) ?? ""
        username = try container.decodeIfPresent(String.self, forKey: .username) ?? ""
        password = try container.decodeIfPresent(String.self, forKey: .password) ?? ""
        lastUsedAt = try container.decodeIfPresent(Date.self, forKey: .lastUsedAt) ?? Date()
    }
}

enum ProxyScheme: String, Codable, CaseIterable, Identifiable, Sendable {
    case http
    case https

    var id: String { rawValue }
    var title: String { rawValue.uppercased() }
    var defaultPort: Int { self == .https ? 443 : 80 }
}

struct ProxyEndpoint: Equatable, Sendable {
    var scheme: ProxyScheme
    var host: String
    var port: Int
    var server: String
}

enum HTTPProxyAddress {
    static func build(scheme: ProxyScheme, host: String, port: Int) throws -> String {
        var normalizedHost = host.trimmingCharacters(in: .whitespacesAndNewlines)
        if normalizedHost.hasPrefix("[") && normalizedHost.hasSuffix("]") {
            normalizedHost.removeFirst()
            normalizedHost.removeLast()
        }
        guard !normalizedHost.isEmpty,
              !normalizedHost.contains("://"),
              !normalizedHost.contains("/"),
              !normalizedHost.contains("@"),
              (1...65_535).contains(port) else {
            throw YTrayError.invalidProxy("\(scheme.rawValue)://\(host):\(port)")
        }
        var components = URLComponents()
        components.scheme = scheme.rawValue
        components.host = normalizedHost
        components.port = port
        guard let value = components.string else {
            throw YTrayError.invalidProxy("\(scheme.rawValue)://\(host):\(port)")
        }
        return value
    }

    static func split(_ value: String) throws -> ProxyEndpoint {
        let normalized = try normalize(value)
        guard let components = URLComponents(string: normalized),
              let rawScheme = components.scheme,
              let scheme = ProxyScheme(rawValue: rawScheme),
              var host = components.host else {
            throw YTrayError.invalidProxy(value)
        }
        if host.hasPrefix("[") && host.hasSuffix("]") {
            host.removeFirst()
            host.removeLast()
        }
        return ProxyEndpoint(
            scheme: scheme,
            host: host,
            port: components.port ?? scheme.defaultPort,
            server: normalized
        )
    }

    static func normalize(_ value: String) throws -> String {
        var candidate = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !candidate.isEmpty else { throw YTrayError.invalidProxy(value) }
        if !candidate.contains("://") { candidate = "http://\(candidate)" }
        guard var components = URLComponents(string: candidate),
              let scheme = components.scheme?.lowercased(),
              ["http", "https"].contains(scheme),
              let host = components.host,
              !host.isEmpty,
              components.user == nil,
              components.password == nil,
              components.port.map({ (1...65_535).contains($0) }) ?? true,
              !candidate.hasSuffix(":"),
              (components.path.isEmpty || components.path == "/"),
              components.query == nil,
              components.fragment == nil else {
            throw YTrayError.invalidProxy(value)
        }
        components.scheme = scheme
        components.path = ""
        guard let normalized = components.string, URL(string: normalized) != nil else {
            throw YTrayError.invalidProxy(value)
        }
        return normalized
    }
}

enum LaunchMode: String, Codable, CaseIterable {
    case quick
    case isolated
    case custom
    var title: String {
        switch self {
        case .quick: return "快速启动"
        case .isolated: return "快速启动"
        case .custom: return "自定义启动"
        }
    }
}

struct LaunchSettings: Codable, Equatable {
    static let currentConfigurationVersion = 5
    static let certificateDefaultMigrationVersion = 2
    static let defaultPresetProxyServer = "http://127.0.0.1:8083"

    var configurationVersion = Self.currentConfigurationVersion
    var defaultRuntimeID: UUID?
    var homeURL = "chrome://newtab"
    var proxyServer = ""
    var proxyUsername = ""
    var proxyPassword = ""
    var presetProxyServer = Self.defaultPresetProxyServer
    var presetProxyScheme = ProxyScheme.http
    var presetProxyHost = "127.0.0.1"
    var presetProxyPort = 8083
    var presetProxyUsername = ""
    var presetProxyPassword = ""
    var presetProxyRemark = ""
    var presetProxyCheckTarget = ""
    var recentProxyPresets: [ProxyPreset] = []
    var debugPort = 9222
    var restrictWebRTC = true
    var disableNotifications = true
    var ignoreCertificateErrors = true
    var colorizeBrowserInstances = true
    var additionalFlags = ""
    var defaultPluginIDs: [UUID] = []
    var dockBadge = ""

    static let blockedCustomPrefixes = [
        "--user-data-dir", "--remote-debugging-address", "--remote-debugging-port",
        "--load-extension", "--disable-extensions-except",
        "--install-autogenerated-theme",
        "--proxy-server", "--no-proxy-server", "--proxy-pac-url",
        "--proxy-auto-detect", "--proxy-bypass-list"
    ]

    private enum CodingKeys: String, CodingKey {
        case configurationVersion, defaultRuntimeID, homeURL, proxyServer, proxyUsername
        case presetProxyServer, presetProxyScheme, presetProxyHost, presetProxyPort
        case presetProxyUsername, presetProxyPassword, presetProxyRemark, presetProxyCheckTarget
        case recentProxyPresets
        case debugPort, restrictWebRTC
        case disableNotifications, ignoreCertificateErrors, colorizeBrowserInstances, additionalFlags
        case defaultPluginIDs, dockBadge
    }
}

extension LaunchSettings {
    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        let savedVersion = try container.decodeIfPresent(Int.self, forKey: .configurationVersion) ?? 0
        configurationVersion = Self.currentConfigurationVersion
        defaultRuntimeID = try container.decodeIfPresent(UUID.self, forKey: .defaultRuntimeID)
        homeURL = try container.decodeIfPresent(String.self, forKey: .homeURL) ?? "chrome://newtab"
        proxyServer = try container.decodeIfPresent(String.self, forKey: .proxyServer) ?? ""
        proxyUsername = try container.decodeIfPresent(String.self, forKey: .proxyUsername) ?? ""
        proxyPassword = ""
        let legacyServer = try container.decodeIfPresent(String.self, forKey: .presetProxyServer)
            ?? Self.defaultPresetProxyServer
        let legacyEndpoint = try? HTTPProxyAddress.split(legacyServer)
        presetProxyScheme = try container.decodeIfPresent(ProxyScheme.self, forKey: .presetProxyScheme)
            ?? legacyEndpoint?.scheme
            ?? .http
        presetProxyHost = try container.decodeIfPresent(String.self, forKey: .presetProxyHost)
            ?? legacyEndpoint?.host
            ?? "127.0.0.1"
        presetProxyPort = try container.decodeIfPresent(Int.self, forKey: .presetProxyPort)
            ?? legacyEndpoint?.port
            ?? 8083
        presetProxyServer = (try? HTTPProxyAddress.build(
            scheme: presetProxyScheme,
            host: presetProxyHost,
            port: presetProxyPort
        )) ?? Self.defaultPresetProxyServer
        presetProxyUsername = try container.decodeIfPresent(String.self, forKey: .presetProxyUsername) ?? ""
        presetProxyPassword = try container.decodeIfPresent(String.self, forKey: .presetProxyPassword) ?? ""
        presetProxyRemark = try container.decodeIfPresent(String.self, forKey: .presetProxyRemark) ?? ""
        presetProxyCheckTarget = try container.decodeIfPresent(String.self, forKey: .presetProxyCheckTarget) ?? ""
        recentProxyPresets = try container.decodeIfPresent([ProxyPreset].self, forKey: .recentProxyPresets) ?? []
        debugPort = try container.decodeIfPresent(Int.self, forKey: .debugPort) ?? 9222
        restrictWebRTC = try container.decodeIfPresent(Bool.self, forKey: .restrictWebRTC) ?? true
        disableNotifications = try container.decodeIfPresent(Bool.self, forKey: .disableNotifications) ?? true
        ignoreCertificateErrors = savedVersion < Self.certificateDefaultMigrationVersion
            ? true
            : try container.decodeIfPresent(Bool.self, forKey: .ignoreCertificateErrors) ?? true
        colorizeBrowserInstances = try container.decodeIfPresent(
            Bool.self,
            forKey: .colorizeBrowserInstances
        ) ?? true
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

enum ProxyCheckPhase: Equatable {
    case idle
    case checking
    case success
    case failure
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
    var thumbnailPath: String? = nil
    var thumbnailUpdatedAt: Date? = nil
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

struct ExtensionManifest: Decodable {
    let latest: String
    let updatedAt: String
    let versions: [ExtensionReleaseVersion]
    enum CodingKeys: String, CodingKey {
        case latest, versions
        case updatedAt = "updated_at"
    }
}

struct ExtensionReleaseVersion: Decodable, Identifiable {
    let version: String
    let publishedAt: String
    let commit: String
    let artifacts: [ExtensionArtifact]
    enum CodingKeys: String, CodingKey {
        case version, commit, artifacts
        case publishedAt = "published_at"
    }
    var id: String { version }
}

struct ExtensionArtifact: Decodable {
    let variant: String
    let browser: String
    let mode: String
    let filename: String
    let url: String
    let sha256: String
    let size: Int64?
    let checksumUrl: String
    enum CodingKeys: String, CodingKey {
        case variant, browser, mode, filename, url, sha256, size
        case checksumUrl = "checksum_url"
    }
}

enum YTrayError: LocalizedError {
    case noRuntime
    case invalidExecutable(String)
    case invalidPlugin(String)
    case invalidURL(String)
    case invalidProxy(String)
    case invalidFlag(String)
    case launchFailed(String)
    case downloadFailed(String)
    case extensionInstallFailed(String)
    case screenshotFailed(String)

    var errorDescription: String? {
        switch self {
        case .noRuntime: return "没有可用浏览器；请选择本机浏览器，或安装一个 Chrome for Testing 版本"
        case .invalidExecutable(let value): return "找不到可执行的 Chrome：\(value)"
        case .invalidPlugin(let value): return "插件目录无效：\(value)（目录根部必须包含 manifest.json）"
        case .invalidURL(let value): return "启动地址无效：\(value)"
        case .invalidProxy(let value): return "HTTP 代理地址无效：\(value)（例如 http://127.0.0.1:8083）"
        case .invalidFlag(let value): return "不允许覆盖实例隔离或调试参数：\(value)"
        case .launchFailed(let value): return "浏览器启动失败：\(value)"
        case .downloadFailed(let value): return "运行时安装失败：\(value)"
        case .extensionInstallFailed(let value): return "插件下载失败：\(value)"
        case .screenshotFailed(let value): return "快速截图失败：\(value)"
        }
    }
}
