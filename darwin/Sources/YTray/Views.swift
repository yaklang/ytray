import AppKit
import SwiftUI

struct VisualEffect: NSViewRepresentable {
    func makeNSView(context: Context) -> NSVisualEffectView {
        let view = NSVisualEffectView()
        view.material = .popover
        view.blendingMode = .behindWindow
        view.state = .active
        return view
    }
    func updateNSView(_ nsView: NSVisualEffectView, context: Context) {}
}

enum WidgetMetrics {
    static let width: CGFloat = 390

    static let collapsedProxyHeight: CGFloat = 136
    static let expandedProxyHeight: CGFloat = 212

    static func height(
        runningCount: Int,
        historyCount: Int,
        proxyAdvancedExpanded: Bool = false
    ) -> CGFloat {
        let proxyHeight = proxyAdvancedExpanded ? expandedProxyHeight : collapsedProxyHeight
        return 372 + (proxyHeight - collapsedProxyHeight)
            + runningListHeight(runningCount) + historyListHeight(historyCount)
    }

    static func runningListHeight(_ count: Int) -> CGFloat {
        count == 0 ? 62 : CGFloat(min(count, 4) * 68)
    }

    static func historyListHeight(_ count: Int) -> CGFloat {
        guard count > 0 else { return 34 }
        return CGFloat(min(count, 4) * 54 + (count > 4 ? 20 : 0))
    }
}

struct WidgetView: View {
    @ObservedObject var store: InstanceStore
    @ObservedObject var presentation: WidgetPresentationState
    let openManager: (ManagerSection) -> Void
    let closeWidget: () -> Void
    @State private var newInstanceHovered = false
    @State private var showClearHistoryConfirmation = false

    var body: some View {
        ZStack {
            VisualEffect()
            Color(nsColor: .windowBackgroundColor).opacity(0.72)
            VStack(spacing: 9) {
                header
                ProxyPresetControl(store: store)
                groupTitle("运行中", count: store.runningInstances.count)
                runningSection

                historyGroupTitle
                historySection

                HStack(spacing: 6) {
                    Spacer()
                    Button { store.launchConfigured(usePresetProxy: false) } label: {
                        LaunchActionLabel(
                            title: "无代理启动",
                            systemImage: "network.slash",
                            isLoading: store.isLaunching && store.launchingUsesProxy == false
                        )
                    }
                        .buttonStyle(SmallSecondaryButtonStyle())
                        .disabled(store.isLaunching)
                    Button { store.launchConfigured(usePresetProxy: true) } label: {
                        LaunchActionLabel(
                            title: "使用HTTP代理启动",
                            systemImage: "network",
                            isLoading: store.isLaunching && store.launchingUsesProxy == true
                        )
                    }
                        .buttonStyle(SmallOrangeButtonStyle())
                        .disabled(store.isLaunching)
                }

                Button { openManager(.quick) } label: {
                    HStack(spacing: 8) {
                        Image(systemName: "gearshape")
                        Text("全部管理").fontWeight(.medium)
                        Spacer()
                        Image(systemName: "chevron.right").font(.caption)
                    }
                    .font(.system(size: 12))
                    .padding(.horizontal, 11)
                    .frame(height: 32)
                }
                .buttonStyle(PlainHoverButtonStyle(cornerRadius: 8))
                .background(Color(nsColor: .controlBackgroundColor))
                .clipShape(RoundedRectangle(cornerRadius: 8))
                .overlay(RoundedRectangle(cornerRadius: 8).stroke(Color.primary.opacity(0.08)))
            }
            .padding(14)
        }
        .clipShape(RoundedRectangle(cornerRadius: 18))
        .frame(width: WidgetMetrics.width, height: preferredHeight)
        .alert("YTray", isPresented: errorPresented) {
            Button("知道了") { store.errorMessage = nil }
        } message: { Text(store.errorMessage ?? "") }
        .alert("清理全部历史？", isPresented: $showClearHistoryConfirmation) {
            Button("取消", role: .cancel) {}
            Button("清理全部", role: .destructive) { store.removeAllHistory() }
        } message: {
            Text("将删除全部历史记录；正在运行的浏览器不会受到影响。")
        }
    }

    private var preferredHeight: CGFloat {
        WidgetMetrics.height(
            runningCount: store.runningInstances.count,
            historyCount: store.historyInstances.count,
            proxyAdvancedExpanded: store.isProxyAdvancedExpanded
        )
    }

