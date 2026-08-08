import AppKit
import SwiftUI

enum ManagerSection: String, CaseIterable, Identifiable {
    case quick = "启动新实例"
    case runtimes = "浏览器运行时"
    case settings = "启动设置"
    case instances = "运行与历史"
    case plugins = "插件管理"
    var id: String { rawValue }
    var icon: String {
        switch self {
        case .quick: return "bolt.fill"
        case .runtimes: return "shippingbox"
        case .settings: return "slider.horizontal.3"
        case .instances: return "rectangle.stack"
        case .plugins: return "puzzlepiece.extension"
        }
    }
}

@MainActor
final class ManagerNavigation: ObservableObject {
    @Published var selection: ManagerSection? = .quick
}

struct ManagerView: View {
    @ObservedObject var store: InstanceStore
    @ObservedObject var navigation: ManagerNavigation
    @State private var showWizard = false

    var body: some View {
        NavigationSplitView {
            List(ManagerSection.allCases, selection: $navigation.selection) { section in
                Label(section.rawValue, systemImage: section.icon).tag(section)
            }
            .navigationTitle("Instance Dock")
            .tint(Brand.orange)
        } detail: {
            Group {
                switch navigation.selection ?? .quick {
                case .quick: QuickLaunchPage(store: store, showWizard: $showWizard)
                case .runtimes: RuntimePage(store: store)
                case .settings: SettingsPage(store: store)
                case .instances: InstancesPage(store: store)
                case .plugins: PluginsPage(store: store)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(Color(nsColor: .windowBackgroundColor))
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
        .alert("Instance Dock", isPresented: Binding(
            get: { store.errorMessage != nil },
            set: { if !$0 { store.errorMessage = nil } }
        )) { Button("知道了") { store.errorMessage = nil } } message: { Text(store.errorMessage ?? "") }
    }
}

struct PageHeader: View {
    let title: String
    let subtitle: String
    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            Text(title).font(.system(size: 25, weight: .bold))
            Text(subtitle).font(.callout).foregroundStyle(.secondary)
        }.frame(maxWidth: .infinity, alignment: .leading)
    }
}

struct QuickLaunchPage: View {
    @ObservedObject var store: InstanceStore
    @Binding var showWizard: Bool
    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 24) {
                PageHeader(title: "启动新实例", subtitle: "每次启动都创建独立用户目录，实例之间不会共用 Cookie、缓存或登录态。")
                HStack(spacing: 16) {
                    LaunchModeCard(icon: "plus.circle.fill", title: "启动新实例", detail: "使用当前默认参数、代理和插件配置创建独立实例",
                                   button: "启动新实例", isLoading: store.isLaunching && store.launchingMode == .quick,
                                   disabled: store.isLaunching) { store.launch(mode: .quick) }
                    LaunchModeCard(icon: "wand.and.stars", title: "自定义启动", detail: "通过步骤向导配置本次运行，不覆盖默认设置",
                                   button: "开始配置", isLoading: false,
                                   disabled: store.isLaunching) { showWizard = true }
                }
                GroupBox("当前默认配置") {
                    Grid(alignment: .leading, horizontalSpacing: 28, verticalSpacing: 12) {
                        GridRow { Text("运行时").foregroundStyle(.secondary); Text(defaultRuntimeName) }
                        GridRow { Text("启动地址").foregroundStyle(.secondary); Text(store.settings.homeURL) }
                        GridRow { Text("代理").foregroundStyle(.secondary); Text(store.settings.proxyServer.isEmpty ? "直连" : store.settings.proxyServer) }
                        GridRow { Text("调试端口").foregroundStyle(.secondary); Text("127.0.0.1:\(store.settings.debugPort) 起自动避让") }
                        GridRow { Text("插件").foregroundStyle(.secondary); Text("\(store.settings.defaultPluginIDs.count) 个") }
                    }.padding(8).frame(maxWidth: .infinity, alignment: .leading)
                }
            }.padding(28)
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
        VStack(alignment: .leading, spacing: 13) {
            Image(systemName: icon).font(.system(size: 24)).foregroundStyle(Brand.orange)
            Text(title).font(.headline)
            Text(detail).font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            Spacer()
            Button(action: action) {
                LaunchActionLabel(title: button, systemImage: icon, isLoading: isLoading)
            }
            .buttonStyle(FilledOrangeButtonStyle())
            .disabled(disabled)
            .frame(maxWidth: .infinity, alignment: .leading)
        }.padding(18).frame(maxWidth: .infinity, minHeight: 190, alignment: .leading)
        .background(Brand.orange.opacity(0.10)).clipShape(RoundedRectangle(cornerRadius: 14))
        .overlay(RoundedRectangle(cornerRadius: 14).stroke(Brand.orange.opacity(0.35)))
    }
}

