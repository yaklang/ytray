import AppKit
import SwiftUI

enum ManagerSection: String, CaseIterable, Identifiable {
    case quick = "快速配置"
    case runtimes = "浏览器运行时"
    case settings = "启动设置"
    case instances = "运行与历史"
    case plugins = "插件管理"
    case launchAtLogin = "开机启动"
    var id: String { rawValue }
    var icon: String {
        switch self {
        case .quick: return "bolt.fill"
        case .runtimes: return "shippingbox"
        case .settings: return "slider.horizontal.3"
        case .instances: return "rectangle.stack"
        case .plugins: return "puzzlepiece.extension"
        case .launchAtLogin: return "power"
        }
    }
}

@MainActor
final class ManagerNavigation: ObservableObject {
    @Published var selection: ManagerSection? = .quick
    @Published var columnVisibility: NavigationSplitViewVisibility = .all
}

@MainActor
struct ManagerView: View {
    @ObservedObject var store: InstanceStore
    @ObservedObject var navigation: ManagerNavigation
    @ObservedObject var launchAtLogin: LaunchAtLoginManager
    @ObservedObject private var updater = AppUpdateManager.shared
    @StateObject private var updatePopover = AppUpdatePopoverPresenter(updater: .shared)
    let quitApplication: () -> Void
    @State private var showWizard = false