    private var header: some View {
        HStack(spacing: 8) {
            VStack(alignment: .leading, spacing: 1) {
                Text("浏览器实例").font(.system(size: 18, weight: .bold))
                if store.launchPhase != .idle {
                    LaunchStatusLine(phase: store.launchPhase, message: store.launchMessage)
                } else {
                    Text(store.defaultRuntime.map { "默认 · \($0.displayTitle) \($0.versionLabel)" } ?? "未选择默认浏览器")
                        .font(.system(size: 10.5, weight: .medium))
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
            }
            Spacer()
            newInstanceMenu
            Button {
                presentation.isPinned.toggle()
            } label: {
                Image(systemName: isEffectivelyPinned ? "pin.fill" : "pin")
                    .font(.system(size: 11, weight: .semibold))
                    .frame(width: 24, height: 24)
            }
            .buttonStyle(PlainHoverButtonStyle(cornerRadius: 6))
            .foregroundStyle(isEffectivelyPinned ? Brand.orange : .secondary)
            .disabled(store.proxyCheckPhase == .checking)
            .help(store.proxyCheckPhase == .checking
                  ? "代理检测期间已临时固定"
                  : presentation.isPinned ? "取消固定；失焦后自动隐藏" : "固定小组件")
            .accessibilityLabel(isEffectivelyPinned ? "取消固定小组件" : "固定小组件")
            Button(action: closeWidget) {
                Image(systemName: "xmark").font(.system(size: 11, weight: .semibold)).frame(width: 24, height: 24)
            }
            .buttonStyle(PlainHoverButtonStyle(cornerRadius: 6))
            .foregroundStyle(.secondary)
        }
    }

    private var isEffectivelyPinned: Bool {
        presentation.isPinned || store.proxyCheckPhase == .checking
    }

    private var newInstanceMenu: some View {
        Menu {
            if let runtime = store.defaultRuntime {
                Section("当前选择") {
                    Label("\(runtime.displayTitle) \(runtime.versionLabel)", systemImage: "checkmark.circle.fill")
                }
            }
            if !store.managedRuntimes.isEmpty {
                Section("已安装版本") {
                    ForEach(store.managedRuntimes) { runtime in
                        Button { store.selectDefaultRuntime(runtime) } label: {
                            runtimeMenuLabel(runtime)
                        }
                    }
                }
            }
            if !store.localBrowserRuntimes.isEmpty {
                Section("本机浏览器") {
                    ForEach(store.localBrowserRuntimes) { runtime in
                        Button { store.selectDefaultRuntime(runtime) } label: {
                            runtimeMenuLabel(runtime)
                        }
                    }
                }
            }
            Divider()
            Button { chooseLocalBrowser() } label: {
                Label("选择其他本地浏览器…", systemImage: "folder")
            }
            Button { openManager(.settings) } label: {
                Label("更多启动设置…", systemImage: "slider.horizontal.3")
            }
            Button { openManager(.runtimes) } label: {
                Label("安装新版本…", systemImage: "arrow.down.circle")
            }
        } label: {
            HStack(spacing: 5) {
                if store.isLaunching {
                    ProgressView().controlSize(.small).scaleEffect(0.72)
                    Text("正在启动")
                } else {
                    Image(systemName: "slider.horizontal.3")
                    Text("快速配置")
                    Image(systemName: "chevron.down").font(.system(size: 8, weight: .bold))
                }
            }
            .font(.system(size: 12, weight: .semibold))
            .foregroundStyle(Brand.orange)
            .padding(.horizontal, 9)
            .frame(height: 28)
            .background(newInstanceHovered ? Brand.orange.opacity(0.12) : Color(nsColor: .controlBackgroundColor))
            .clipShape(RoundedRectangle(cornerRadius: 7))
            .overlay(RoundedRectangle(cornerRadius: 7).stroke(Brand.orange.opacity(newInstanceHovered ? 0.55 : 0.24)))
            .shadow(color: Brand.orange.opacity(newInstanceHovered ? 0.2 : 0), radius: 4, y: 1)
            .animation(.easeOut(duration: 0.12), value: newInstanceHovered)
            .onHover { newInstanceHovered = $0 }
        }
        .menuStyle(.borderlessButton)
        .fixedSize()
        .disabled(store.isLaunching)
    }

    @ViewBuilder private var runningSection: some View {
        if store.runningInstances.isEmpty {
            CompactEmptyState(
                runtime: store.defaultRuntime,
                chooseBrowser: { chooseLocalBrowser() }
            )
        } else {
            ScrollView(.vertical) {
                LazyVStack(spacing: 0) {
                    ForEach(store.runningInstances) { instance in
                        RunningInstanceRow(store: store, instance: instance)
                    }
                }
            }
            .frame(height: WidgetMetrics.runningListHeight(store.runningInstances.count))
            .background(Color(nsColor: .controlBackgroundColor))
            .clipShape(RoundedRectangle(cornerRadius: 9))
            .overlay(RoundedRectangle(cornerRadius: 9).stroke(Brand.orange.opacity(0.24)))
        }
    }

    @ViewBuilder private var historySection: some View {
        VStack(spacing: 0) {
            ForEach(store.historyInstances.prefix(4)) { instance in
                HistoryInstanceRow(store: store, instance: instance)
            }
            if store.historyInstances.isEmpty {
                Text("暂无历史记录")
                    .font(.system(size: 11, weight: .medium))
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, minHeight: 34)
            } else if store.historyInstances.count > 4 {
                Text("另有 \(store.historyInstances.count - 4) 条历史 · 在“全部管理”中查看")
                    .font(.system(size: 10))
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity)
                    .frame(height: 20)
            }
        }
        .background(Color(nsColor: .controlBackgroundColor))
        .clipShape(RoundedRectangle(cornerRadius: 9))
        .overlay(RoundedRectangle(cornerRadius: 9).stroke(Color.primary.opacity(0.09)))
    }

    private var errorPresented: Binding<Bool> {
        Binding(get: { store.errorMessage != nil }, set: { if !$0 { store.errorMessage = nil } })
    }

    private func groupTitle(_ title: String, count: Int) -> some View {
        HStack(spacing: 5) {
            Image(systemName: "chevron.down").font(.system(size: 8, weight: .bold))
            Text(title).font(.system(size: 12, weight: .semibold))
            Text("\(count)").font(.system(size: 10, weight: .medium)).foregroundStyle(.secondary)
            Spacer()
        }
        .frame(height: 17)
    }

    private var historyGroupTitle: some View {
        HStack(spacing: 5) {
            Image(systemName: "chevron.down").font(.system(size: 8, weight: .bold))
            Text("历史").font(.system(size: 12, weight: .semibold))
            Text("\(store.historyInstances.count)")
                .font(.system(size: 10, weight: .medium))
                .foregroundStyle(.secondary)
            Spacer()
            if !store.historyInstances.isEmpty {
                Button {
                    showClearHistoryConfirmation = true
                } label: {
                    Label("清理全部", systemImage: "trash")
                        .font(.system(size: 10, weight: .medium))
                        .padding(.horizontal, 6)
                        .frame(height: 21)
                }
                .buttonStyle(PlainHoverButtonStyle(cornerRadius: 5))
                .foregroundStyle(.red)
                .help("清理全部历史记录")
            }
        }
        .frame(height: 21)
    }

    private func runtimeMenuLabel(_ runtime: BrowserRuntime) -> some View {
        Label(
            "\(runtime.displayTitle) \(runtime.versionLabel)",
            systemImage: store.settings.defaultRuntimeID == runtime.id
                ? "checkmark.circle.fill"
                : runtime.kind.symbol
        )
    }

    private func chooseLocalBrowser() {
        let panel = NSOpenPanel()
        panel.title = "选择 Chrome、Chromium 或 Edge"
        panel.message = "可选择浏览器 .app，或 Contents/MacOS 中的可执行文件"
        panel.canChooseDirectories = true
        panel.canChooseFiles = true
        panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let url = panel.url,
              let runtime = store.addLocalRuntime(selectedURL: url) else { return }
        store.selectDefaultRuntime(runtime)
    }
}

