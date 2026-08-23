import AppKit
import Combine
import ServiceManagement

enum LaunchAtLoginStatus: Equatable {
    case enabled
    case disabled
    case requiresApproval
    case unavailable

    var title: String {
        switch self {
        case .enabled: return "已开启"
        case .disabled: return "未开启"
        case .requiresApproval: return "等待系统确认"
        case .unavailable: return "当前环境不可用"
        }
    }
}

protocol LaunchAtLoginBackend {
    var status: LaunchAtLoginStatus { get }
    func register() throws
    func unregister() throws
    func openSystemSettings()
}

struct SystemLaunchAtLoginBackend: LaunchAtLoginBackend {
    var status: LaunchAtLoginStatus {
        switch SMAppService.mainApp.status {
        case .enabled: return .enabled
        case .requiresApproval: return .requiresApproval
        case .notRegistered: return .disabled
        case .notFound: return .unavailable
        @unknown default: return .unavailable
        }
    }

    func register() throws { try SMAppService.mainApp.register() }
    func unregister() throws { try SMAppService.mainApp.unregister() }
    func openSystemSettings() { SMAppService.openSystemSettingsLoginItems() }
}

enum FirstLaunchAtLoginResult: Equatable {
    case enabled
    case requiresApproval
    case failed(String)
}

@MainActor
final class LaunchAtLoginManager: ObservableObject {
    static let firstLaunchKey = "ytray.launch-at-login.setup.v1"

    @Published private(set) var status: LaunchAtLoginStatus
    @Published var errorMessage: String?

    private let backend: LaunchAtLoginBackend
    private let defaults: UserDefaults
    private let packagedApplication: Bool

    init(
        backend: LaunchAtLoginBackend = SystemLaunchAtLoginBackend(),
        defaults: UserDefaults = .standard,
        packagedApplication: Bool = Bundle.main.bundleURL.pathExtension.lowercased() == "app"
    ) {
        self.backend = backend
        self.defaults = defaults
        self.packagedApplication = packagedApplication
        status = packagedApplication ? backend.status : .unavailable
    }

    var isEnabled: Bool { status == .enabled || status == .requiresApproval }

    var statusDetail: String {
        switch status {
        case .enabled:
            return "登录 macOS 后，YTray 会自动进入菜单栏，浏览器实例仍由你手动启动。"
        case .disabled:
            return "YTray 不会随登录自动运行。你仍可随时手动打开应用。"
        case .requiresApproval:
            return "注册请求已提交，请在“系统设置 → 通用 → 登录项”中允许 YTray。"
        case .unavailable:
            return packagedApplication
                ? "系统没有找到可注册的应用。请将 YTray 放入“应用程序”后重试。"
                : "开发构建不会修改登录项；安装版中可正常使用此功能。"
        }
    }

    @discardableResult
    func setEnabled(_ enabled: Bool) -> Bool {
        guard packagedApplication else {
            errorMessage = "开发构建不会修改系统登录项。请在安装版 YTray 中使用此功能。"
            status = .unavailable
            return false
        }
        do {
            if enabled { try backend.register() }
            else { try backend.unregister() }
            refresh()
            return enabled ? isEnabled : status == .disabled
        } catch {
            refresh()
            errorMessage = enabled
                ? "无法开启开机启动：\(error.localizedDescription)"
                : "无法关闭开机启动：\(error.localizedDescription)"
            return false
        }
    }

    func refresh() {
        status = packagedApplication ? backend.status : .unavailable
    }

    func openSystemSettings() { backend.openSystemSettings() }

    func enableOnFirstLaunchIfNeeded() -> FirstLaunchAtLoginResult? {
        guard packagedApplication, !defaults.bool(forKey: Self.firstLaunchKey) else { return nil }
        defaults.set(true, forKey: Self.firstLaunchKey)
        refresh()
        if status == .enabled { return .enabled }
        if status == .requiresApproval { return .requiresApproval }
        do {
            try backend.register()
            refresh()
            return status == .requiresApproval ? .requiresApproval : .enabled
        } catch {
            refresh()
            return .failed(error.localizedDescription)
        }
    }
}

struct PreviewLaunchAtLoginBackend: LaunchAtLoginBackend {
    var status: LaunchAtLoginStatus = .enabled
    func register() throws {}
    func unregister() throws {}
    func openSystemSettings() {}
}