    var body: some View {
        NavigationSplitView(columnVisibility: $navigation.columnVisibility) {
            List(ManagerSection.allCases) { section in
                ManagerSidebarRow(section: section, isSelected: navigation.selection == section) {
                    navigation.selection = section
                }
                .listRowInsets(EdgeInsets(top: 2, leading: 10, bottom: 2, trailing: 10))
                .listRowBackground(Color.clear)
            }
            .listStyle(.sidebar)
            .navigationSplitViewColumnWidth(min: 200, ideal: ManagerMetrics.sidebarWidth, max: 240)
            .navigationTitle("YTray")
            .safeAreaInset(edge: .bottom, spacing: 0) {
                VStack(spacing: 0) {
                    Divider()
                    Button { store.openDiagnosticLog() } label: {
                        HStack(spacing: 8) {
                            Image(systemName: "doc.text.magnifyingglass")
                                .font(.system(size: 13, weight: .medium))
                            Text("诊断日志").font(.caption)
                            Spacer()
                        }
                        .foregroundStyle(.secondary)
                        .contentShape(Rectangle())
                        .padding(.horizontal, 12)
                        .frame(height: 36)
                    }
                    .buttonStyle(.plain)
                    .keyboardShortcut("l", modifiers: [.command, .shift])
                    .help("打开诊断日志 · \(store.diagnosticLogPath)")
                    .accessibilityLabel("打开诊断日志")
                }
                .background(.regularMaterial)
            }
        } detail: {
            Group {
                switch navigation.selection ?? .quick {
                case .quick: QuickLaunchPage(store: store, showWizard: $showWizard)
                case .runtimes: RuntimePage(store: store)
                case .settings: SettingsPage(store: store, quitApplication: quitApplication)
                case .instances: InstancesPage(store: store)
                case .plugins: PluginsPage(store: store)
                case .launchAtLogin: LaunchAtLoginPage(manager: launchAtLogin)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(Color(nsColor: .windowBackgroundColor))
        }
        .navigationSplitViewStyle(.balanced)
        .buttonStyle(ManagerButtonStyle())
        .controlSize(.regular)
        .tint(Brand.orange)
        .accentColor(Brand.orange)
        .toolbar {
            ToolbarItem(placement: .navigation) {
                AppUpdateVersionButton(updater: updater, presenter: updatePopover)
            }
        }
        .sheet(isPresented: $showWizard) { CustomLaunchWizard(store: store, isPresented: $showWizard) }
        .overlay(alignment: .topTrailing) {
            if store.launchPhase != .idle {
                LaunchStatusLine(phase: store.launchPhase, message: store.launchMessage)
                    .padding(.horizontal, 12)
                    .frame(height: 34)
                    .background(.regularMaterial)
                    .clipShape(Capsule())
                    .overlay(Capsule().stroke(Brand.orange.opacity(0.28)))
                    .shadow(color: .black.opacity(0.14), radius: 8, y: 3)
                    .padding(14)
                    .transition(.move(edge: .top).combined(with: .opacity))
            }
        }
        .animation(.easeOut(duration: 0.18), value: store.launchPhase)
        .alert("YTray", isPresented: Binding(
            get: { store.errorMessage != nil },
            set: { if !$0 { store.errorMessage = nil } }
        )) { Button("知道了") { store.errorMessage = nil } } message: { Text(store.errorMessage ?? "") }
    }
}

@MainActor
private struct AppUpdateVersionButton: View {
    @ObservedObject var updater: AppUpdateManager
    let presenter: AppUpdatePopoverPresenter
    @State private var isHovered = false

    private var displayVersion: String {
        updater.isUpdateAvailable ? updater.availableVersion ?? updater.currentVersion : updater.currentVersion
    }

    var body: some View {
        Button {
            presenter.toggle()
        } label: {
            HStack(spacing: 6) {
                Text("v\(displayVersion)").monospacedDigit()
                if updater.isUpdateAvailable {
                    Circle().fill(Brand.orange).frame(width: 7, height: 7)
                }
            }
            .font(.system(size: 12, weight: .medium))
            .foregroundStyle(updater.isUpdateAvailable ? Color.orange : Color.secondary)
            .padding(.horizontal, 9)
            .frame(height: 24)
            .background(
                updater.isUpdateAvailable ? Color.yellow.opacity(0.30)
                    : Color.primary.opacity(isHovered ? 0.06 : 0),
                in: Capsule()
            )
        }
        .buttonStyle(.plain)
        .background(AppUpdatePopoverAnchor(presenter: presenter))
        .onHover { isHovered = $0 }
        .help(Text(updater.isUpdateAvailable
            ? "发现新版本 v\(displayVersion)，当前版本 v\(updater.currentVersion)"
            : "YTray v\(updater.currentVersion) · 查看版本与更新"))
        .accessibilityLabel(Text(updater.isUpdateAvailable
            ? "YTray v\(displayVersion) 可更新，当前版本 v\(updater.currentVersion)"
            : "YTray 当前版本 v\(updater.currentVersion)"))
        .accessibilityIdentifier("YTray 版本与更新")
    }
}

/// Keeps popover ownership outside the SwiftUI toolbar item, whose native host can be
/// replaced during state updates on macOS 26.
@MainActor
private final class AppUpdatePopoverPresenter: NSObject, ObservableObject, NSPopoverDelegate {
    private let updater: AppUpdateManager
    private let popover = NSPopover()
    private weak var anchor: NSView?

    init(updater: AppUpdateManager) {
        self.updater = updater
        super.init()
        popover.delegate = self
        popover.behavior = .transient
        popover.animates = true
    }

    func attach(to view: NSView) { anchor = view }

    func toggle() {
        if popover.isShown {
            popover.performClose(nil)
            return
        }
        guard let anchor, anchor.window != nil else { return }
        let hosting = NSHostingController(rootView: AppUpdatePopover(updater: updater) { [weak self] in
            self?.popover.performClose(nil)
        })
        hosting.view.layoutSubtreeIfNeeded()
        let fitting = hosting.view.fittingSize
        popover.contentSize = NSSize(width: 400, height: min(450, max(230, fitting.height)))
        popover.contentViewController = hosting
        popover.show(relativeTo: anchor.bounds, of: anchor, preferredEdge: .minY)
        Task { await updater.checkForUpdates() }
    }

    func popoverShouldClose(_ popover: NSPopover) -> Bool {
        guard let event = NSApp.currentEvent,
              event.type == .leftMouseDown,
              let anchor,
              event.window === anchor.window
        else { return true }
        return !anchor.bounds.contains(anchor.convert(event.locationInWindow, from: nil))
    }
}

@MainActor
private struct AppUpdatePopoverAnchor: NSViewRepresentable {
    let presenter: AppUpdatePopoverPresenter

    func makeNSView(context: Context) -> NSView {
        let view = NSView()
        presenter.attach(to: view)
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {
        presenter.attach(to: nsView)
    }
}

@MainActor
private struct AppUpdatePopover: View {
    @ObservedObject var updater: AppUpdateManager
    let dismiss: () -> Void

    private var displayVersion: String {
        updater.isUpdateAvailable ? updater.availableVersion ?? updater.currentVersion : updater.currentVersion
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 9) {
                Text("YTray v\(displayVersion)")
                    .font(.system(size: 16, weight: .semibold))
                if updater.isUpdateAvailable {
                    Text("新版本")
                        .font(.system(size: 10.5, weight: .semibold))
                        .foregroundStyle(Color.orange)
                        .padding(.horizontal, 8).padding(.vertical, 3)
                        .background(Color.yellow.opacity(0.30), in: Capsule())
                }
                Spacer()
            }
            Text(updater.statusText)
                .font(.caption)
                .foregroundStyle(updater.phase == .failed ? Color.red : Color.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Divider()
            ScrollView {
                Text(notes)
                    .font(.system(size: 12))
                    .foregroundStyle(.secondary)
                    .textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .frame(maxHeight: 235)
            Text(updater.lastCheck.map { "上次检查：\($0.formatted())" } ?? "尚未检查更新")
                .font(.caption2).foregroundStyle(.tertiary)
            HStack(spacing: 8) {
                if updater.releaseNotesURL != nil {
                    Button("查看完整说明") { updater.openReleaseNotes() }
                }
                Spacer()
                Button("手动下载") { updater.openDownloads() }
                    .disabled(!updater.updatesEnabled)
                Button(updater.isUpdateAvailable ? "立即更新" : updater.actionLabel) {
                    if updater.isUpdateAvailable {
                        dismiss()
                        updater.installUpdate()
                    } else {
                        Task { await updater.checkForUpdates() }
                    }
                }
                .buttonStyle(ManagerButtonStyle(emphasis: .primary))
                .disabled(updater.isBusy || !updater.updatesEnabled)
            }
        }
        .padding(16)
        .frame(width: 400)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("YTray 版本更新弹层")
    }

    private var notes: String {
        if let notes = updater.releaseNotesText, !notes.isEmpty { return notes }
        return updater.isBusy ? "正在获取版本说明…" : "检查更新后可查看当前版本说明。"
    }
}

private struct ManagerSidebarRow: View {
    let section: ManagerSection
    let isSelected: Bool
    let action: () -> Void
    @State private var isHovered = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 9) {
                Image(systemName: section.icon)
                    .foregroundStyle(Brand.orange)
                    .frame(width: 17)
                Text(section.rawValue)
                    .lineLimit(1)
                    .fixedSize(horizontal: true, vertical: false)
                    .foregroundStyle(Color.primary)
            }
                .font(.system(size: 13, weight: isSelected ? .semibold : .medium))
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, 10)
                .frame(height: 36)
                .background(
                    isSelected ? Brand.orange.opacity(0.18)
                        : Color.primary.opacity(isHovered ? 0.055 : 0),
                    in: RoundedRectangle(cornerRadius: 8)
                )
                .overlay(alignment: .leading) {
                    if isSelected {
                        Capsule().fill(Brand.orange).frame(width: 3, height: 20).padding(.leading, 1)
                    }
                }
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { isHovered = $0 }
        .accessibilityAddTraits(isSelected ? .isSelected : [])
    }
}