struct ProxyPresetControl: View {
    @ObservedObject var store: InstanceStore
    @State private var showProxyCheckDetails = false

    var body: some View {
        VStack(spacing: 7) {
            HStack(spacing: 6) {
                Text("预设代理")
                    .font(.system(size: 12, weight: .bold))
                Text("用于高频本地网络调试")
                    .font(.system(size: 9.5))
                    .foregroundStyle(.secondary)
                Spacer()
                Menu {
                    if store.settings.recentProxyPresets.isEmpty {
                        Text("暂无历史")
                    } else {
                        ForEach(store.settings.recentProxyPresets) { preset in
                            Button { store.selectProxyPreset(preset) } label: {
                                Text(proxyPresetTitle(preset))
                            }
                        }
                    }
                } label: {
                    Label("历史", systemImage: "clock.arrow.circlepath")
                        .font(.system(size: 10.5, weight: .medium))
                        .frame(height: 22)
                }
                .menuStyle(.borderlessButton)
                .fixedSize()
                .help("最近使用的代理")
            }

            HStack(spacing: 6) {
                Picker("协议", selection: proxySchemeBinding) {
                    ForEach(ProxyScheme.allCases) { scheme in
                        Text(scheme.title).tag(scheme)
                    }
                }
                .labelsHidden()
                .pickerStyle(.menu)
                .controlSize(.small)
                .frame(width: 76)
                TextField("Host，例如 127.0.0.1", text: proxyHostBinding)
                    .textFieldStyle(.roundedBorder)
                    .controlSize(.small)
                TextField("端口", value: proxyPortBinding, format: .number.grouping(.never))
                    .textFieldStyle(.roundedBorder)
                    .controlSize(.small)
                    .frame(width: 66)
            }

            HStack(spacing: 6) {
                Text("备注")
                    .font(.system(size: 10.5, weight: .medium))
                    .foregroundStyle(.secondary)
                    .frame(width: 42, alignment: .leading)
                TextField("可选，例如 Yak MITM", text: proxyRemarkBinding)
                    .textFieldStyle(.roundedBorder)
                    .controlSize(.small)
                Text("最近 \(store.settings.recentProxyPresets.count)/5")
                    .font(.system(size: 9.5))
                    .foregroundStyle(.tertiary)
                    .frame(width: 46, alignment: .trailing)
            }

            HStack(spacing: 7) {
                Button {
                    withAnimation(.easeInOut(duration: 0.16)) {
                        store.isProxyAdvancedExpanded.toggle()
                    }
                } label: {
                    Label(
                        "高级",
                        systemImage: store.isProxyAdvancedExpanded ? "chevron.up" : "chevron.down"
                    )
                }
                .buttonStyle(SmallSecondaryButtonStyle())
                proxyCheckStatus
                Spacer(minLength: 4)
                Button { store.checkPresetProxy() } label: {
                    HStack(spacing: 5) {
                        if store.proxyCheckPhase == .checking {
                            ProgressView().controlSize(.small).scaleEffect(0.72)
                        } else {
                            Image(systemName: "wave.3.right")
                        }
                        Text(store.proxyCheckPhase == .checking ? "检测中" : "检测")
                    }
                }
                .buttonStyle(SmallSecondaryButtonStyle())
                .disabled(store.proxyCheckPhase == .checking)
                Button { _ = store.rememberPresetProxy() } label: {
                    Label("保存", systemImage: "square.and.arrow.down")
                }
                .buttonStyle(SmallOrangeButtonStyle())
            }

            if store.isProxyAdvancedExpanded {
                Divider()
                    .transition(.opacity)
                HStack(spacing: 6) {
                    Label("认证", systemImage: "lock.shield")
                        .font(.system(size: 10.5, weight: .semibold))
                        .frame(width: 48, alignment: .leading)
                    TextField("用户名（可选）", text: proxyUsernameBinding)
                        .textFieldStyle(.roundedBorder)
                        .controlSize(.small)
                    SecureField("密码（随历史保存）", text: proxyPasswordBinding)
                        .textFieldStyle(.roundedBorder)
                        .controlSize(.small)
                }
                .transition(.opacity.combined(with: .move(edge: .top)))

                HStack(spacing: 6) {
                    Label("目标", systemImage: "scope")
                        .font(.system(size: 10.5, weight: .semibold))
                        .frame(width: 48, alignment: .leading)
                    TextField("特定 URL / DOMAIN（可选）", text: proxyCheckTargetBinding)
                        .textFieldStyle(.roundedBorder)
                        .controlSize(.small)
                    Text("自动补全 https://")
                        .font(.system(size: 9))
                        .foregroundStyle(.tertiary)
                        .fixedSize()
                }
                .transition(.opacity.combined(with: .move(edge: .top)))
            }
        }
        .padding(10)
        .frame(height: store.isProxyAdvancedExpanded
               ? WidgetMetrics.expandedProxyHeight
               : WidgetMetrics.collapsedProxyHeight)
        .background(Color(nsColor: .controlBackgroundColor))
        .clipShape(RoundedRectangle(cornerRadius: 11))
        .overlay(RoundedRectangle(cornerRadius: 11).stroke(Brand.orange.opacity(0.18)))
        .sheet(isPresented: $showProxyCheckDetails) {
            if let report = store.proxyCheckReport {
                ProxyCheckDetailsView(report: report)
            }
        }
    }

