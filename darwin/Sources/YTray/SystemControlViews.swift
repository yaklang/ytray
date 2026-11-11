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
    @State private var showTools = false

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                PageHeader(title: "电脑控制", subtitle: "让 AI 操作应用，每一步都看得见。")
                VStack(alignment: .leading, spacing: 16) {
                    HStack(spacing: 12) {
                        Image(systemName: "desktopcomputer").font(.system(size: 26)).foregroundStyle(Brand.orange)
                        VStack(alignment: .leading, spacing: 4) {
                            Text(controller.statusText).font(.headline)
                            Text(controller.isActive ? (controller.busy ? "正在操作，请留意屏幕提示" : "等待 AI 连接 · 可随时停止") : "开启后复制配置，连接你的 AI 客户端")
                                .font(.callout).foregroundStyle(.secondary)
                        }
                        Spacer()
                        if controller.isActive {
                            Button("停止控制", role: .destructive) { controller.stop() }
                                .buttonStyle(.borderedProminent).tint(Brand.orange)
                        } else {
                            Button("开启控制") {
                                controller.refreshHostPermissions()
                                if controller.hostPermissions.isGranted { Task { _ = await controller.enableIfAuthorized() } }
                                else { showPermissions = true }
                            }.buttonStyle(.borderedProminent).tint(Brand.orange)
                        }
                    }
                    if !controller.isActive {
                        Toggle("允许切换应用与使用鼠标、键盘", isOn: $controller.allowForegroundDraft)
                            .font(.callout).tint(Brand.orange)
                    }
                    Divider()
                    HStack(spacing: 12) {
                        Button(copied ? "已复制" : "复制 MCP 配置") {
                            guard let text = controller.configurationJSON(port: mcp.configuredPort) else { return }
                            NSPasteboard.general.clearContents()
                            NSPasteboard.general.setString(text, forType: .string)
                            copied = true
                        }.disabled(controller.phase != .running || mcp.phase != .running)
                        Button("查看 MCP 工具") { showTools = true }
                        Spacer()
                        if mcp.phase != .running {
                            Button("启动接入服务") { mcp.setEnabled(true); mcp.startIfEnabled() }
                        } else {
                            Label("本地服务已就绪", systemImage: "checkmark.circle.fill")
                                .font(.caption).foregroundStyle(.secondary)
                        }
                    }
                }
                .padding(18).background(Color(nsColor: .controlBackgroundColor), in: RoundedRectangle(cornerRadius: 12))
                VStack(alignment: .leading, spacing: 12) {
                    Text("正在做什么").font(.headline)
                    if let activity = controller.activity {
                        activityRow(activity)
                    } else {
                        HStack(spacing: 12) {
                            Image(systemName: "paperplane").font(.title2).foregroundStyle(Brand.orange)
                            VStack(alignment: .leading, spacing: 4) {
                                Text("操作时，屏幕边缘会亮起陶土红氛围灯。")
                                Text("飞镖光标标出位置，顶部显示动作与结果。输入内容只显示字符数。")
                                    .font(.caption).foregroundStyle(.secondary)
                            }
                        }.padding(.vertical, 5)
                    }
                    if controller.recentActivity.count > 1 {
                        DisclosureGroup("最近操作") {
                            VStack(spacing: 12) {
                                ForEach(controller.recentActivity.filter { $0.id != controller.activity?.id }) { activityRow($0) }
                            }.padding(.top, 12)
                        }.font(.caption)
                    }
                }.padding(18).frame(maxWidth: .infinity, alignment: .leading)
                    .background(Color(nsColor: .controlBackgroundColor), in: RoundedRectangle(cornerRadius: 12))
                if !controller.lastError.isEmpty { Text(controller.lastError).font(.callout).foregroundStyle(.red).textSelection(.enabled) }
                if !localError.isEmpty { Text(localError).foregroundStyle(.red).textSelection(.enabled) }
                DisclosureGroup("权限与连接设置") {
                    VStack(alignment: .leading, spacing: 12) {
                        HStack {
                            Label(controller.hostPermissions.accessibility ? "辅助功能已授权" : "需要辅助功能", systemImage: controller.hostPermissions.accessibility ? "checkmark.circle" : "exclamationmark.circle")
                            Label(controller.hostPermissions.screenRecording ? "屏幕录制已授权" : "需要屏幕录制", systemImage: controller.hostPermissions.screenRecording ? "checkmark.circle" : "exclamationmark.circle")
                        }.font(.caption).foregroundStyle(.secondary)
                        HStack {
                            Button("管理权限") { showPermissions = true }
                            ApplicationRestartButton()
                            Button("刷新权限") { controller.refreshHostPermissions() }
                        }
                        TextField("允许的网页来源（留空只接入本机 AI）", text: $controller.allowedOriginDraft)
                            .textFieldStyle(.roundedBorder).disabled(controller.isActive)
                        Text("网页需填写完整来源，例如 https://example.com。修改接入方式需先停止控制。")
                            .font(.caption).foregroundStyle(.secondary)
                        if let expiry = controller.expiresAt, controller.isActive {
                            Text("本次控制到 \(expiry.formatted(date: .omitted, time: .shortened)) 自动结束；停止后配置立即失效。")
                                .font(.caption).foregroundStyle(.secondary)
                        }
                    }.padding(.top, 12)
                }
                DisclosureGroup("查看 AI 看到的画面与控件") {
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
                            Text("连接后读取真实窗口、权限和截图。Agent 按控件编号点击、输入、按键与滚动，动作后会自动返回新状态。")
                                .font(.callout).foregroundStyle(.secondary)
                        }
                    }.padding(10).frame(maxWidth: .infinity, alignment: .leading)
                }
                Text("控制当前真实桌面。可读取应用、截图、点击、输入与滚动；前台操作需本机授权。")
                    .font(.caption).foregroundStyle(.secondary)
            }.padding(24)
        }
        .background(Color(nsColor: .windowBackgroundColor))
        .onAppear { controller.refreshHostPermissions() }
        .sheet(isPresented: $showPermissions) { ComputerControlPermissionView(controller: controller) }
        .sheet(isPresented: $showTools) {
            VStack(alignment: .trailing, spacing: 0) {
                Button("完成") { showTools = false }.keyboardShortcut(.cancelAction).padding([.top, .trailing], 16)
                MCPConfigurationView(controller: mcp, computer: controller,
                    openComputerSettings: { showTools = false }, initialSelection: .computer)
            }.frame(width: 860, height: 720)
        }
        .onChange(of: controller.sessionID) { _, _ in copied = false; windows = []; selectedWindow = ""; observation = ""; screenshot = nil }
        .onChange(of: controller.phase) { _, phase in
            if phase == .stopped || phase == .failed { screenshot = nil; observation = ""; windows = [] }
        }
    }

    private func activityRow(_ feedback: DesktopOperationFeedback) -> some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: feedback.phase == .completed ? "checkmark.circle.fill" : feedback.phase.isWorking ? "paperplane.fill" : "exclamationmark.circle.fill")
                .foregroundStyle(Brand.orange).font(.system(size: 18)).frame(width: 24)
            VStack(alignment: .leading, spacing: 4) {
                Text(feedback.summary).font(.callout.weight(.medium))
                Text(feedback.note.isEmpty ? feedback.context : feedback.note).font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            Text(feedback.phase.label).font(.caption).foregroundStyle(.secondary)
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