struct LaunchAtLoginPage: View {
    @ObservedObject var manager: LaunchAtLoginManager
    @State private var confirmDisable = false

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: ManagerMetrics.sectionSpacing) {
                PageHeader(
                    title: "开机启动",
                    subtitle: "让 YTray 在你登录系统后自动进入菜单栏，不自动打开浏览器实例。"
                )

                GroupBox {
                    HStack(spacing: 18) {
                        ZStack {
                            Circle().fill(statusColor.opacity(0.14)).frame(width: 44, height: 44)
                            Image(systemName: manager.isEnabled ? "power.circle.fill" : "power.circle")
                                .font(.system(size: 26))
                                .foregroundStyle(statusColor)
                        }
                        VStack(alignment: .leading, spacing: 6) {
                            Text(manager.status.title).font(.title3.bold())
                            Text(manager.statusDetail).font(.callout).foregroundStyle(.secondary)
                                .fixedSize(horizontal: false, vertical: true)
                        }
                        Spacer(minLength: 18)
                        Toggle("", isOn: Binding(
                            get: { manager.isEnabled },
                            set: { enabled in
                                if enabled { _ = manager.setEnabled(true) }
                                else { confirmDisable = true }
                            }
                        ))
                        .labelsHidden()
                        .toggleStyle(.switch)
                        .tint(Brand.orange)
                        .disabled(manager.status == .unavailable)
                    }
                    .padding(10)
                } label: {
                    Label("随登录自动运行", systemImage: "person.crop.circle.badge.clock")
                }

                VStack(alignment: .leading, spacing: 12) {
                    Label("默认开启，但始终由你掌控", systemImage: "checkmark.shield")
                        .font(.headline)
                    Text("YTray 首次从安装版启动时会尝试开启，并明确告诉你结果。关闭操作需要再次确认，防止误触。")
                        .foregroundStyle(.secondary)
                    Label("不会自动启动浏览器", systemImage: "hand.raised")
                        .font(.headline)
                    Text("此设置只让 YTray 驻留菜单栏。代理、插件和浏览器实例仍需由你点击启动。")
                        .foregroundStyle(.secondary)
                }
                .padding(12)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(Color.primary.opacity(0.035))
                .clipShape(RoundedRectangle(cornerRadius: 12))

                HStack {
                    Button("刷新状态") { manager.refresh() }
                        .buttonStyle(ManagerButtonStyle())
                    Button("打开系统登录项设置") { manager.openSystemSettings() }
                        .buttonStyle(ManagerButtonStyle())
                    Spacer()
                }
            }
            .padding(ManagerMetrics.pagePadding)
        }
        .confirmationDialog("确认关闭开机启动？", isPresented: $confirmDisable) {
            Button("关闭开机启动", role: .destructive) { _ = manager.setEnabled(false) }
            Button("取消", role: .cancel) {}
        } message: {
            Text("关闭后，登录系统时 YTray 将不会自动进入菜单栏。")
        }
        .alert("开机启动设置失败", isPresented: Binding(
            get: { manager.errorMessage != nil },
            set: { if !$0 { manager.errorMessage = nil } }
        )) {
            Button("知道了") { manager.errorMessage = nil }
        } message: {
            Text(manager.errorMessage ?? "")
        }
    }

    private var statusColor: Color {
        switch manager.status {
        case .enabled: return .green
        case .requiresApproval: return Brand.orange
        case .disabled, .unavailable: return .secondary
        }
    }
}

struct PageHeader: View {
    let title: String
    let subtitle: String
    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            Text(title).font(.system(size: 20, weight: .semibold))
            Text(subtitle).font(.subheadline).foregroundStyle(.secondary)
        }.frame(maxWidth: .infinity, alignment: .leading)
    }
}

struct QuickLaunchPage: View {
    @ObservedObject var store: InstanceStore
    @Binding var showWizard: Bool
    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: ManagerMetrics.sectionSpacing) {
                PageHeader(title: "快速配置", subtitle: "选择直连或预设 HTTP 代理启动；每个实例仍使用独立用户目录。")
                HStack(spacing: 12) {
                    LaunchModeCard(icon: "network.slash", title: "无代理启动", detail: "忽略预设代理，以直连方式创建新的独立实例",
                                   button: "无代理启动", isLoading: store.isLaunching && store.launchingUsesProxy == false,
                                   disabled: store.isLaunching) { store.launchConfigured(usePresetProxy: false) }
                    LaunchModeCard(icon: "network", title: "使用 HTTP 代理", detail: "使用托盘中保存的预设代理创建新的独立实例",
                                   button: "使用代理启动", isLoading: store.isLaunching && store.launchingUsesProxy == true,
                                   disabled: store.isLaunching) { store.launchConfigured(usePresetProxy: true) }
                    LaunchModeCard(icon: "wand.and.stars", title: "自定义启动", detail: "通过步骤向导配置本次运行，不覆盖默认设置",
                                   button: "开始配置", isLoading: false,
                                   disabled: store.isLaunching) { showWizard = true }
                }
                GroupBox("当前默认配置") {
                    Grid(alignment: .leading, horizontalSpacing: 20, verticalSpacing: 8) {
                        GridRow { Text("运行时").foregroundStyle(.secondary); Text(defaultRuntimeName) }
                        GridRow { Text("启动地址").foregroundStyle(.secondary); Text(store.settings.homeURL) }
                        GridRow { Text("调试端口").foregroundStyle(.secondary); Text("127.0.0.1:\(store.settings.debugPort) 起自动避让") }
                        GridRow { Text("插件").foregroundStyle(.secondary); Text("\(store.settings.defaultPluginIDs.count) 个") }
                    }.padding(8).frame(maxWidth: .infinity, alignment: .leading)
                }
            }.padding(ManagerMetrics.pagePadding)
        }
    }
    private var defaultRuntimeName: String {
        guard let runtime = store.defaultRuntime else { return "未设置" }
        return "\(runtime.displayTitle) \(runtime.versionLabel) · \(runtime.source.title)"
    }
}

struct LaunchModeCard: View {
    let icon: String
    let title: String
    let detail: String
    let button: String
    let isLoading: Bool
    let disabled: Bool
    let action: () -> Void
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Label(title, systemImage: icon)
                .font(.system(size: 14, weight: .semibold))
                .foregroundStyle(Brand.orange)
            Text(detail).font(.system(size: 12)).foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
                .frame(minHeight: 34, alignment: .topLeading)
            Button(action: action) {
                LaunchActionLabel(title: button, systemImage: icon, isLoading: isLoading)
            }
            .buttonStyle(ManagerButtonStyle(emphasis: .primary))
            .disabled(disabled)
            .frame(maxWidth: .infinity, alignment: .leading)
        }.padding(12).frame(maxWidth: .infinity, minHeight: 130, alignment: .leading)
        .background(Brand.orange.opacity(0.065)).clipShape(RoundedRectangle(cornerRadius: 12))
        .overlay(RoundedRectangle(cornerRadius: 12).stroke(Brand.orange.opacity(0.22)))
    }
}

