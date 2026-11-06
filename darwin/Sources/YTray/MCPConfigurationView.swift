import AppKit
import SwiftUI

struct MCPToolDescription: Identifiable {
    let name: String
    let title: String
    let summary: String
    let schema: String?
    var id: String { name }

    static let descriptions: [String: (String, String)] = [
        "list_instances": ("浏览器实例", "列出用户在 YTray 中打开的浏览器及其调试端口。"),
        "get_cdp_info": ("调试连接", "读取浏览器版本、标签页和 CDP WebSocket 地址。"),
        "get_page_content": ("页面内容", "读取当前页面的标题、地址、可见文本和可选 HTML。"),
        "evaluate_javascript": ("执行 JavaScript", "在当前浏览器页面执行 JavaScript 并返回结果。"),
        "list_apps": ("发现应用", "发现当前桌面上的应用和进程。"),
        "list_windows": ("发现窗口", "列出应用窗口，用 PID 与窗口 ID 确定操作目标。"),
        "get_window_state": ("窗口状态", "读取指定窗口的控件树、元素 token 与截图。"),
        "get_desktop_state": ("桌面截图", "读取当前桌面的画面与实际捕获能力反馈。"),
        "click": ("点击控件", "通过元素 token 或明确坐标点击目标窗口。"),
        "type_text": ("输入文字", "向明确指定的窗口或桌面目标输入文字。"),
        "press_key": ("键盘操作", "向指定目标发送按键与组合键。"),
        "scroll": ("滚动", "在指定窗口或桌面坐标滚动。"),
        "check_permissions": ("权限检查", "读取 macOS 实际报告的系统权限状态。"),
        "system_status": ("控制状态", "查看当前桌面会话、到期时间和权限状态。"),
        "end_session": ("结束控制", "停止当前电脑控制会话，撤销临时接入令牌。"),
    ]

    static func from(_ definition: [String: Any]) -> Self? {
        guard let name = definition["name"] as? String else { return nil }
        let description = descriptions[name]
        let schema = (definition["inputSchema"] as? [String: Any]).flatMap {
            try? JSONSerialization.data(withJSONObject: $0, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
        }.map { String(decoding: $0, as: UTF8.self) }
        return Self(name: name, title: description?.0 ?? name,
                    summary: description?.1 ?? definition["description"] as? String ?? "", schema: schema)
    }

    @MainActor static var computerCapabilities: [Self] {
        (SystemControlController.fields.keys.sorted() + ["system_status", "end_session"]).map { name in
            Self(name: name, title: descriptions[name]?.0 ?? name, summary: descriptions[name]?.1 ?? "", schema: nil)
        }
    }
}

struct MCPConfigurationView: View {
    enum Connection: String, CaseIterable { case browser = "浏览器"; case computer = "电脑控制" }
    @ObservedObject var controller: YTrayMCPController
    @ObservedObject var computer: SystemControlController
    let openComputerSettings: () -> Void
    var initialSelection: Connection = .browser
    @State private var selection: Connection = .browser
    @State private var selectedTool = ""
    @State private var copied = false