struct RuntimePage: View {
    @ObservedObject var store: InstanceStore
    @State private var selectedVersion = ""
    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            PageHeader(title: "浏览器来源", subtitle: "系统浏览器会自动识别并可直接使用；安装 Chrome for Testing 只是可选项。")
            HStack(spacing: 8) {
                Button("重新扫描本机") { store.refreshSystemBrowsers() }
                    .buttonStyle(SmallSecondaryButtonStyle())
                Button("选择其他本地浏览器…") { chooseRuntime() }
                    .buttonStyle(SmallSecondaryButtonStyle())
                Divider().frame(height: 24)
                Picker("镜像版本", selection: $selectedVersion) {
                    Text(store.availableVersions.isEmpty ? "请先刷新" : "选择版本").tag("")
                    ForEach(store.availableVersions) { Text($0.version).tag($0.version) }
                }.frame(width: 260).controlSize(.small)
                Button("刷新可安装版本") { Task { await store.refreshManifest() } }
                    .buttonStyle(SmallSecondaryButtonStyle())
                Button {
                    guard let version = store.availableVersions.first(where: { $0.version == selectedVersion }) else { return }
                    Task { await store.install(version: version) }
                } label: {
                    HStack(spacing: 5) {
                        if store.isInstalling { ProgressView().controlSize(.small).scaleEffect(0.72) }
                        Text(store.isInstalling ? "安装中…" : "安装")
                    }
                }
                .buttonStyle(FilledOrangeButtonStyle())
                .disabled(selectedVersion.isEmpty || store.isInstalling)
                Spacer()
            }
            if store.isInstalling { ProgressView(store.activityMessage).controlSize(.small) }
            List {
                ForEach(store.runtimes) { runtime in
                    HStack(spacing: 12) {
                        Image(systemName: runtime.kind.symbol)
                            .foregroundStyle(Brand.orange).frame(width: 26)
                        VStack(alignment: .leading, spacing: 3) {
                            HStack {
                                Text(runtime.displayTitle).font(.headline)
                                StatusBadge(text: runtime.source.title, color: runtime.isSystemEnvironment ? .green : Brand.orange)
                            }
                            Text("版本 \(runtime.versionLabel) · \(runtime.architecture)").font(.caption).foregroundStyle(.secondary)
                            Text(runtime.executablePath).font(.caption2).foregroundStyle(.tertiary).lineLimit(1)
                        }
                        Spacer()
                        if store.settings.defaultRuntimeID == runtime.id { StatusBadge(text: "默认", color: .green) }
                        Button("设为默认") { store.settings.defaultRuntimeID = runtime.id; store.saveSettings() }
                            .buttonStyle(SmallSecondaryButtonStyle())
                        if runtime.source != .system {
                            Button(role: .destructive) { store.removeRuntime(runtime) } label: { Image(systemName: "trash") }
                                .buttonStyle(IconButtonStyle())
                        }
                    }.padding(.vertical, 5)
                }
            }.overlay { if store.runtimes.isEmpty { ContentUnavailableView("未发现浏览器", systemImage: "globe", description: Text("选择本地浏览器，或安装一个 Chrome for Testing 版本")) } }
        }.padding(28).task { if store.availableVersions.isEmpty { await store.refreshManifest() } }
    }

    private func chooseRuntime() {
        let panel = NSOpenPanel()
        panel.title = "选择 Chrome.app 或 Chrome 可执行文件"
        panel.canChooseDirectories = true
        panel.canChooseFiles = true
        panel.allowsMultipleSelection = false
        if panel.runModal() == .OK, let url = panel.url { store.addLocalRuntime(selectedURL: url) }
    }
}