struct RuntimePage: View {
    @ObservedObject var store: InstanceStore
    @State private var selectedVersion = ""
    @State private var runtimePendingRemoval: BrowserRuntime?
    var body: some View {
        VStack(alignment: .leading, spacing: ManagerMetrics.sectionSpacing) {
            PageHeader(title: "浏览器来源", subtitle: "系统浏览器会自动识别并可直接使用；安装 Chrome for Testing 只是可选项。")
            VStack(spacing: 0) {
                HStack(spacing: 8) {
                    Label("本机浏览器", systemImage: "desktopcomputer")
                        .font(.callout.weight(.semibold))
                    Spacer()
                    Button("重新扫描") { store.refreshSystemBrowsers() }
                        .buttonStyle(ManagerButtonStyle())
                    Button("添加本地浏览器…") { chooseRuntime() }
                        .buttonStyle(ManagerButtonStyle())
                }
                .padding(.horizontal, 12)
                .frame(height: 44)
                Divider()
                HStack(spacing: 8) {
                    Label("Chrome for Testing", systemImage: "shippingbox")
                        .font(.callout.weight(.semibold))
                    Spacer()
                    ManagerPicker(title: "Chrome for Testing 版本",
                                  selectedTitle: selectedVersion.isEmpty ? "选择镜像版本" : selectedVersion,
                                  selection: $selectedVersion) {
                        Text(store.availableVersions.isEmpty ? "请先刷新版本" : "选择镜像版本").tag("")
                        ForEach(store.availableVersions) { Text($0.version).tag($0.version) }
                    }
                    .frame(width: 180)
                    Button("刷新版本") { Task { await store.refreshManifest() } }
                        .buttonStyle(ManagerButtonStyle())
                    Button {
                        guard let version = store.availableVersions.first(where: { $0.version == selectedVersion }) else { return }
                        Task { await store.install(version: version) }
                    } label: {
                        HStack(spacing: 5) {
                            if store.isInstalling { ProgressView().controlSize(.small).scaleEffect(0.72) }
                            Text(store.isInstalling ? "安装中…" : "安装")
                        }
                    }
                    .buttonStyle(ManagerButtonStyle(emphasis: .primary))
                    .disabled(selectedVersion.isEmpty || store.isInstalling)
                }
                .padding(.horizontal, 12)
                .frame(height: 44)
            }
            .background(Color.primary.opacity(0.035), in: RoundedRectangle(cornerRadius: 10))
            .overlay(RoundedRectangle(cornerRadius: 10).stroke(Color.primary.opacity(0.10)))
            if store.isInstalling {
                VStack(alignment: .leading, spacing: 7) {
                    HStack(spacing: 8) {
                        Text(store.runtimeInstallProgress?.message ?? store.activityMessage)
                            .font(.caption).fontWeight(.medium)
                        Spacer()
                        if let progress = store.runtimeInstallProgress,
                           progress.phase == .downloading,
                           progress.totalBytes != nil {
                            Text("\(progress.percent)%")
                                .font(.caption.monospacedDigit())
                                .foregroundStyle(.secondary)
                        }
                    }
                    ProgressView(
                        value: Double(store.runtimeInstallProgress?.percent ?? 0),
                        total: 100
                    )
                    .progressViewStyle(.linear)
                    .tint(Brand.orange)
                    HStack {
                        Text(runtimeInstallDetail)
                        Spacer()
                        Text(runtimeInstallStage)
                    }
                    .font(.caption2)
                    .foregroundStyle(.secondary)
                }
                .padding(.horizontal, 12)
                .padding(.vertical, 10)
                .background(Color.primary.opacity(0.035), in: RoundedRectangle(cornerRadius: 9))
                .transition(.opacity.combined(with: .move(edge: .top)))
            }
            profileRootBar
            Table(store.runtimes) {
                TableColumn("浏览器") { runtime in
                    HStack(spacing: 10) {
                        RuntimeArtwork(runtime: runtime)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(runtime.displayTitle).font(.callout.weight(.semibold)).lineLimit(1)
                            Text(runtime.executablePath)
                                .font(.caption2)
                                .foregroundStyle(.tertiary)
                                .lineLimit(1)
                        }
                    }
                    .padding(.vertical, 4)
                    .help(Text(runtime.executablePath))
                }
                .width(min: 180, ideal: 200)
                TableColumn("版本 / 架构") { runtime in
                    VStack(alignment: .leading, spacing: 2) {
                        Text(runtime.versionLabel).font(.callout.monospacedDigit()).lineLimit(1)
                        Text(runtime.architecture).font(.caption2).foregroundStyle(.secondary)
                    }
                }
                .width(110)
                TableColumn("来源") { runtime in
                    StatusBadge(text: runtime.source.title,
                                color: runtime.isSystemEnvironment ? .green : Brand.orange)
                }
                .width(78)
                TableColumn("默认") { runtime in
                    if store.settings.defaultRuntimeID == runtime.id {
                        StatusBadge(text: "当前默认", color: Brand.orange)
                    } else {
                        Button("设为默认") { store.selectDefaultRuntime(runtime) }
                            .buttonStyle(ManagerButtonStyle())
                    }
                }
                .width(88)
                TableColumn("操作") { runtime in
                    if runtime.source != .system {
                        Button(role: .destructive) {
                            if runtime.source == .managed { runtimePendingRemoval = runtime }
                            else { store.removeRuntime(runtime) }
                        } label: { Image(systemName: "trash") }
                            .buttonStyle(ManagerButtonStyle(emphasis: .destructive, iconOnly: true))
                            .help(Text(runtime.source == .managed
                                ? "卸载此 Chrome for Testing" : "移除此浏览器记录"))
                            .accessibilityLabel(Text(runtime.source == .managed
                                ? "卸载此 Chrome for Testing" : "移除此浏览器记录"))
                    } else {
                        Image(systemName: "lock.fill")
                            .foregroundStyle(.tertiary)
                            .help("系统浏览器由 macOS 管理")
                            .accessibilityLabel("系统浏览器由 macOS 管理")
                    }
                }
                .width(44)
            }
            .clipShape(RoundedRectangle(cornerRadius: 9))
            .overlay {
                RoundedRectangle(cornerRadius: 9).stroke(Color.primary.opacity(0.10))
                if store.runtimes.isEmpty {
                    ContentUnavailableView("未发现浏览器", systemImage: "globe",
                                           description: Text("添加本地浏览器，或安装 Chrome for Testing"))
                }
            }
        }.padding(ManagerMetrics.pagePadding).task { if store.availableVersions.isEmpty { await store.refreshManifest() } }
        .animation(.easeOut(duration: 0.16), value: store.isInstalling)
        .alert("卸载 Chrome for Testing？", isPresented: Binding(
            get: { runtimePendingRemoval != nil },
            set: { if !$0 { runtimePendingRemoval = nil } }
        ), presenting: runtimePendingRemoval) { runtime in
            Button("卸载", role: .destructive) { store.uninstallRuntime(runtime); runtimePendingRemoval = nil }
            Button("取消", role: .cancel) { runtimePendingRemoval = nil }
        } message: { runtime in
            Text("将删除 Chrome for Testing \(runtime.versionLabel) 的程序文件；实例历史和用户数据会保留。")
        }
    }

    private var runtimeInstallStage: String {
        switch store.runtimeInstallProgress?.phase {
        case .connecting, .none: return "连接镜像"
        case .downloading: return "下载中"
        case .verifying: return "校验文件"
        case .extracting: return "解压安装"
        case .completed: return "安装完成"
        }
    }

    private var profileRootBar: some View {
        HStack(spacing: 10) {
            Label("可用浏览器", systemImage: "globe")
                .font(.callout.weight(.semibold))
            Text("\(store.runtimes.count)")
                .font(.caption)
                .foregroundStyle(.secondary)
                .padding(.horizontal, 7)
                .padding(.vertical, 2)
                .background(Color.primary.opacity(0.06), in: Capsule())
            Divider().frame(height: 18)
            Text("新实例数据").font(.caption).foregroundStyle(.secondary)
            Text(store.resolveProfileRoot().path)
                .font(.caption.monospaced())
                .lineLimit(1)
                .textSelection(.enabled)
                .help(Text(store.resolveProfileRoot().path))
            Spacer()
            if !store.settings.profileRootPath.isEmpty {
                Button("恢复默认") { store.setProfileRoot(nil) }
                    .buttonStyle(ManagerButtonStyle())
            }
            Button("选择目录…") { chooseProfileRoot() }
                .buttonStyle(ManagerButtonStyle())
        }
        .padding(.horizontal, 12)
        .frame(height: 42)
        .background(Color.primary.opacity(0.035), in: RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(Color.primary.opacity(0.10)))
    }

    private var runtimeInstallDetail: String {
        if let summary = store.runtimeInstallProgress?.byteSummary { return summary }
        switch store.runtimeInstallProgress?.phase {
        case .verifying: return "下载已完成"
        case .extracting: return "文件校验已通过"
        case .completed: return "已写入浏览器目录"
        case .connecting, .downloading, .none: return "正在准备下载…"
        }
    }

    private func chooseRuntime() {
        let panel = NSOpenPanel()
        panel.title = "选择 Chrome.app 或 Chrome 可执行文件"
        panel.canChooseDirectories = true
        panel.canChooseFiles = true
        panel.allowsMultipleSelection = false
        if panel.runModal() == .OK, let url = panel.url { store.addLocalRuntime(selectedURL: url) }
    }

    private func chooseProfileRoot() {
        let panel = NSOpenPanel()
        panel.title = "选择浏览器实例数据的父目录"
        panel.message = "YTray 会为每个实例创建独立子目录。"
        panel.prompt = "选择"
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.allowsMultipleSelection = false
        let current = store.resolveProfileRoot()
        panel.directoryURL = FileManager.default.fileExists(atPath: current.path)
            ? current : current.deletingLastPathComponent()
        if panel.runModal() == .OK, let url = panel.url { store.setProfileRoot(url) }
    }
}