    private var tools: [MCPToolDescription] {
        if selection == .browser { return controller.toolDefinitions.compactMap(MCPToolDescription.from) }
        if computer.phase == .running { return computer.mcpToolDefinitions.compactMap(MCPToolDescription.from) }
        return MCPToolDescription.computerCapabilities
    }
    private var currentTool: MCPToolDescription? { tools.first { $0.name == selectedTool } ?? tools.first }
    private var configuration: String? {
        selection == .browser ? controller.configurationJSON : computer.configurationJSON(port: controller.configuredPort)
    }
    private var endpoint: String {
        selection == .browser ? controller.endpoint : "http://127.0.0.1:\(controller.configuredPort)/system/mcp"
    }
    private var canCopy: Bool { controller.phase == .running && configuration != nil }

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack(spacing: 12) {
                Image(systemName: "point.3.connected.trianglepath.dotted")
                    .font(.system(size: 25, weight: .medium)).foregroundStyle(Brand.orange)
                    .frame(width: 46, height: 46)
                    .background(Brand.orange.opacity(0.1), in: RoundedRectangle(cornerRadius: 11))
                VStack(alignment: .leading, spacing: 4) {
                    Text("MCP 接入与函数").font(.system(size: 22, weight: .bold))
                    Text("查看可调用能力，启动本地服务并复制客户端配置。")
                        .font(.callout).foregroundStyle(.secondary)
                }
                Spacer()
            }
            serviceControl
            HStack {
                Picker("接入类型", selection: $selection) {
                    ForEach(Connection.allCases, id: \.self) { Text($0.rawValue).tag($0) }
                }.pickerStyle(.segmented).frame(width: 240)
                Spacer()
                if selection == .computer {
                    ComputerControlSwitch(controller: computer)
                    Text("电脑控制").font(.caption).foregroundStyle(.secondary)
                }
            }
            HStack(alignment: .top, spacing: 16) {
                functionList.frame(width: 268)
                configurationPanel.frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            }.frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .padding(24).frame(minWidth: 820, minHeight: 640)
        .background(Color(nsColor: .windowBackgroundColor))
        .tint(Brand.orange)
        .onChange(of: selection) { _, _ in selectedTool = ""; copied = false }
        .onChange(of: computer.sessionID) { _, _ in copied = false }
        .onChange(of: computer.phase) { _, _ in copied = false }
        .onChange(of: controller.configuredPort) { _, _ in copied = false }
        .onAppear { selection = computer.isActive ? .computer : initialSelection }
    }