    @ViewBuilder private var proxyCheckStatus: some View {
        switch store.proxyCheckPhase {
        case .idle:
            Text(store.settings.presetProxyPassword.isEmpty ? "" : "已保存认证")
                .foregroundStyle(.tertiary)
        case .checking:
            Text(store.proxyCheckMessage)
                .font(.system(size: 9.5, weight: .medium))
                .foregroundStyle(.secondary)
                .lineLimit(1)
        case .success:
            proxyCheckResultButton(color: .green, systemImage: "checkmark.circle.fill")
        case .failure:
            proxyCheckResultButton(color: .red, systemImage: "exclamationmark.triangle.fill")
        }
    }

    private func proxyCheckResultButton(color: Color, systemImage: String) -> some View {
        Button { showProxyCheckDetails = true } label: {
            Label(store.proxyCheckMessage, systemImage: systemImage)
                .font(.system(size: 9.5, weight: .semibold))
                .foregroundStyle(color)
                .lineLimit(1)
        }
        .buttonStyle(.plain)
        .help("点击查看每个检测目标的结果")
    }

    private var proxySchemeBinding: Binding<ProxyScheme> {
        Binding(
            get: { store.settings.presetProxyScheme },
            set: { store.updatePresetProxyScheme($0) }
        )
    }

    private var proxyHostBinding: Binding<String> {
        Binding(
            get: { store.settings.presetProxyHost },
            set: { store.updatePresetProxyHost($0) }
        )
    }

    private var proxyPortBinding: Binding<Int> {
        Binding(
            get: { store.settings.presetProxyPort },
            set: { store.updatePresetProxyPort($0) }
        )
    }

    private var proxyUsernameBinding: Binding<String> {
        Binding(
            get: { store.settings.presetProxyUsername },
            set: { store.updatePresetProxyUsername($0) }
        )
    }

    private var proxyPasswordBinding: Binding<String> {
        Binding(
            get: { store.settings.presetProxyPassword },
            set: { store.updatePresetProxyPassword($0) }
        )
    }

    private var proxyCheckTargetBinding: Binding<String> {
        Binding(
            get: { store.settings.presetProxyCheckTarget },
            set: { store.updatePresetProxyCheckTarget($0) }
        )
    }

    private var proxyRemarkBinding: Binding<String> {
        Binding(
            get: { store.settings.presetProxyRemark },
            set: { store.updatePresetProxyRemark($0) }
        )
    }

    private func proxyPresetTitle(_ preset: ProxyPreset) -> String {
        let identity = preset.username.isEmpty ? "" : " · \(preset.username)"
        return preset.remark.isEmpty
            ? "\(preset.server)\(identity)"
            : "\(preset.remark) · \(preset.server)\(identity)"
    }
}

