import AppKit
import SwiftUI

struct ComputerControlBar: View {
    @ObservedObject var controller: SystemControlController
    let open: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: "desktopcomputer").font(.system(size: 17, weight: .medium))
                .foregroundStyle(controller.isActive ? Brand.orange : .secondary)
            Button(action: open) {
                VStack(alignment: .leading, spacing: 4) {
                    HStack(spacing: 6) {
                        Text("电脑控制").font(.system(size: 12, weight: .semibold))
                        if controller.isActive {
                            Text(controller.statusText).font(.system(size: 9, weight: .medium)).foregroundStyle(Brand.orange)
                        }
                    }
                    Text(controller.phase == .failed ? "启用失败，点击查看详情" : controller.isActive ? "控制当前桌面 · 可随时关闭" : "开启后允许 Agent 操作本机桌面")
                        .font(.system(size: 10)).foregroundStyle(controller.phase == .failed ? Color.red : .secondary)
                }.frame(maxWidth: .infinity, alignment: .leading)
            }.buttonStyle(.plain)
            ComputerControlSwitch(controller: controller)
        }
        .padding(.horizontal, 11).frame(height: 64)
        .background(Color(nsColor: .controlBackgroundColor))
        .clipShape(RoundedRectangle(cornerRadius: 9))
        .overlay(RoundedRectangle(cornerRadius: 9).stroke(controller.isActive ? Brand.orange.opacity(0.25) : Color.primary.opacity(0.07)))
    }
}

struct ComputerControlSwitch: View {
    @ObservedObject var controller: SystemControlController
    @State private var showPermissions = false
    @State private var showError = false

    var body: some View {
        Toggle("启用电脑控制", isOn: Binding(get: { controller.isActive }, set: { enabled in
            if !enabled { controller.stop(); return }
            controller.refreshHostPermissions()
            if !controller.hostPermissions.isGranted { showPermissions = true }
            else {
                Task {
                    let enabled = await controller.enableIfAuthorized()
                    if !enabled { showError = controller.phase == .failed }
                }
            }
        }))
        .toggleStyle(.switch).labelsHidden().controlSize(.small).tint(Brand.orange)
        .accessibilityLabel("启用电脑控制")
        .sheet(isPresented: $showPermissions) { ComputerControlPermissionView(controller: controller) }
        .alert("无法启用电脑控制", isPresented: $showError) {
            Button("知道了", role: .cancel) {}
        } message: { Text(controller.lastError) }
    }
}

