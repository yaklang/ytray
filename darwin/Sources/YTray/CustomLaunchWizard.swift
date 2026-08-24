import SwiftUI

struct CustomLaunchWizard: View {
    @ObservedObject var store: InstanceStore
    @Binding var isPresented: Bool
    @State private var step = 0
    @State private var draft: LaunchSettings
    @State private var pluginIDs: Set<UUID>
    @State private var rememberBrowser = true

    init(store: InstanceStore, isPresented: Binding<Bool>) {
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
                Button { isPresented = false } label: { Image(systemName: "xmark.circle.fill").font(.title2) }
                    .buttonStyle(PlainHoverButtonStyle(cornerRadius: 8)).foregroundStyle(.secondary)
            }.padding(22)
            stepHeader.padding(.horizontal, 28).padding(.bottom, 18)
            Divider()
            Group {
                switch step {
                case 0: runtimeStep
                case 1: networkStep
                case 2: pluginStep
                default: reviewStep
                }
            }.frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading).padding(28)
            Divider()
            HStack {
                Button("取消") { isPresented = false }.buttonStyle(SmallSecondaryButtonStyle())
                Spacer()
                if step > 0 { Button("上一步") { step -= 1 }.buttonStyle(SmallSecondaryButtonStyle()) }
                if step < 3 {
                    Button("下一步") { step += 1 }.buttonStyle(FilledOrangeButtonStyle()).disabled(step == 0 && draft.defaultRuntimeID == nil)
                } else {
                    Button {
                        var settings = draft
                        settings.defaultPluginIDs = Array(pluginIDs)
                        if rememberBrowser {
                            store.settings.defaultRuntimeID = settings.defaultRuntimeID
                            store.saveSettings()
                        }
                        store.launch(
                            mode: .custom,
                            customSettings: settings,
                            customPluginIDs: Array(pluginIDs),
                            launchUsesProxy: false
                        )
                    } label: {
                        LaunchActionLabel(title: "启动实例", systemImage: "play.fill",
                                          isLoading: store.isLaunching && store.launchingMode == .custom)
                    }
                    .buttonStyle(FilledOrangeButtonStyle())
                    .disabled(store.isLaunching)
                }
            }.padding(20)
        }
        .frame(width: 720, height: 570)
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
            stepItem(1, "启动参数", "调试与参数")
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
                Text(subtitle).font(.system(size: 9)).foregroundStyle(.tertiary)
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
                        }.padding(14)
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
        }
    }

    private var networkStep: some View {
        Form {
            TextField("启动地址", text: $draft.homeURL)
            TextField("调试端口", value: $draft.debugPort, format: .number)
            TextField("Dock 角标（留空自动分配 A/B/C…，可填 1–2 个字母）", text: $draft.dockBadge)
            Toggle("限制 WebRTC 非代理 UDP 与本地 IP 暴露", isOn: $draft.restrictWebRTC)
            Toggle("关闭通知", isOn: $draft.disableNotifications)
            Toggle("忽略证书错误", isOn: $draft.ignoreCertificateErrors)
            VStack(alignment: .leading) {
                Text("附加参数（每行一个）").font(.caption).foregroundStyle(.secondary)
                TextEditor(text: $draft.additionalFlags).font(.system(.body, design: .monospaced)).frame(height: 90)
                    .overlay(RoundedRectangle(cornerRadius: 6).stroke(Color.secondary.opacity(0.2)))
            }
            Label("自定义启动使用直连；HTTP 代理请在托盘的“预设代理”中选择。", systemImage: "network")
                .font(.caption)
                .foregroundStyle(.secondary)
        }.formStyle(.grouped)
    }

    private var pluginStep: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("选择本次加载的本地插件").font(.headline)
            Text("仅列出已验证 manifest.json 的已解压目录。").font(.caption).foregroundStyle(.secondary)
            if store.plugins.isEmpty {
                ContentUnavailableView("没有本地插件", systemImage: "puzzlepiece.extension", description: Text("本次实例将不加载扩展"))
            } else {
                List(store.plugins.filter(\.enabled)) { plugin in
                    Toggle(isOn: Binding(
                        get: { pluginIDs.contains(plugin.id) },
                        set: { enabled in
                            if enabled { pluginIDs.insert(plugin.id) } else { pluginIDs.remove(plugin.id) }
                        }
                    )) {
                        VStack(alignment: .leading) {
                            Text(plugin.name).font(.headline)
                            Text("v\(plugin.version) · Manifest V\(plugin.manifestVersion)").font(.caption).foregroundStyle(.secondary)
                        }
                    }
                }
            }
        }
    }

    private var reviewStep: some View {
        VStack(alignment: .leading, spacing: 18) {
            Text("确认本次启动配置").font(.headline)
            Grid(alignment: .leading, horizontalSpacing: 30, verticalSpacing: 13) {
                reviewRow("浏览器", selectedRuntimeDescription)
                reviewRow("启动地址", draft.homeURL)
                reviewRow("网络", "直连（无代理）")
                reviewRow("调试", "127.0.0.1:\(draft.debugPort) 起自动避让")
                reviewRow("Dock 角标", draft.dockBadge.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                          ? "自动分配" : draft.dockBadge.uppercased())
                reviewRow("WebRTC", draft.restrictWebRTC ? "限制非代理 UDP/IP 暴露" : "不限制")
                reviewRow("插件", "\(pluginIDs.count) 个")
            }.padding(18).frame(maxWidth: .infinity, alignment: .leading)
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
}