    private var serviceControl: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 8) {
                Circle().fill(Color(nsColor: controller.statusColor)).frame(width: 7, height: 7)
                Text("MCP 服务器").font(.system(size: 13, weight: .semibold))
                Text(controller.statusText).font(.caption).foregroundStyle(.secondary).lineLimit(1).help(controller.statusText)
                Spacer()
                Toggle("启用服务", isOn: Binding(get: { controller.isEnabled }, set: { controller.setEnabled($0) }))
                    .toggleStyle(.switch).controlSize(.small).font(.caption)
                if controller.phase != .running {
                    Button("启动服务") {
                        if !controller.isEnabled { controller.setEnabled(true) }
                        else { controller.startIfEnabled() }
                    }.buttonStyle(.borderedProminent)
                }
            }
            HStack(spacing: 8) {
                Text("本机地址").font(.caption).foregroundStyle(.secondary)
                Text(YTrayMCPController.host).font(.system(size: 12, design: .monospaced))
                Text(":").foregroundStyle(.secondary)
                TextField("端口", text: $controller.draftPort).textFieldStyle(.roundedBorder)
                    .font(.system(size: 12, design: .monospaced)).frame(width: 76)
                    .onSubmit { controller.applyPort() }
                Button("应用端口") { controller.applyPort() }.controlSize(.small)
                Spacer()
                Text("Streamable HTTP · 仅本机监听").font(.caption).foregroundStyle(.secondary)
            }
            if let message = controller.validationMessage { Text(message).font(.caption).foregroundStyle(.red) }
        }
        .padding(14).background(Color(nsColor: .controlBackgroundColor), in: RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(Color.primary.opacity(0.07)))
    }

    private var functionList: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text("MCP 函数").font(.system(size: 13, weight: .semibold))
                Spacer()
                Text("\(tools.count)").font(.caption.monospacedDigit()).foregroundStyle(.secondary)
            }
            if selection == .computer && computer.phase != .running {
                Text("以下能力在开启电脑控制后可用").font(.caption).foregroundStyle(.secondary)
            }
            ScrollView {
                LazyVStack(spacing: 4) {
                    ForEach(tools) { tool in
                        Button { selectedTool = tool.name } label: {
                            HStack(spacing: 8) {
                                VStack(alignment: .leading, spacing: 4) {
                                    Text(tool.name).font(.system(size: 11, weight: .medium, design: .monospaced))
                                    Text(tool.title).font(.caption).foregroundStyle(.secondary)
                                }
                                Spacer(minLength: 0)
                                if currentTool?.name == tool.name { Image(systemName: "chevron.right").font(.system(size: 9, weight: .semibold)).foregroundStyle(Brand.orange) }
                            }
                            .padding(.horizontal, 10).padding(.vertical, 9)
                            .background(currentTool?.name == tool.name ? Brand.orange.opacity(0.12) : Color.clear,
                                        in: RoundedRectangle(cornerRadius: 7))
                            .contentShape(Rectangle())
                        }.buttonStyle(.plain)
                    }
                }
            }
        }
        .padding(12).frame(maxHeight: .infinity)
        .background(Color(nsColor: .controlBackgroundColor), in: RoundedRectangle(cornerRadius: 10))
    }

    private var configurationPanel: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                HStack {
                    Text("连接配置").font(.system(size: 14, weight: .semibold))
                    Spacer()
                    Button {
                        guard canCopy, let configuration else { return }
                        NSPasteboard.general.clearContents()
                        NSPasteboard.general.setString(configuration, forType: .string)
                        copied = true
                    } label: { Label(copied ? "已复制" : "复制配置", systemImage: copied ? "checkmark" : "doc.on.doc") }
                    .buttonStyle(.borderedProminent).disabled(!canCopy)
                }
                Text(endpoint).font(.system(size: 11, design: .monospaced)).textSelection(.enabled)
                    .foregroundStyle(.secondary)
                if let configuration {
                    ScrollView(.horizontal) {
                        Text(configuration).font(.system(size: 11, design: .monospaced)).textSelection(.enabled)
                            .fixedSize(horizontal: true, vertical: true).padding(12)
                    }
                    .frame(height: CGFloat(configuration.components(separatedBy: "\n").count) * 14 + 24)
                    .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 8))
                    Text(controller.phase == .running ? "将 JSON 添加到 MCP 客户端。配置包含高权限令牌，请只提供给可信客户端。" : "服务器尚未运行，启动服务后即可复制并连接。")
                        .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                } else {
                    VStack(alignment: .leading, spacing: 10) {
                        Label("电脑控制尚未开启", systemImage: "desktopcomputer").font(.headline)
                        Text("开启电脑控制并授予系统权限后，这里会生成临时会话配置。关闭控制后令牌立即失效。")
                            .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                        Button("电脑控制设置…", action: openComputerSettings)
                    }.padding(14).frame(maxWidth: .infinity, alignment: .leading)
                        .background(Brand.orange.opacity(0.07), in: RoundedRectangle(cornerRadius: 8))
                }
                if selection == .computer {
                    Text("授权网页：\(computer.allowedOrigin ?? "仅本机 Agent，未开放网页来源")")
                        .font(.caption).foregroundStyle(.secondary)
                    if let expiry = computer.expiresAt, computer.isActive {
                        Text("会话到期：\(expiry.formatted(date: .omitted, time: .standard))").font(.caption).foregroundStyle(.secondary)
                    }
                    if !computer.lastError.isEmpty { Text(computer.lastError).font(.caption).foregroundStyle(.red).textSelection(.enabled) }
                }
                if let tool = currentTool {
                    Divider()
                    Text(tool.title).font(.system(size: 13, weight: .semibold))
                    Text(tool.summary).font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    DisclosureGroup("函数参数 · \(tool.name)") {
                        Text(tool.schema ?? "开启电脑控制后，从 Swift 原生后端加载参数定义。")
                            .font(.system(size: 10, design: .monospaced)).textSelection(.enabled)
                            .frame(maxWidth: .infinity, alignment: .leading).padding(.top, 8)
                    }.font(.caption)
                }
            }.frame(maxWidth: .infinity, alignment: .leading)
        }
    }
}