struct ComputerControlPermissionView: View {
    @ObservedObject var controller: SystemControlController
    @Environment(\.dismiss) private var dismiss
    @State private var message = ""
    private let refresh = Timer.publish(every: 1, on: .main, in: .common).autoconnect()

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack(spacing: 12) {
                Image(systemName: "hand.raised.circle.fill").font(.system(size: 34)).foregroundStyle(Brand.orange)
                VStack(alignment: .leading, spacing: 4) {
                    Text("启用电脑控制").font(.title2.bold())
                    Text("请先为 \(AppEnvironment.displayName) 授予以下系统权限").font(.callout).foregroundStyle(.secondary)
                }
            }
            Text("授权后，Agent 可以读取当前桌面并操作应用。你可以随时关闭小组件中的开关，停止控制。")
                .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            VStack(spacing: 0) {
                permissionRow("辅助功能", detail: "读取应用控件、点击、输入与按键", symbol: "accessibility", granted: controller.hostPermissions.accessibility,
                              request: controller.requestAccessibilityPermission, pane: "Privacy_Accessibility")
                Divider().padding(.horizontal, 12)
                permissionRow("屏幕录制", detail: "读取窗口截图与桌面画面", symbol: "rectangle.inset.filled", granted: controller.hostPermissions.screenRecording,
                              request: controller.requestScreenRecordingPermission, pane: "Privacy_ScreenCapture")
            }
            .background(Color(nsColor: .controlBackgroundColor), in: RoundedRectangle(cornerRadius: 10))
            DisclosureGroup("当前授权的应用身份") {
                VStack(alignment: .leading, spacing: 6) {
                    Text(Bundle.main.bundleURL.path).textSelection(.enabled)
                    Text("Bundle ID：\(Bundle.main.bundleIdentifier ?? "未知")")
                    if let identity = AppSigningIdentity.current {
                        Text(identity.isAdHoc ? "临时签名：重建后旧授权可能失效" : "稳定签名 · Team \(identity.teamIdentifier ?? "未知")")
                        if let hash = identity.codeHash { Text("当前代码：\(hash.prefix(12))…").textSelection(.enabled) }
                    }
                    Button("在 Finder 中定位本应用") { NSWorkspace.shared.activateFileViewerSelecting([Bundle.main.bundleURL]) }
                }.font(.caption).padding(.top, 8)
            }.font(.caption)
            if AppSigningIdentity.current?.isAdHoc == true {
                Text("当前是临时签名开发包，重建后 macOS 可能拒绝旧授权。请使用固定签名的开发构建，再在权限列表中重新添加本应用一次。")
                    .font(.caption).foregroundStyle(.orange).fixedSize(horizontal: false, vertical: true)
            }
            Text(message.isEmpty ? "授权后点“检查并开启”；若系统提示需要重启，点“重启并应用权限”。重开后会回到电脑控制页面，控制会话保持关闭。" : message)
                .font(.caption).foregroundStyle(message.isEmpty ? Color.secondary : Color.red)
                .fixedSize(horizontal: false, vertical: true)
            HStack {
                Button("申请所需权限") { controller.requestPermissions() }
                ApplicationRestartButton().controlSize(.small)
                Spacer()
                Button("取消", role: .cancel) {
                    if controller.phase == .starting { controller.stop() }
                    dismiss()
                }.keyboardShortcut(.cancelAction)
                Button(controller.phase == .starting ? "正在开启…" : "检查并开启") {
                    Task {
                        if await controller.enableIfAuthorized() { dismiss() }
                        else { message = controller.hostPermissions.isGranted ? controller.lastError : "系统仍未认可当前进程的权限。如果刚授权，请先点“重启并应用权限”；重启后仍不可用时，再检查列表中的应用是否与当前签名身份匹配。" }
                    }
                }
                .buttonStyle(.borderedProminent).tint(Brand.orange)
                .disabled(controller.phase == .starting).keyboardShortcut(.defaultAction)
            }
        }
        .padding(24).frame(width: 480)
        .background(Color(nsColor: .windowBackgroundColor))
        .onAppear { controller.refreshHostPermissions() }
        .onReceive(refresh) { _ in controller.refreshHostPermissions() }
        .onDisappear { if controller.phase == .starting { controller.stop() } }
    }

    private func permissionRow(_ title: String, detail: String, symbol: String, granted: Bool, request: @escaping () -> Void, pane: String) -> some View {
        HStack(spacing: 12) {
            Image(systemName: symbol).font(.system(size: 20)).frame(width: 26).foregroundStyle(.secondary)
            VStack(alignment: .leading, spacing: 3) {
                Text(title).font(.system(size: 13, weight: .semibold))
                Text(detail).font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            if granted { Label("已授权", systemImage: "checkmark.circle.fill").font(.caption).foregroundStyle(.green) }
            else {
                Button("授权", action: request)
                Button("打开设置") { NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?\(pane)")!) }
            }
        }.padding(14)
    }
}

struct SystemControlPage: View {
    @ObservedObject var controller: SystemControlController
    @ObservedObject var mcp: YTrayMCPController
    @State private var windows: [[String: Any]] = []
    @State private var selectedWindow = ""
    @State private var observation = ""
    @State private var screenshot: NSImage?
    @State private var localError = ""
    @State private var copied = false
    @State private var showPermissions = false

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                PageHeader(title: "系统操作实例", subtitle: "使用 Swift 原生控制当前登录用户的真实桌面。")
                GroupBox {
                    VStack(alignment: .leading, spacing: 12) {
                        HStack {
                            Label(controller.statusText, systemImage: "desktopcomputer")
                                .font(.headline)
                            Spacer()
                            if controller.isActive {
                                Button("停止控制", role: .destructive) { controller.stop() }
                                    .buttonStyle(.borderedProminent).tint(.red)
                            } else {
                                Button("创建并连接会话") {
                                    controller.refreshHostPermissions()
                                    if controller.hostPermissions.isGranted { Task { _ = await controller.enableIfAuthorized() } }
                                    else { showPermissions = true }
                                }
                                    .buttonStyle(.borderedProminent).tint(Brand.orange)
                            }
                        }
                        Text("此实例共享本机的应用、屏幕、鼠标与键盘，不提供虚拟机隔离。开启后，持有临时令牌的 Agent 或授权网页可观察和操作桌面；30 分钟后自动结束。")
                            .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                        if let id = controller.sessionID {
                            Text("会话 \(id.uuidString)").font(.caption.monospaced()).textSelection(.enabled)
                        }
                        if let expiry = controller.expiresAt, controller.isActive {
                            Text("到期 \(expiry.formatted(date: .omitted, time: .standard)) · 停止即撤销令牌").font(.caption).foregroundStyle(.secondary)
                        }
                    }.padding(10)
                }
                GroupBox("接入与授权") {
                    VStack(alignment: .leading, spacing: 12) {
                        HStack {
                            Text("控制引擎").frame(width: 95, alignment: .leading)
                            Label("Swift 原生 · Apple AX / ScreenCaptureKit", systemImage: "swift")
                                .font(.callout)
                            Spacer()
                        }
                        Text("操作时以红色空心发光光标标出 AI 的作用位置。后台使用控件 AX 动作，通用按键和任意坐标点击需要授权前台输入。")
                            .font(.caption).foregroundStyle(.secondary)
                        TextField("允许的网页来源（留空仅接入本机 Agent）", text: $controller.allowedOriginDraft)
                            .textFieldStyle(.roundedBorder).disabled(controller.isActive)
                        Text("例如 https://example.com 或 http://localhost:3000，必须匹配协议、域名和端口。连接配置只在本机复制。")
                            .font(.caption).foregroundStyle(.secondary)
                        Toggle("允许显式前台输入（可能影响当前鼠标和键盘）", isOn: $controller.allowForegroundDraft)
                            .disabled(controller.isActive)
                        HStack {
                            Button("申请系统权限") { controller.requestPermissions() }
                            Button("检查权限") { inspect("check_permissions") }.disabled(controller.phase != .running || controller.busy)
                            ApplicationRestartButton()
                            Button("打开权限设置") {
                                NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")!)
                            }
                        }
                        Text("权限授予 YTray。修改辅助功能或屏幕录制权限后，请停止并重新连接；系统要求时重新打开 YTray。原生权限和操作结果显示在下方。")
                            .font(.caption).foregroundStyle(.secondary)
                        HStack {
                            Text("本地 MCP · \(mcp.statusText)").font(.caption).foregroundStyle(.secondary)
                            if mcp.phase != .running {
                                Button("开启 / 重试 MCP") { mcp.setEnabled(true); mcp.startIfEnabled() }
                            }
                            Spacer()
                            Button(copied ? "已复制" : "复制会话连接配置") {
                                guard let text = controller.configurationText(port: mcp.configuredPort) else { return }
                                NSPasteboard.general.clearContents()
                                NSPasteboard.general.setString(text, forType: .string)
                                copied = true
                            }.disabled(controller.phase != .running || mcp.phase != .running)
                        }
                    }.padding(10)
                }
                if !controller.lastError.isEmpty { Text(controller.lastError).font(.callout).foregroundStyle(.red).textSelection(.enabled) }
                if !localError.isEmpty { Text(localError).foregroundStyle(.red).textSelection(.enabled) }
                GroupBox("观察桌面") {
                    VStack(alignment: .leading, spacing: 12) {
                        HStack {
                            Button("发现应用") { inspect("list_apps") }
                            Button("刷新窗口") { inspect("list_windows") }
                            Button("整屏截图") { inspect("get_desktop_state") }
                            if controller.busy { ProgressView().controlSize(.small) }
                        }.disabled(controller.phase != .running || controller.busy)
                        if !windows.isEmpty {
                            Picker("窗口", selection: $selectedWindow) {
                                Text("选择窗口").tag("")
                                ForEach(Array(windows.enumerated()), id: \.offset) { index, window in
                                    Text("\(window["title"] as? String ?? "无标题") · PID \(window["pid"] ?? "?") · \(window["window_id"] ?? "?")")
                                        .tag(String(index))
                                }
                            }
                            Button("读取窗口 AX 树与截图") {
                                guard let index = Int(selectedWindow), windows.indices.contains(index) else { return }
                                let window = windows[index]
                                inspect("get_window_state", ["pid": window["pid"] ?? 0, "window_id": window["window_id"] ?? 0,
                                                             "tree_format": "elements", "include_screenshot": true, "include_accessibility_tree": true])
                            }.disabled(controller.phase != .running || controller.busy || selectedWindow.isEmpty)
                        }
                        if let screenshot {
                            Image(nsImage: screenshot).resizable().scaledToFit().frame(maxHeight: 360)
                                .accessibilityLabel("ScreenCaptureKit 返回的桌面或窗口截图")
                        }
                        if !observation.isEmpty {
                            Text(observation).font(.system(size: 11, design: .monospaced)).textSelection(.enabled)
                                .frame(maxWidth: .infinity, alignment: .leading)
                        } else {
                            Text("连接后读取真实窗口、权限和截图。Agent 可按元素 token 点击、输入、按键与滚动，再读取窗口状态验证结果。")
                                .font(.callout).foregroundStyle(.secondary)
                        }
                    }.padding(10).frame(maxWidth: .infinity, alignment: .leading)
                }
            }.padding(24)
        }
        .sheet(isPresented: $showPermissions) { ComputerControlPermissionView(controller: controller) }
        .onChange(of: controller.sessionID) { _, _ in copied = false; windows = []; selectedWindow = ""; observation = ""; screenshot = nil }
        .onChange(of: controller.phase) { _, phase in
            if phase == .stopped || phase == .failed { screenshot = nil; observation = ""; windows = [] }
        }
    }

    private func inspect(_ name: String, _ args: [String: Any] = [:]) {
        Task {
            do {
                localError = ""
                let result = try await controller.perform(name, arguments: args)
                observation = SystemControlController.text(result)
                if let structured = result["structuredContent"] as? [String: Any] {
                    if name == "list_windows" {
                        windows = structured["windows"] as? [[String: Any]] ?? []
                        selectedWindow = ""
                    }
                    let data = try JSONSerialization.data(withJSONObject: structured, options: [.prettyPrinted, .sortedKeys])
                    observation = String(decoding: data, as: UTF8.self)
                }
                let images = result["content"] as? [[String: Any]] ?? []
                screenshot = images.first(where: { $0["type"] as? String == "image" })
                    .flatMap { $0["data"] as? String }.flatMap { Data(base64Encoded: $0) }.flatMap(NSImage.init(data:))
            } catch { localError = error.localizedDescription }
        }
    }
}