struct SettingsPage: View {
    @ObservedObject var store: InstanceStore
    var body: some View {
        Form {
            Section {
                Picker("默认运行时", selection: $store.settings.defaultRuntimeID) {
                    Text("未选择").tag(nil as UUID?)
                    ForEach(store.runtimes) {
                        Text("\($0.displayTitle) \($0.versionLabel) · \($0.source.title)").tag(Optional($0.id))
                    }
                }
                TextField("启动地址", text: $store.settings.homeURL)
                TextField("代理服务器，例如 http://127.0.0.1:8080", text: $store.settings.proxyServer)
                TextField("调试端口", value: $store.settings.debugPort, format: .number)
            } header: { Text("启动") }
            Section {
                Toggle("限制 WebRTC 非代理 UDP 与本地 IP 暴露", isOn: $store.settings.restrictWebRTC)
                Toggle("关闭浏览器通知", isOn: $store.settings.disableNotifications)
                Toggle("忽略证书错误（默认关闭）", isOn: $store.settings.ignoreCertificateErrors)
            } header: { Text("浏览器精简与网络") }
            Section {
                TextEditor(text: $store.settings.additionalFlags).font(.system(.body, design: .monospaced)).frame(minHeight: 130)
                Text("每行一个 --flag。实例隔离、调试端口和插件参数由 Instance Dock 管理，不能在这里覆盖。")
                    .font(.caption).foregroundStyle(.secondary)
            } header: { Text("附加参数") }
            Section {
                ForEach(store.plugins) { plugin in
                    Toggle(plugin.name, isOn: Binding(
                        get: { store.settings.defaultPluginIDs.contains(plugin.id) },
                        set: { enabled in
                            if enabled { store.settings.defaultPluginIDs.append(plugin.id) }
                            else { store.settings.defaultPluginIDs.removeAll { $0 == plugin.id } }
                        }
                    ))
                }
                if store.plugins.isEmpty { Text("请先在“插件管理”添加已解压插件目录").foregroundStyle(.secondary) }
            } header: { Text("默认加载插件") }
            Button("保存默认设置") { store.saveSettings() }.buttonStyle(FilledOrangeButtonStyle())
        }.formStyle(.grouped).navigationTitle("启动设置")
    }
}

struct InstancesPage: View {
    @ObservedObject var store: InstanceStore
    @State private var renameTarget: BrowserInstance?
    @State private var draftName = ""
    @State private var showClearHistoryConfirmation = false

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
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
                            .buttonStyle(.borderless)
                        }
                    }
                }
            }
        }
        .padding(28)
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
            Image(systemName: "play.circle.fill").foregroundStyle(Brand.orange).font(.title2)
                .overlay(alignment: .bottomTrailing) {
                    if let badge = instance.dockBadge { DockBadgeMark(label: badge, size: 15) }
                }
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
            Button("截图") { Task { await store.capture(instance) } }.buttonStyle(SmallSecondaryButtonStyle())
            Button("目录") { store.revealProfile(instance) }.buttonStyle(SmallSecondaryButtonStyle())
            Button("停止", role: .destructive) { store.stop(instance) }.buttonStyle(SmallDangerButtonStyle())
        }.padding(.vertical, 6)
    }

    private func historyRow(_ instance: BrowserInstance) -> some View {
        HStack(spacing: 12) {
            Image(systemName: "clock.arrow.circlepath").foregroundStyle(.secondary).font(.title2)
                .overlay(alignment: .bottomTrailing) {
                    if let badge = instance.dockBadge { DockBadgeMark(label: badge, size: 15) }
                }
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
            .buttonStyle(SmallOrangeButtonStyle())
            .disabled(store.isLaunching)
            Button("重命名") {
                draftName = instance.name
                renameTarget = instance
            }.buttonStyle(SmallSecondaryButtonStyle())
            Button("删除", role: .destructive) { store.removeHistory(instance) }
                .buttonStyle(SmallDangerButtonStyle())
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
}

struct PluginsPage: View {
    @ObservedObject var store: InstanceStore
    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            PageHeader(title: "插件管理", subtitle: "Chrome 命令行只能加载已解压插件目录；请选择根部包含 manifest.json 的文件夹。")
            HStack { Button("添加本地插件目录…") { choosePlugin() }.buttonStyle(FilledOrangeButtonStyle()); Spacer() }
            List {
                ForEach(store.plugins) { plugin in
                    HStack(spacing: 12) {
                        Image(systemName: "puzzlepiece.extension.fill").foregroundStyle(Brand.orange).font(.title2)
                        VStack(alignment: .leading, spacing: 3) {
                            Text(plugin.name).font(.headline)
                            Text("v\(plugin.version) · Manifest V\(plugin.manifestVersion)").font(.caption).foregroundStyle(.secondary)
                            Text(plugin.path).font(.caption2).foregroundStyle(.tertiary).lineLimit(1)
                        }
                        Spacer()
                        Toggle("可用", isOn: Binding(get: { plugin.enabled }, set: { enabled in
                            var changed = plugin; changed.enabled = enabled; store.updatePlugin(changed)
                        })).toggleStyle(.switch)
                        Button(role: .destructive) { store.removePlugin(plugin) } label: { Image(systemName: "trash") }
                            .buttonStyle(HistoryDeleteButtonStyle())
                    }.padding(.vertical, 6)
                }
            }.overlay { if store.plugins.isEmpty { ContentUnavailableView("还没有插件", systemImage: "puzzlepiece.extension", description: Text("添加一个已解压的 Chrome 插件目录")) } }
        }.padding(28)
    }

    private func choosePlugin() {
        let panel = NSOpenPanel()
        panel.title = "选择已解压 Chrome 插件目录"
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        if panel.runModal() == .OK, let url = panel.url { store.addPlugin(directory: url) }
    }
}