private struct ProxyCheckDetailsView: View {
    @Environment(\.dismiss) private var dismiss
    let report: ProxyCheckReport

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 10) {
                Image(systemName: report.isSuccess ? "checkmark.circle.fill" : "xmark.circle.fill")
                    .font(.system(size: 24))
                    .foregroundStyle(report.isSuccess ? .green : .red)
                VStack(alignment: .leading, spacing: 2) {
                    Text(report.message).font(.system(size: 15, weight: .bold))
                    Text("任意一个目标可访问，即表示代理检测成功；单项失败仍会保留在明细中。")
                        .font(.system(size: 10.5))
                        .foregroundStyle(.secondary)
                }
                Spacer()
            }

            VStack(spacing: 0) {
                ForEach(report.details) { detail in
                    HStack(spacing: 9) {
                        Image(systemName: detail.isSuccess ? "checkmark.circle.fill" : "exclamationmark.triangle.fill")
                            .foregroundStyle(detail.isSuccess ? .green : .red)
                            .frame(width: 18)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(detail.target)
                                .font(.system(size: 11.5, weight: .semibold))
                                .lineLimit(1)
                                .truncationMode(.middle)
                            Text(detail.message)
                                .font(.system(size: 10))
                                .foregroundStyle(.secondary)
                                .lineLimit(2)
                        }
                        Spacer(minLength: 8)
                        Text(detail.elapsedMilliseconds == 0 ? "—" : "\(detail.elapsedMilliseconds) ms")
                            .font(.system(size: 9.5, design: .monospaced))
                            .foregroundStyle(.tertiary)
                    }
                    .padding(.horizontal, 10)
                    .frame(minHeight: 50)
                    if detail.id != report.details.last?.id { Divider() }
                }
            }
            .background(Color(nsColor: .controlBackgroundColor))
            .clipShape(RoundedRectangle(cornerRadius: 10))

            HStack {
                Text("总超时：10 秒")
                    .font(.system(size: 10))
                    .foregroundStyle(.secondary)
                Spacer()
                Button("完成") { dismiss() }
                    .buttonStyle(SmallOrangeButtonStyle())
            }
        }
        .padding(18)
        .frame(width: 430)
        .frame(minHeight: 270)
    }
}

struct RunningInstanceRow: View {
    @ObservedObject var store: InstanceStore
    let instance: BrowserInstance

    var body: some View {
        HStack(spacing: 8) {
            InstanceThumbnail(instance: instance, kind: kind, width: 76, height: 48)
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 5) {
                    Text(instance.name).font(.system(size: 12, weight: .semibold)).lineLimit(1)
                    StatusBadge(text: "运行中", color: .green)
                }
                Text("\(kind.title) \(version) · \(instance.lastPageTitle ?? "正在读取页面标题")")
                    .font(.system(size: 10)).foregroundStyle(.secondary).lineLimit(1)
                Text("PID \(instance.processID) · 调试 :\(instance.debugPort) · \(source.title)")
                    .font(.system(size: 9.5)).foregroundStyle(.tertiary).lineLimit(1)
            }
            Spacer(minLength: 2)
            HStack(spacing: 6) {
                Button { store.focus(instance) } label: { Image(systemName: "arrow.up.right.square") }
                    .buttonStyle(IconButtonStyle()).help("打开浏览器")
                Button { Task { await store.capture(instance) } } label: { Image(systemName: "camera") }
                    .buttonStyle(IconButtonStyle()).help("快速截图")
                Button(role: .destructive) { store.stop(instance) } label: { Image(systemName: "stop.fill") }
                    .buttonStyle(HistoryDeleteButtonStyle()).help("停止并保存到历史")
            }
        }
        .padding(.horizontal, 9)
        .frame(height: 68)
        .overlay(alignment: .bottom) { Divider().padding(.leading, 40) }
    }

    private var runtime: BrowserRuntime? { store.runtime(for: instance) }
    private var kind: BrowserKind { runtime?.kind ?? instance.runtimeKind ?? .infer(name: instance.runtimeName, path: "") }
    private var version: String { runtime?.versionLabel ?? instance.runtimeVersion ?? "版本未知" }
    private var source: RuntimeSource { runtime?.source ?? instance.runtimeSource ?? .local }
}

struct CompactEmptyState: View {
    let runtime: BrowserRuntime?
    let chooseBrowser: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            BrowserGlyph(kind: runtime?.kind ?? .chrome, size: 32)
            VStack(alignment: .leading, spacing: 2) {
                Text("暂无运行中实例").font(.system(size: 13, weight: .semibold))
                if let runtime {
                    Text("下一次使用 \(runtime.displayTitle) \(runtime.versionLabel) · \(runtime.source.title)")
                        .font(.system(size: 10.5, weight: .medium)).foregroundStyle(.secondary).lineLimit(1)
                } else {
                    Text("请先选择下一次启动使用的浏览器")
                        .font(.system(size: 10.5, weight: .medium)).foregroundStyle(.secondary)
                }
            }
            Spacer(minLength: 4)
            Button(action: chooseBrowser) {
                Label(runtime == nil ? "选择浏览器" : "更换", systemImage: "folder")
            }
                .buttonStyle(SmallOrangeButtonStyle())
        }
        .padding(.horizontal, 10)
        .frame(height: 62)
        .background(Color(nsColor: .controlBackgroundColor))
        .clipShape(RoundedRectangle(cornerRadius: 9))
        .overlay(RoundedRectangle(cornerRadius: 9).stroke(Color.primary.opacity(0.1)))
    }
}

struct HistoryInstanceRow: View {
    @ObservedObject var store: InstanceStore
    let instance: BrowserInstance
    @State private var showRename = false
    @State private var draftName = ""

