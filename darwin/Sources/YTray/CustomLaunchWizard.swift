import AppKit
import SwiftUI

struct CustomLaunchWizard: View {
    @ObservedObject var store: InstanceStore
    @Binding var isPresented: Bool
    @State private var step = 0
    @State private var draft: LaunchSettings
    @State private var pluginIDs: Set<UUID>
    @State private var rememberBrowser = true
    @State private var usePresetProxy = false

    init(store: InstanceStore, isPresented: Binding<Bool>, initialStep: Int = 0) {
        self._step = State(initialValue: min(max(initialStep, 0), 3))
        self.store = store
        self._isPresented = isPresented
        var settings = store.settings
        settings.proxyServer = ""
        self._draft = State(initialValue: settings)
        self._pluginIDs = State(initialValue: Set(store.settings.defaultPluginIDs))
    }

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                VStack(alignment: .leading, spacing: 3) {
                    Text("自定义启动").font(.title2.bold())
                    Text("本次参数独立生效；浏览器选择可在确认页记住").font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                Button { isPresented = false } label: { Image(systemName: "xmark") }
                    .buttonStyle(ManagerButtonStyle(iconOnly: true))
                    .help("关闭自定义启动")
                    .accessibilityLabel("关闭自定义启动")
            }.padding(16)
            stepHeader.padding(.horizontal, 16).padding(.bottom, 12)
            Divider()
            Group {
                switch step {
                case 0: ScrollView { runtimeStep }
                case 1: networkStep
                case 2: pluginStep
                default: reviewStep
                }
            }.frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading).padding(16)
            Divider()
            HStack {
                Button("取消") { isPresented = false }.buttonStyle(ManagerButtonStyle())
                Spacer()
                if step > 0 { Button("上一步") { step -= 1 }.buttonStyle(ManagerButtonStyle()) }
                if step < 3 {
                    Button("下一步") { step += 1 }.buttonStyle(ManagerButtonStyle(emphasis: .primary)).disabled(step == 0 && draft.defaultRuntimeID == nil)
                } else {
                    Button {
                        var settings = draft
                        applySelectedNetwork(to: &settings)
                        let selectedPluginIDs = effectivePluginIDs
                        settings.defaultPluginIDs = selectedPluginIDs
                        if rememberBrowser {
                            store.settings.defaultRuntimeID = settings.defaultRuntimeID
                            store.saveSettings()
                        }
                        store.launch(
                            mode: .custom,
                            customSettings: settings,
                            customPluginIDs: selectedPluginIDs,
                            launchUsesProxy: usePresetProxy
                        )
                    } label: {
                        LaunchActionLabel(title: "启动实例", systemImage: "play.fill",
                                          isLoading: store.isLaunching && store.launchingMode == .custom)
                    }
                    .buttonStyle(ManagerButtonStyle(emphasis: .primary))
                    .disabled(store.isLaunching)
                }
            }.padding(16)
        }
        .frame(width: 760, height: 580)
        .buttonStyle(ManagerButtonStyle())
        .controlSize(.regular)
        // The wizard is normally hosted by a SwiftUI sheet, whose presentation
        // surface supplies the background. It is also rendered directly for the
        // website and design-review artifacts, so the root view must own an
        // opaque surface instead of inheriting transparency from its host.
        .background(Color(nsColor: .windowBackgroundColor))
        .tint(Brand.orange)
        .onChange(of: store.launchPhase) { _, phase in
            if phase == .succeeded { isPresented = false }
        }
    }

    private var stepHeader: some View {
        HStack(spacing: 0) {
            stepItem(0, "运行时", "选择浏览器")
            connector(after: 0)
            stepItem(1, "网络与参数", "代理与调试")
            connector(after: 1)
            stepItem(2, "插件", "本地扩展")
            connector(after: 2)
            stepItem(3, "确认", "启动实例")
        }
    }

    private func stepItem(_ index: Int, _ title: String, _ subtitle: String) -> some View {
        HStack(spacing: 8) {
            ZStack {
                Circle().fill(index <= step ? Brand.orange : Color.secondary.opacity(0.18)).frame(width: 28, height: 28)
                if index < step { Image(systemName: "checkmark").font(.caption.bold()).foregroundStyle(.white) }
                else { Text("\(index + 1)").font(.caption.bold()).foregroundStyle(index == step ? .white : .secondary) }
            }
            VStack(alignment: .leading, spacing: 1) {
                Text(title).font(.caption.bold()).foregroundStyle(index <= step ? .primary : .secondary)
                Text(subtitle).font(.system(size: 11)).foregroundStyle(.tertiary)
            }
        }
    }

    private func connector(after index: Int) -> some View {
        Rectangle().fill(index < step ? Brand.orange : Color.secondary.opacity(0.2)).frame(height: 1).padding(.horizontal, 8)
    }

    private var runtimeStep: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("选择本次使用的浏览器").font(.headline)
            if store.runtimes.isEmpty {
                ContentUnavailableView("没有可用浏览器", systemImage: "globe", description: Text("请在“浏览器来源”中选择本地浏览器或安装新版本"))
            } else {
                ForEach(store.runtimes) { runtime in
                    Button {
                        draft.defaultRuntimeID = runtime.id
                    } label: {
                        HStack(spacing: 13) {
                            Image(systemName: runtime.kind.symbol)
                                .foregroundStyle(Brand.orange).font(.title2)
                            VStack(alignment: .leading) {
                                HStack {
                                    Text(runtime.displayTitle).font(.headline)
                                    StatusBadge(text: runtime.source.title, color: runtime.isSystemEnvironment ? .green : Brand.orange)
                                }
                                Text("版本 \(runtime.versionLabel) · \(runtime.architecture)").font(.caption).foregroundStyle(.secondary)
                            }
                            Spacer()
                            Image(systemName: draft.defaultRuntimeID == runtime.id ? "checkmark.circle.fill" : "circle")
                                .foregroundStyle(draft.defaultRuntimeID == runtime.id ? Brand.orange : .secondary)
                        }.padding(10)
                    }.buttonStyle(PlainHoverButtonStyle(cornerRadius: 11))
                    // Keep the selected row adaptive. A fixed pale background makes
                    // macOS dark-mode primary text (white) effectively disappear.
                    .background(draft.defaultRuntimeID == runtime.id
                                ? Brand.orange.opacity(0.16)
                                : Color(nsColor: .controlBackgroundColor))
                    .clipShape(RoundedRectangle(cornerRadius: 11))
                    .overlay(RoundedRectangle(cornerRadius: 11).stroke(draft.defaultRuntimeID == runtime.id ? Brand.orange : Color.secondary.opacity(0.2)))
                }
            }
            profileRootChoice
        }
    }

    private var profileRootChoice: some View {
        HStack(spacing: 10) {
            VStack(alignment: .leading, spacing: 3) {
                Text("本次实例数据").font(.callout.weight(.semibold))
                Text(store.resolveProfileRoot(draft).path)
                    .font(.caption.monospaced())
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .textSelection(.enabled)
                    .help(Text(store.resolveProfileRoot(draft).path))
                Text("YTray 会在该父目录下创建独立子目录")
                    .font(.caption2).foregroundStyle(.tertiary)
            }
            Spacer()
            if draft.profileRootPath != store.settings.profileRootPath {
                Button("使用默认") { draft.profileRootPath = store.settings.profileRootPath }
                    .buttonStyle(ManagerButtonStyle())
            }
            Button("选择目录…") { chooseProfileRoot() }
                .buttonStyle(ManagerButtonStyle())
        }
        .padding(10)
        .background(Color.primary.opacity(0.035), in: RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(Color.primary.opacity(0.10)))
    }

    private var networkStep: some View {
        ScrollView {
            HStack(alignment: .top, spacing: 16) {
                VStack(alignment: .leading, spacing: 10) {
                    Grid(alignment: .leading, horizontalSpacing: 10, verticalSpacing: 8) {
                        GridRow {
                            Text("网络模式")
                            ManagerPicker(title: "网络模式",
                                          selectedTitle: usePresetProxy ? "HTTP 代理" : "无代理（直连）",
                                          selection: $usePresetProxy) {
                                Text("无代理（直连）").tag(false)
                                Text("HTTP 代理 · \(presetProxyAddress)").tag(true)
                            }
                        }
                        GridRow {
                            Text("启动地址")
                            TextField("chrome://newtab", text: $draft.homeURL)
                        }
                        GridRow {
                            Text("调试端口")
                            TextField("9222", value: $draft.debugPort, format: .number)
                        }
                        GridRow {
                            Text("Dock 角标")
                            TextField("自动分配，也可填 1–2 个字母", text: $draft.dockBadge)
                        }
                    }
                    .font(.system(size: 13))
                    .textFieldStyle(ManagerTextFieldStyle())
                    Text("附加参数 · 每行一个 --flag").font(.system(size: 12)).foregroundStyle(.secondary)
                    TextEditor(text: $draft.additionalFlags)
                        .font(.system(size: 12, design: .monospaced))
                        .frame(height: 72)
                        .padding(6)
                        .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 7))
                        .overlay(RoundedRectangle(cornerRadius: 7).stroke(Color.primary.opacity(0.16)))
                }
                .frame(maxWidth: .infinity)
                VStack(alignment: .leading, spacing: 0) {
                    ManagerToggleRow(title: "限制 WebRTC", detail: "禁止非代理 UDP 与本地 IP 暴露", isOn: $draft.restrictWebRTC)
                    Divider()
                    ManagerToggleRow(title: "关闭通知", detail: "减少浏览器系统提示", isOn: $draft.disableNotifications)
                    Divider()
                    ManagerToggleRow(title: "忽略证书错误", detail: "用于本地代理与网络调试", isOn: $draft.ignoreCertificateErrors)
                    Divider()
                    ManagerToggleRow(title: "测试模式", detail: "添加 --test-type 启动参数", isOn: $draft.useTestType)
                    Label(usePresetProxy ? "使用预设代理 \(presetProxyAddress)" : "忽略系统代理，以直连方式启动",
                          systemImage: usePresetProxy ? "network" : "network.slash")
                        .font(.system(size: 12)).foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                        .padding(.top, 12)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
        }
    }

    private var pluginStep: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("选择本次加载的本地插件").font(.headline)
            if let runtime = selectedRuntime, !BrowserLauncher.supportsCommandLineExtensions(runtimeKind: runtime.kind) {
                Label {
                    Text("\(runtime.displayTitle) 将在启动时加载并验证插件；加载失败时可选择不加载插件继续启动。")
                } icon: {
                    Image(systemName: "exclamationmark.triangle.fill")
                }
                .font(.caption)
                .foregroundStyle(.secondary)
                .padding(12)
                .background(Color.secondary.opacity(0.08))
                .clipShape(RoundedRectangle(cornerRadius: 9))
            } else {
                Text("仅列出已验证 manifest.json 的已解压目录。").font(.caption).foregroundStyle(.secondary)
            }
            if store.plugins.isEmpty {
                ContentUnavailableView("没有本地插件", systemImage: "puzzlepiece.extension", description: Text("本次实例将不加载扩展"))
            } else {
                List(store.plugins.filter(\.enabled)) { plugin in
                    Toggle(isOn: Binding(
                        get: { selectedRuntimeSupportsPlugins && pluginIDs.contains(plugin.id) },
                        set: { enabled in
                            if enabled { pluginIDs.insert(plugin.id) } else { pluginIDs.remove(plugin.id) }
                        }
                    )) {
                        VStack(alignment: .leading) {
                            Text(plugin.name).font(.headline)
                            Text("v\(plugin.version) · Manifest V\(plugin.manifestVersion)").font(.caption).foregroundStyle(.secondary)
                        }
                    }
                    .disabled(!selectedRuntimeSupportsPlugins)
                }
            }
        }
    }

    private var reviewStep: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("确认本次启动配置").font(.headline)
            Grid(alignment: .leading, horizontalSpacing: 20, verticalSpacing: 8) {
                reviewRow("浏览器", selectedRuntimeDescription)
                reviewRow("启动地址", draft.homeURL)
                reviewRow("网络", usePresetProxy ? "HTTP 代理 · \(presetProxyAddress)" : "直连（无代理）")
                reviewRow("调试", "127.0.0.1:\(draft.debugPort) 起自动避让")
                reviewRow("Dock 角标", draft.dockBadge.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                          ? "自动分配" : draft.dockBadge.uppercased())
                reviewRow("WebRTC", draft.restrictWebRTC ? "限制非代理 UDP/IP 暴露" : "不限制")
                reviewRow("插件", "\(pluginIDs.count) 个（启动时验证）")
                reviewRow("实例数据", store.resolveProfileRoot(draft).path)
            }.padding(12).frame(maxWidth: .infinity, alignment: .leading)
            .background(Brand.orange.opacity(0.10)).clipShape(RoundedRectangle(cornerRadius: 13))
            Toggle("记住此浏览器，作为下次快速启动的默认选择", isOn: $rememberBrowser)
                .toggleStyle(.checkbox)
            Label("无论使用哪种模式，YTray 都会创建独立用户目录，并把调试服务限制在本机回环地址。",
                  systemImage: "lock.shield").font(.caption).foregroundStyle(.secondary)
        }
    }

    private func reviewRow(_ key: String, _ value: String) -> some View {
        GridRow { Text(key).foregroundStyle(.secondary); Text(value).textSelection(.enabled) }
    }

    private var selectedRuntimeDescription: String {
        guard let runtime = store.runtimes.first(where: { $0.id == draft.defaultRuntimeID }) else { return "未选择" }
        return "\(runtime.displayTitle) \(runtime.versionLabel) · \(runtime.source.title)"
    }

    private var selectedRuntime: BrowserRuntime? {
        store.runtimes.first(where: { $0.id == draft.defaultRuntimeID })
    }

    private var selectedRuntimeSupportsPlugins: Bool {
        selectedRuntime != nil
    }

    private var effectivePluginIDs: [UUID] {
        selectedRuntimeSupportsPlugins ? Array(pluginIDs) : []
    }

    private var presetProxyAddress: String {
        let value = draft.presetProxyServer.isEmpty
            ? LaunchSettings.defaultPresetProxyServer
            : draft.presetProxyServer
        return value.replacingOccurrences(of: "http://", with: "")
            .replacingOccurrences(of: "https://", with: "")
    }

    private func applySelectedNetwork(to settings: inout LaunchSettings) {
        if usePresetProxy {
            settings.proxyServer = (try? HTTPProxyAddress.build(
                scheme: settings.presetProxyScheme,
                host: settings.presetProxyHost,
                port: settings.presetProxyPort
            )) ?? settings.presetProxyServer
            settings.proxyUsername = settings.presetProxyUsername
            settings.proxyPassword = settings.presetProxyPassword
        } else {
            settings.proxyServer = ""
            settings.proxyUsername = ""
            settings.proxyPassword = ""
        }
    }

    private func chooseProfileRoot() {
        let panel = NSOpenPanel()
        panel.title = "选择本次浏览器实例数据的父目录"
        panel.message = "YTray 会为本次实例创建独立子目录。"
        panel.prompt = "选择"
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.allowsMultipleSelection = false
        let current = store.resolveProfileRoot(draft)
        panel.directoryURL = FileManager.default.fileExists(atPath: current.path)
            ? current : current.deletingLastPathComponent()
        if panel.runModal() == .OK, let url = panel.url,
           let path = store.prepareProfileRoot(url) {
            draft.profileRootPath = path
        }
    }
}