private struct RuntimeArtwork: View {
    let runtime: BrowserRuntime

    var body: some View {
        Image(nsImage: icon)
            .resizable()
            .interpolation(.high)
            .frame(width: 30, height: 30)
    }

    private var icon: NSImage {
        var url = URL(fileURLWithPath: runtime.executablePath)
        while url.path != "/" {
            if url.pathExtension.lowercased() == "app" {
                return NSWorkspace.shared.icon(forFile: url.path)
            }
            url.deleteLastPathComponent()
        }
        return NSWorkspace.shared.icon(forFile: runtime.executablePath)
    }
}

@MainActor
struct SettingsPage: View {
    @ObservedObject var store: InstanceStore
    @ObservedObject private var updater = AppUpdateManager.shared
    let quitApplication: () -> Void

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: ManagerMetrics.sectionSpacing) {
                HStack(alignment: .top, spacing: 10) {
                    PageHeader(title: "启动设置", subtitle: "设置新实例的默认运行时、网络行为与 Chromium 附加参数。")
                    Button("退出 YTray", action: quitApplication)
                        .buttonStyle(ManagerButtonStyle(emphasis: .destructive))
                        .keyboardShortcut("q", modifiers: .command)
                        .help("退出 YTray；已启动的浏览器继续运行")
                    Button("保存设置") { store.saveSettings() }
                        .buttonStyle(ManagerButtonStyle(emphasis: .primary))
                }

                updatePreferencesPanel

                ViewThatFits(in: .horizontal) {
                    HStack(alignment: .top, spacing: ManagerMetrics.sectionSpacing) {
                        VStack(spacing: ManagerMetrics.sectionSpacing) {
                            startupPanel
                            advancedFlagsPanel
                        }
                            .frame(minWidth: 310, maxWidth: .infinity)
                        browserBehaviorPanel
                            .frame(minWidth: 320, maxWidth: .infinity)
                    }
                    VStack(spacing: ManagerMetrics.sectionSpacing) {
                        startupPanel
                        browserBehaviorPanel
                        advancedFlagsPanel
                    }
                }
            }
            .padding(ManagerMetrics.pagePadding)
        }
        .tint(Brand.orange)
    }

    private var advancedFlagsPanel: some View {
        GroupBox {
            VStack(alignment: .leading, spacing: 6) {
                TextEditor(text: $store.settings.additionalFlags)
                    .font(.system(size: 12, design: .monospaced))
                    .frame(height: 72)
                    .scrollContentBackground(.hidden)
                    .padding(6)
                    .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 7))
                    .overlay(RoundedRectangle(cornerRadius: 7).stroke(Color.primary.opacity(0.12)))
                Text("每行一个 --flag。实例隔离、调试端口、代理和插件由 YTray 管理。")
                    .font(.system(size: 11)).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(4)
        } label: {
            Label("Chrome 高级参数", systemImage: "terminal")
        }
    }

    private var updatePreferencesPanel: some View {
        GroupBox {
            HStack(spacing: 12) {
                Toggle("自动检查新版本", isOn: $updater.automaticallyChecks)
                    .toggleStyle(.checkbox)
                    .disabled(!updater.updatesEnabled)
                Spacer()
                Text("后台定期检查，不会自动下载或安装。版本与更新说明可在窗口顶部查看。")
                    .font(.caption).foregroundStyle(.secondary)
            }
            .padding(4)
        } label: {
            Label("应用更新", systemImage: "arrow.down.circle")
        }
    }

    private var startupPanel: some View {
        GroupBox {
            Grid(alignment: .leading, horizontalSpacing: 10, verticalSpacing: 8) {
                GridRow {
                    Text("默认运行时").foregroundStyle(.secondary)
                    ManagerPicker(title: "默认运行时", selectedTitle: selectedRuntimeTitle,
                                  selection: $store.settings.defaultRuntimeID) {
                        Text("未选择").tag(nil as UUID?)
                        ForEach(store.runtimes) {
                            Text("\($0.displayTitle) \($0.versionLabel) · \($0.source.title)")
                                .tag(Optional($0.id))
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
                GridRow {
                    Text("启动地址").foregroundStyle(.secondary)
                    TextField("chrome://newtab", text: $store.settings.homeURL)
                        .textFieldStyle(ManagerTextFieldStyle())
                }
                GridRow {
                    Text("调试端口").foregroundStyle(.secondary)
                    HStack(spacing: 8) {
                        TextField("9222", value: $store.settings.debugPort, format: .number)
                            .textFieldStyle(ManagerTextFieldStyle())
                            .frame(width: 80)
                        Text("占用时自动递增")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                }
            }
            .font(.callout)
            .padding(4)
        } label: {
            Label("默认启动", systemImage: "play.circle")
        }
    }

    private var selectedRuntimeTitle: String {
        guard let runtime = store.runtimes.first(where: { $0.id == store.settings.defaultRuntimeID }) else {
            return "未选择"
        }
        return "\(runtime.displayTitle) \(runtime.versionLabel)"
    }

    private var browserBehaviorPanel: some View {
        GroupBox {
            VStack(spacing: 0) {
                settingToggle(
                    "限制 WebRTC 本地 IP 暴露",
                    detail: "禁止非代理 UDP 连接",
                    isOn: $store.settings.restrictWebRTC
                )
                Divider()
                settingToggle(
                    "关闭浏览器通知",
                    detail: "减少测试过程中的系统提示",
                    isOn: $store.settings.disableNotifications
                )
                Divider()
                settingToggle(
                    "忽略证书错误",
                    detail: "适用于本地代理与网络调试",
                    isOn: $store.settings.ignoreCertificateErrors
                )
                Divider()
                settingToggle(
                    "测试模式（--test-type）",
                    detail: "隐藏命令行参数提示；会改变部分浏览器行为",
                    isOn: $store.settings.useTestType
                )
                if AppEnvironment.instanceColorThemesEnabled {
                    Divider()
                    settingToggle(
                        "使用 A/B/C 独立主题色",
                        detail: "仅影响之后创建的新实例",
                        isOn: $store.settings.colorizeBrowserInstances
                    )
                }
            }
            .padding(.horizontal, 8)
        } label: {
            Label("浏览器默认行为", systemImage: "switch.2")
        }
    }

    private func settingToggle(_ title: String, detail: String, isOn: Binding<Bool>) -> some View {
        ManagerToggleRow(title: title, detail: detail, isOn: isOn)
    }
}

struct InstancesPage: View {
    @ObservedObject var store: InstanceStore
    @State private var renameTarget: BrowserInstance?
    @State private var draftName = ""
    @State private var showClearHistoryConfirmation = false

    var body: some View {
        VStack(alignment: .leading, spacing: ManagerMetrics.sectionSpacing) {
            PageHeader(title: "运行与历史", subtitle: "运行中显示全部浏览器；停止后保存最后页面标题，并进入历史记录。")
            List {
                Section("运行中 · \(store.runningInstances.count)") {
                    ForEach(store.runningInstances) { instance in runningRow(instance) }
                    if store.runningInstances.isEmpty {
                        Text("暂无运行中的浏览器").foregroundStyle(.secondary)
                    }
                }
                Section {
                    ForEach(store.historyInstances) { instance in historyRow(instance) }
                    if store.historyInstances.isEmpty {
                        Text("暂无历史记录").foregroundStyle(.secondary)
                    }
                } header: {
                    HStack {
                        Text("历史 · \(store.historyInstances.count)")
                        Spacer()
                        if !store.historyInstances.isEmpty {
                            Button("清理全部", role: .destructive) {
                                showClearHistoryConfirmation = true
                            }
                            .buttonStyle(ManagerButtonStyle(emphasis: .destructive))
                        }
                    }
                }
            }
        }
        .padding(ManagerMetrics.pagePadding)
        .alert("修改历史名称", isPresented: renamePresented) {
            TextField("名称", text: $draftName)
            Button("取消", role: .cancel) { renameTarget = nil }
            Button("保存") {
                if let target = renameTarget { store.renameHistory(target, name: draftName) }
                renameTarget = nil
            }
            .disabled(draftName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
        } message: {
            Text(renameTarget?.lastPageTitle ?? "未记录页面标题")
        }
        .alert("清理全部历史？", isPresented: $showClearHistoryConfirmation) {
            Button("取消", role: .cancel) {}
            Button("清理全部", role: .destructive) { store.removeAllHistory() }
        } message: {
            Text("将删除全部历史记录；正在运行的浏览器不会受到影响。")
        }
    }

    private func runningRow(_ instance: BrowserInstance) -> some View {
        HStack(spacing: 12) {
            InstanceThumbnail(
                instance: instance,
                kind: runtimeKind(instance),
                width: 80,
                height: 50
            )
            VStack(alignment: .leading, spacing: 3) {
                HStack {
                    Text(instance.name).font(.headline)
                    StatusBadge(text: "运行中", color: .green)
                    StatusBadge(text: instance.mode.title, color: Brand.orange)
                }
                Text(instance.lastPageTitle ?? "正在读取页面标题")
                    .font(.callout).foregroundStyle(.secondary).lineLimit(1)
                Text("PID \(instance.processID) · Debug 127.0.0.1:\(instance.debugPort) · \(runtimeDescription(instance))")
                    .font(.caption).foregroundStyle(.tertiary)
            }
            Spacer()
            Button("截图") { Task { await store.capture(instance) } }.buttonStyle(ManagerButtonStyle())
            Button("目录") { store.revealProfile(instance) }.buttonStyle(ManagerButtonStyle())
            Button("停止", role: .destructive) { store.stop(instance) }.buttonStyle(ManagerButtonStyle(emphasis: .destructive))
        }.padding(.vertical, 6)
    }

    private func historyRow(_ instance: BrowserInstance) -> some View {
        HStack(spacing: 12) {
            InstanceThumbnail(
                instance: instance,
                kind: runtimeKind(instance),
                width: 72,
                height: 44
            )
            VStack(alignment: .leading, spacing: 3) {
                Text(instance.lastPageTitle ?? "未记录页面标题").font(.headline).lineLimit(1)
                Text("\(instance.name) · \(runtimeDescription(instance))")
                    .font(.caption).foregroundStyle(.secondary).lineLimit(1)
                Text(instance.startedAt.formatted(date: .abbreviated, time: .shortened))
                    .font(.caption2).foregroundStyle(.tertiary)
            }
            Spacer()
            Button {
                store.restoreHistory(instance)
            } label: {
                HStack(spacing: 4) {
                    if store.restoringInstanceID == instance.id && store.isLaunching {
                        ProgressView().controlSize(.small).scaleEffect(0.68)
                        Text("打开中…")
                    } else {
                        Image(systemName: "play.fill")
                        Text("打开")
                    }
                }
            }
            .buttonStyle(ManagerButtonStyle(emphasis: .primary))
            .disabled(store.isLaunching)
            Button("重命名") {
                draftName = instance.name
                renameTarget = instance
            }.buttonStyle(ManagerButtonStyle())
            Button("删除", role: .destructive) { store.removeHistory(instance) }
                .buttonStyle(ManagerButtonStyle(emphasis: .destructive))
        }.padding(.vertical, 6)
    }

    private var renamePresented: Binding<Bool> {
        Binding(
            get: { renameTarget != nil },
            set: { if !$0 { renameTarget = nil } }
        )
    }

    private func runtimeDescription(_ instance: BrowserInstance) -> String {
        if let runtime = store.runtime(for: instance) {
            return "\(runtime.displayTitle) \(runtime.versionLabel) · \(runtime.source.title)"
        }
        let kind = instance.runtimeKind ?? BrowserKind.infer(name: instance.runtimeName, path: "")
        return "\(kind.title) \(instance.runtimeVersion ?? "版本未知") · \((instance.runtimeSource ?? .local).title)"
    }

    private func runtimeKind(_ instance: BrowserInstance) -> BrowserKind {
        store.runtime(for: instance)?.kind
            ?? instance.runtimeKind
            ?? .infer(name: instance.runtimeName, path: "")
    }
}

struct PluginsPage: View {
    @ObservedObject var store: InstanceStore
    var body: some View {
        VStack(alignment: .leading, spacing: ManagerMetrics.sectionSpacing) {
            PageHeader(title: "插件管理", subtitle: "可分别设置默认加载和工具栏固定；修改将在下次启动实例时生效。")
            yakitExtensionSection
            HStack {
                Label("本地插件 · \(store.plugins.count)", systemImage: "puzzlepiece.extension")
                    .font(.callout.weight(.semibold))
                Spacer()
                Button("添加或扫描插件目录…") { choosePlugin() }
                    .buttonStyle(ManagerButtonStyle(emphasis: .primary))
            }
            Table(sortedPlugins) {
                TableColumn("插件") { plugin in
                    HStack(spacing: 10) {
                        PluginArtwork(plugin: plugin)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(plugin.name).font(.callout.weight(.semibold)).lineLimit(1)
                            Text("v\(plugin.version) · Manifest V\(plugin.manifestVersion)")
                                .font(.caption)
                                .foregroundStyle(.secondary)
                            Text(plugin.path)
                                .font(.caption2)
                                .foregroundStyle(.tertiary)
                                .lineLimit(1)
                        }
                    }
                    .padding(.vertical, 4)
                    .help(Text(plugin.path))
                }
                .width(min: 220, ideal: 250)
                TableColumn("默认加载") { plugin in
                    Toggle("", isOn: Binding(get: { plugin.enabled }, set: { enabled in
                        var changed = plugin
                        changed.enabled = enabled
                        store.updatePlugin(changed)
                    }))
                    .labelsHidden()
                    .toggleStyle(.switch)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .tint(Brand.orange)
                    .help(Text(plugin.enabled ? "关闭后，新实例不再加载此插件" : "开启后，新实例默认加载此插件"))
                }
                .width(88)
                TableColumn("固定到工具栏") { plugin in
                    Toggle("", isOn: Binding(get: { plugin.pinToToolbar == true }, set: { pinned in
                        var changed = plugin
                        changed.pinToToolbar = pinned
                        store.updatePlugin(changed)
                    }))
                    .labelsHidden()
                    .toggleStyle(.switch)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .tint(Brand.orange)
                    .help("固定到浏览器工具栏；下次启动实例生效")
                }
                .width(104)
                TableColumn("操作") { plugin in
                    if plugin.id != store.managedExtension?.id {
                        Button(role: .destructive) { store.removePlugin(plugin) } label: {
                            Image(systemName: "trash")
                        }
                        .buttonStyle(ManagerButtonStyle(emphasis: .destructive, iconOnly: true))
                        .help("移除此插件记录，不删除插件目录")
                        .accessibilityLabel(Text("移除插件 \(plugin.name)"))
                    } else {
                        Image(systemName: "lock.fill")
                            .foregroundStyle(.tertiary)
                            .help("内置插件不可移除")
                            .accessibilityLabel("内置插件不可移除")
                    }
                }
                .width(44)
            }
            .clipShape(RoundedRectangle(cornerRadius: 9))
            .overlay {
                RoundedRectangle(cornerRadius: 9).stroke(Color.primary.opacity(0.10))
                if store.plugins.isEmpty {
                    ContentUnavailableView("还没有插件", systemImage: "puzzlepiece.extension",
                                           description: Text("添加一个已解压的 Chrome 插件目录"))
                }
            }
        }.padding(ManagerMetrics.pagePadding)
        .task { if store.extensionManifest == nil { await store.refreshExtensionManifest() } }
    }

    private var sortedPlugins: [BrowserPlugin] {
        store.plugins.sorted { left, right in
            let managedID = store.managedExtension?.id
            let leftIsManaged = left.id == managedID
            let rightIsManaged = right.id == managedID
            if leftIsManaged != rightIsManaged { return leftIsManaged }
            return left.createdAt < right.createdAt
        }
    }

    private var latestEnterpriseVersion: ExtensionReleaseVersion? {
        store.extensionManifest?.versions.first { ExtensionInstaller.enterpriseArtifact(of: $0) != nil }
    }

    private var bundledExtensionVersion: String? {
        ExtensionInstaller.bundledVersion()
    }

    private var yakitExtensionSection: some View {
        let installed = store.managedExtension
        let latest = latestEnterpriseVersion
        let bundled = bundledExtensionVersion
        let busy = store.isInstallingExtension
        let needsInstall = installed == nil || store.isExtensionUpdateAvailable
        let buttonTitle = busy ? "安装中…"
            : installed == nil ? (latest.map { "下载 Yakit 插件 v\($0.version)" }
                ?? bundled.map { "安装内置版本 v\($0)" }
                ?? "下载 Yakit 插件")
            : store.isExtensionUpdateAvailable && latest != nil ? "更新到 v\(latest!.version)"
            : latest != nil ? "重新下载" : bundled != nil ? "重新安装内置版本" : "重新下载"
        return HStack(spacing: 12) {
            if busy {
                ProgressView().controlSize(.small).tint(Brand.orange)
            } else {
                Image(systemName: needsInstall ? "arrow.down.circle.fill" : "checkmark.circle.fill")
                    .foregroundStyle(needsInstall ? Brand.orange : Color.green)
                    .font(.title2)
            }
            VStack(alignment: .leading, spacing: 2) {
                if let installed, store.extensionManifest != nil, !store.isExtensionUpdateAvailable {
                    Text("Yakit 浏览器插件 v\(installed.version) 已是最新").font(.callout)
                } else if let installed, store.isExtensionUpdateAvailable {
                    Text("当前 v\(installed.version) · 最新 v\(latest?.version ?? store.extensionManifest?.latest ?? "")").font(.callout)
                } else if let latest {
                    Text("Yakit 浏览器插件 v\(latest.version) 可下载").font(.callout)
                } else if let bundled {
                    Text("安装包内置 Yakit 浏览器插件 v\(bundled)").font(.callout)
                } else {
                    Text("Yakit 浏览器插件").font(.callout)
                }
                if !store.extensionStatusMessage.isEmpty {
                    Text(store.extensionStatusMessage).font(.caption).foregroundStyle(.secondary)
                }
            }
            Spacer()
            Button("检查更新") { Task { await store.refreshExtensionManifest() } }
                .disabled(busy)
            Button(buttonTitle) { Task { await store.installExtension() } }
                .buttonStyle(ManagerButtonStyle(emphasis: .primary))
                .disabled(busy || latest == nil && bundled == nil)
        }
        .padding(.horizontal, 14)
        .frame(minHeight: 58)
        .background(Color.primary.opacity(0.035), in: RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(Color.primary.opacity(0.10)))
    }

    private func choosePlugin() {
        let panel = NSOpenPanel()
        panel.title = "选择插件目录或浏览器扩展根目录"
        panel.message = "可以多选；根目录下包含的有效 Chrome 插件会被自动识别。"
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = true
        if panel.runModal() == .OK { store.addPlugins(directories: panel.urls) }
    }
}

private struct PluginArtwork: View {
    let plugin: BrowserPlugin

    var body: some View {
        Group {
            if let image = PluginIconSource.image(for: plugin) {
                Image(nsImage: image)
                    .resizable()
                    .interpolation(.high)
                    .scaledToFit()
                    .padding(2)
            } else {
                Image(systemName: "puzzlepiece.extension.fill")
                    .resizable()
                    .scaledToFit()
                    .padding(7)
                    .foregroundStyle(Brand.orange)
            }
        }
        .frame(width: 34, height: 34)
        .background(Brand.orange.opacity(0.09), in: RoundedRectangle(cornerRadius: 8))
        .clipShape(RoundedRectangle(cornerRadius: 8))
    }
}