    var body: some View {
        HStack(spacing: 9) {
            InstanceThumbnail(instance: instance, kind: kind, width: 58, height: 36)
            VStack(alignment: .leading, spacing: 1) {
                Text(instance.lastPageTitle ?? "未记录页面标题")
                    .font(.system(size: 12, weight: .semibold)).lineLimit(1)
                Text("\(instance.name) · \(kind.title) \(version)")
                    .font(.system(size: 10)).foregroundStyle(.secondary).lineLimit(1)
            }
            Spacer(minLength: 4)
            Button { store.restoreHistory(instance) } label: {
                HStack(spacing: 4) {
                    if store.restoringInstanceID == instance.id && store.isLaunching {
                        ProgressView().controlSize(.small).scaleEffect(0.68)
                        Text("打开中")
                    } else {
                        Image(systemName: "play.fill")
                        Text("打开")
                    }
                }
            }
            .buttonStyle(SmallOrangeButtonStyle())
            .disabled(store.isLaunching)
            .help("使用原序号、用户目录和 Dock 角标恢复此历史")
            Button {
                draftName = instance.name
                showRename = true
            } label: { Image(systemName: "pencil") }
                .buttonStyle(IconButtonStyle()).help("修改名称")
            Button(role: .destructive) { store.removeHistory(instance) } label: { Image(systemName: "trash") }
                .buttonStyle(HistoryDeleteButtonStyle()).help("删除历史")
        }
        .padding(.horizontal, 9)
        .frame(height: 54)
        .overlay(alignment: .bottom) { Divider().padding(.leading, 40) }
        .alert("修改历史名称", isPresented: $showRename) {
            TextField("名称", text: $draftName)
            Button("取消", role: .cancel) {}
            Button("保存") { store.renameHistory(instance, name: draftName) }
                .disabled(draftName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
        } message: {
            Text(instance.lastPageTitle ?? "未记录页面标题")
        }
    }

    private var runtime: BrowserRuntime? { store.runtime(for: instance) }
    private var kind: BrowserKind { runtime?.kind ?? instance.runtimeKind ?? .infer(name: instance.runtimeName, path: "") }
    private var version: String { runtime?.versionLabel ?? instance.runtimeVersion ?? "版本未知" }
}

struct InstanceThumbnail: View {
    let instance: BrowserInstance
    let kind: BrowserKind
    let width: CGFloat
    let height: CGFloat

    var body: some View {
        ZStack {
            if let image = thumbnailImage {
                Image(nsImage: image)
                    .resizable()
                    .aspectRatio(contentMode: .fill)
                    .frame(width: width, height: height)
                    .clipped()
                LinearGradient(
                    colors: [.clear, .black.opacity(0.26)],
                    startPoint: .center,
                    endPoint: .bottom
                )
            } else {
                RoundedRectangle(cornerRadius: 7)
                    .fill(Brand.orange.opacity(0.10))
                VStack(spacing: 2) {
                    Image(systemName: "camera.viewfinder")
                        .font(.system(size: min(width, height) * 0.34, weight: .medium))
                    Text(instance.status == .running ? "读取中" : "无预览")
                        .font(.system(size: 8.5, weight: .medium))
                }
                .foregroundStyle(Brand.orange.opacity(0.88))
            }
        }
        .frame(width: width, height: height)
        .clipShape(RoundedRectangle(cornerRadius: 7))
        .overlay(RoundedRectangle(cornerRadius: 7).stroke(Color.primary.opacity(0.12)))
        .overlay(alignment: .bottomLeading) {
            BrowserGlyph(kind: kind, size: min(18, height * 0.42))
                .overlay(RoundedRectangle(cornerRadius: 5).stroke(.white.opacity(0.8), lineWidth: 1))
                .padding(3)
        }
        .overlay(alignment: .bottomTrailing) {
            if let badge = instance.dockBadge {
                DockBadgeMark(label: badge, size: min(15, height * 0.38)).padding(3)
            }
        }
        .overlay {
            if let path = previewPath {
                ThumbnailHoverAnchor(
                    imagePath: path,
                    title: instance.lastPageTitle ?? instance.name
                )
            }
        }
        .help(previewPath == nil ? "页面缩略图将在浏览器就绪后自动生成" : "悬停查看页面预览")
        .accessibilityLabel("\(instance.name) 页面缩略图")
    }

    private var previewPath: String? {
        let path = instance.thumbnailPath ?? instance.lastScreenshotPath
        guard let path, FileManager.default.fileExists(atPath: path) else { return nil }
        return path
    }

    private var thumbnailImage: NSImage? {
        guard let path = previewPath else { return nil }
        return NSImage(contentsOfFile: path)
    }
}

private struct ThumbnailHoverAnchor: NSViewRepresentable {
    let imagePath: String
    let title: String

    func makeNSView(context: Context) -> ThumbnailHoverTrackingView {
        let view = ThumbnailHoverTrackingView()
        view.imagePath = imagePath
        view.previewTitle = title
        return view
    }

    func updateNSView(_ nsView: ThumbnailHoverTrackingView, context: Context) {
        nsView.imagePath = imagePath
        nsView.previewTitle = title
    }
}

private final class ThumbnailHoverTrackingView: NSView {
    var imagePath = ""
    var previewTitle = ""
    private var tracking: NSTrackingArea?
    private var pendingPreview: DispatchWorkItem?

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let tracking { removeTrackingArea(tracking) }
        let next = NSTrackingArea(
            rect: bounds,
            options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect],
            owner: self,
            userInfo: nil
        )
        addTrackingArea(next)
        tracking = next
    }

    override func mouseEntered(with event: NSEvent) {
        pendingPreview?.cancel()
        let work = DispatchWorkItem { [weak self] in
            guard let self, self.window != nil else { return }
            ThumbnailPreviewPresenter.shared.show(
                imagePath: self.imagePath,
                title: self.previewTitle,
                relativeTo: self
            )
        }
        pendingPreview = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.18, execute: work)
    }

    override func mouseExited(with event: NSEvent) {
        pendingPreview?.cancel()
        pendingPreview = nil
        ThumbnailPreviewPresenter.shared.hide()
    }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        if window == nil {
            pendingPreview?.cancel()
            ThumbnailPreviewPresenter.shared.hide()
        }
    }
}

@MainActor
private final class ThumbnailPreviewPresenter {
    static let shared = ThumbnailPreviewPresenter()
    private let previewSize = NSSize(width: 320, height: 220)
    private var panel: NSPanel?
    private var imageView: NSImageView?
    private var titleField: NSTextField?

    func show(imagePath: String, title: String, relativeTo anchorView: NSView) {
        guard let image = NSImage(contentsOfFile: imagePath),
              let anchorWindow = anchorView.window else { return }
        let panel = previewPanel()
        imageView?.image = image
        titleField?.stringValue = title.isEmpty ? "浏览器页面预览" : title

        let anchorInWindow = anchorView.convert(anchorView.bounds, to: nil)
        let anchor = anchorWindow.convertToScreen(anchorInWindow)
        let screen = anchorWindow.screen ?? NSScreen.main ?? NSScreen.screens.first
        guard let visible = screen?.visibleFrame else { return }
        var x = anchor.minX - previewSize.width - 10
        if x < visible.minX + 8 { x = anchor.maxX + 10 }
        x = min(max(x, visible.minX + 8), visible.maxX - previewSize.width - 8)
        let y = min(
            max(anchor.midY - previewSize.height / 2, visible.minY + 8),
            visible.maxY - previewSize.height - 8
        )
        if panel.parent !== anchorWindow {
            panel.parent?.removeChildWindow(panel)
            anchorWindow.addChildWindow(panel, ordered: .above)
        }
        panel.setFrameOrigin(NSPoint(x: x, y: y))
        panel.orderFrontRegardless()
    }

    func hide() {
        panel?.orderOut(nil)
        if let panel, let parent = panel.parent {
            parent.removeChildWindow(panel)
        }
    }

    private func previewPanel() -> NSPanel {
        if let panel { return panel }
        let panel = NSPanel(
            contentRect: NSRect(origin: .zero, size: previewSize),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.level = .popUpMenu
        panel.isFloatingPanel = true
        panel.ignoresMouseEvents = true
        panel.collectionBehavior = [.canJoinAllSpaces, .transient, .fullScreenAuxiliary]

        let effect = NSVisualEffectView(frame: NSRect(origin: .zero, size: previewSize))
        effect.material = .popover
        effect.state = .active
        effect.wantsLayer = true
        effect.layer?.cornerRadius = 11
        effect.layer?.masksToBounds = true

        let imageView = NSImageView(frame: NSRect(x: 8, y: 31, width: 304, height: 181))
        imageView.imageScaling = .scaleProportionallyUpOrDown
        imageView.wantsLayer = true
        imageView.layer?.backgroundColor = NSColor.black.cgColor
        imageView.layer?.cornerRadius = 7
        imageView.layer?.masksToBounds = true
        effect.addSubview(imageView)

        let titleField = NSTextField(labelWithString: "")
        titleField.frame = NSRect(x: 10, y: 8, width: 300, height: 16)
        titleField.font = .systemFont(ofSize: 11, weight: .medium)
        titleField.textColor = .secondaryLabelColor
        titleField.lineBreakMode = .byTruncatingTail
        effect.addSubview(titleField)

        panel.contentView = effect
        self.panel = panel
        self.imageView = imageView
        self.titleField = titleField
        return panel
    }
}

struct BrowserGlyph: View {
    let kind: BrowserKind
    let size: CGFloat
    var body: some View {
        ZStack {
            RoundedRectangle(cornerRadius: size * 0.28).fill(Brand.orange.opacity(0.13))
            Image(systemName: kind.symbol).font(.system(size: size * 0.46, weight: .medium)).foregroundStyle(Brand.orange)
        }.frame(width: size, height: size)
    }
}

struct DockBadgeMark: View {
    let label: String
    let size: CGFloat

    var body: some View {
        Text(label)
            .font(.system(size: label.count == 1 ? size * 0.58 : size * 0.45, weight: .bold))
            .foregroundStyle(.white)
            .frame(width: size, height: size)
            .background(Brand.orange)
            .clipShape(Circle())
            .overlay(Circle().stroke(.white, lineWidth: 1.2))
            .accessibilityLabel("Dock 角标 \(label)")
    }
}

struct StatusBadge: View {
    let text: String
    let color: Color
    var body: some View {
        Text(text).font(.system(size: 9.5, weight: .semibold)).foregroundStyle(color)
            .padding(.horizontal, 6).padding(.vertical, 2).background(color.opacity(0.12)).clipShape(Capsule())
    }
}

struct LaunchActionLabel: View {
    let title: String
    let systemImage: String
    let isLoading: Bool

    var body: some View {
        HStack(spacing: 5) {
            if isLoading {
                ProgressView().controlSize(.small).scaleEffect(0.72)
                Text("正在启动…")
            } else {
                Image(systemName: systemImage)
                Text(title)
            }
        }
    }
}

struct LaunchStatusLine: View {
    let phase: BrowserLaunchPhase
    let message: String

    var body: some View {
        HStack(spacing: 5) {
            if phase == .succeeded {
                Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
            } else {
                ProgressView().controlSize(.small).scaleEffect(0.65)
            }
            Text(message.isEmpty ? "正在启动浏览器…" : message)
                .lineLimit(1)
        }
        .font(.system(size: 10.5, weight: .medium))
        .foregroundStyle(phase == .succeeded ? Color.green : Brand.orange)
    }
}

struct SmallOrangeButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        CompactHoverButton(configuration: configuration, palette: .primary,
                           horizontalPadding: 9, width: nil, height: 28, cornerRadius: 7)
    }
}

struct SmallSecondaryButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        CompactHoverButton(configuration: configuration, palette: .secondary,
                           horizontalPadding: 8, width: nil, height: 28, cornerRadius: 7)
    }
}

struct SmallDangerButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        CompactHoverButton(configuration: configuration, palette: .danger,
                           horizontalPadding: 8, width: nil, height: 28, cornerRadius: 7)
    }
}

struct IconButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        CompactHoverButton(configuration: configuration, palette: .icon,
                           horizontalPadding: 0, width: 24, height: 24, cornerRadius: 6)
    }
}

struct HistoryDeleteButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        CompactHoverButton(configuration: configuration, palette: .dangerIcon,
                           horizontalPadding: 0, width: 24, height: 24, cornerRadius: 6)
    }
}

struct PlainHoverButtonStyle: ButtonStyle {
    var cornerRadius: CGFloat = 7

    func makeBody(configuration: Configuration) -> some View {
        PlainHoverButton(configuration: configuration, cornerRadius: cornerRadius)
    }
}

private enum CompactButtonPalette {
    case primary
    case secondary
    case danger
    case icon
    case dangerIcon
}

private struct CompactHoverButton: View {
    let configuration: ButtonStyleConfiguration
    let palette: CompactButtonPalette
    let horizontalPadding: CGFloat
    let width: CGFloat?
    let height: CGFloat
    let cornerRadius: CGFloat
    @Environment(\.isEnabled) private var isEnabled
    @State private var isHovered = false

    var body: some View {
        configuration.label
            .font(.system(size: 11, weight: palette == .primary ? .semibold : .medium))
            .foregroundStyle(foregroundColor)
            .padding(.horizontal, horizontalPadding)
            .frame(width: width, height: height)
            .background(backgroundColor)
            .clipShape(RoundedRectangle(cornerRadius: cornerRadius))
            .overlay {
                RoundedRectangle(cornerRadius: cornerRadius)
                    .stroke(borderColor, lineWidth: isHovered && isEnabled ? 1 : 0.6)
            }
            .shadow(color: shadowColor, radius: isHovered && isEnabled ? 4 : 0, y: 1)
            .scaleEffect(configuration.isPressed ? 0.97 : 1)
            .opacity(isEnabled ? 1 : 0.45)
            .contentShape(RoundedRectangle(cornerRadius: cornerRadius))
            .animation(.easeOut(duration: 0.12), value: isHovered)
            .animation(.easeOut(duration: 0.08), value: configuration.isPressed)
            .onHover { isHovered = $0 }
    }

    private var foregroundColor: Color {
        switch palette {
        case .primary: return .white
        case .danger, .dangerIcon: return .red
        case .secondary: return .primary
        case .icon: return isHovered ? Brand.orange : .secondary
        }
    }

    private var backgroundColor: Color {
        switch palette {
        case .primary:
            return Brand.orange.opacity(configuration.isPressed ? 0.70 : (isHovered ? 0.86 : 1))
        case .secondary, .icon:
            return Color.primary.opacity(configuration.isPressed ? 0.17 : (isHovered ? 0.12 : 0.065))
        case .danger, .dangerIcon:
            return Color.red.opacity(configuration.isPressed ? 0.21 : (isHovered ? 0.15 : 0.075))
        }
    }

    private var borderColor: Color {
        switch palette {
        case .primary: return Color.white.opacity(isHovered ? 0.42 : 0.16)
        case .secondary, .icon: return Color.primary.opacity(isHovered ? 0.2 : 0.08)
        case .danger, .dangerIcon: return Color.red.opacity(isHovered ? 0.4 : 0.15)
        }
    }

    private var shadowColor: Color {
        switch palette {
        case .primary: return Brand.orange.opacity(0.35)
        case .danger, .dangerIcon: return Color.red.opacity(0.18)
        case .secondary, .icon: return Color.black.opacity(0.12)
        }
    }
}

private struct PlainHoverButton: View {
    let configuration: ButtonStyleConfiguration
    let cornerRadius: CGFloat
    @Environment(\.isEnabled) private var isEnabled
    @State private var isHovered = false

    var body: some View {
        configuration.label
            .background(Color.primary.opacity(isHovered && isEnabled ? 0.065 : 0))
            .overlay {
                RoundedRectangle(cornerRadius: cornerRadius)
                    .stroke(Brand.orange.opacity(isHovered && isEnabled ? 0.34 : 0), lineWidth: 1)
            }
            .clipShape(RoundedRectangle(cornerRadius: cornerRadius))
            .scaleEffect(configuration.isPressed ? 0.985 : 1)
            .opacity(isEnabled ? 1 : 0.45)
            .contentShape(RoundedRectangle(cornerRadius: cornerRadius))
            .animation(.easeOut(duration: 0.12), value: isHovered)
            .animation(.easeOut(duration: 0.08), value: configuration.isPressed)
            .onHover { isHovered = $0 }
    }
}

// Manager pages use the same compact primary control instead of introducing a second size system.
typealias FilledOrangeButtonStyle = SmallOrangeButtonStyle
typealias OutlineOrangeButtonStyle = SmallSecondaryButtonStyle
