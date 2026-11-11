import AppKit
import QuartzCore
import SwiftUI

/// Visual dwell only: never delays the next MCP operation or stopping the session.
enum DesktopFeedbackTiming {
    static let cursorHold: UInt64 = 5_000_000_000
    static let completedHold: UInt64 = 8_000_000_000
    static let attentionHold: UInt64 = 12_000_000_000
    static let fadeDuration: TimeInterval = 0.6
    static let fadeWait: UInt64 = 650_000_000
}

/// One public operation, including its automatic readback. Never stores typed text.
struct DesktopOperationFeedback: Identifiable, Equatable {
    enum Phase: String { case preparing, running, completed, failed, uncertain
        var label: String {
            switch self {
            case .preparing: return "即将执行"
            case .running: return "执行中"
            case .completed: return "已执行"
            case .failed: return "未完成"
            case .uncertain: return "待确认"
            }
        }
        var isWorking: Bool { self == .preparing || self == .running }
    }
    let id: UUID
    let summary: String
    let context: String
    let windowID: Int?
    var phase: Phase = .preparing
    var note = ""

    static func make(_ tool: String, arguments: [String: Any], windowTitle: String? = nil, elementLabel: String? = nil) -> Self {
        let target = arguments["target"] as? [String: Any]
        let window = (arguments["window_id"] as? Int) ?? (target?["window_id"] as? Int)
        let element = (arguments["element_index"] as? Int).map { "控件 [\($0)]" }
        let label = clean(elementLabel ?? "")
        let location = label.isEmpty ? element ?? (arguments["x"] != nil ? "画面中的指定位置" : "当前控件") : "「\(label)」"
        let summary: String
        switch tool {
        case "click":
            let verb = arguments["button"] as? String == "right" ? "右键点击" : arguments["count"] as? Int == 2 ? "双击" : "点击"
            summary = verb + location
        case "type_text":
            summary = "输入 \((arguments["text"] as? String ?? "").count) 个字符"
                + (arguments["replace"] as? Bool == true ? " · 替换原文" : "")
                + (arguments["submit"] as? Bool == true ? " · 按回车提交" : "")
        case "press_key":
            let modifiers = arguments["modifiers"] as? [String] ?? []
            summary = "按下 " + clean((modifiers + [arguments["key"] as? String ?? "按键"]).joined(separator: "+").uppercased())
        case "scroll":
            let direction = ["up": "上", "down": "下", "left": "左", "right": "右"][arguments["direction"] as? String ?? ""] ?? "下"
            summary = "向\(direction)滚动 \(arguments["amount"] as? Int ?? 1) \(arguments["by"] as? String == "page" ? "页" : "步")"
        case "activate_window": summary = "切换到目标窗口"
        case "launch_app": summary = "打开 " + clean(arguments["app"] as? String ?? "应用")
        default: summary = "操作桌面"
        }
        let title = clean(windowTitle ?? "")
        let context = target?["kind"] as? String == "desktop" ? "主显示器" : title.isEmpty ? window.map { "窗口 #\($0)" } ?? "应用" : title
        return Self(id: UUID(), summary: summary, context: context, windowID: window)
    }

    func finished(_ result: [String: Any]) -> Self {
        var updated = self
        let state = result["structuredContent"] as? [String: Any] ?? [:]
        if result["isError"] as? Bool == true {
            updated.phase = state["execution_state"] as? String == "Unknown" ? .uncertain : .failed
            updated.note = updated.phase == .uncertain ? "结果尚未确认，请重新观察；不要重复执行。" : "操作未完成，请查看控制页中的原因。"
        } else {
            updated.phase = .completed
            updated.note = state["observation_available"] as? Bool == false ? "动作已发送，暂未取得最新画面。" : ""
        }
        return updated
    }

    private static func clean(_ text: String) -> String {
        String(text.components(separatedBy: .controlCharacters).joined(separator: " ").prefix(70))
    }
}

/// Four narrow edge surfaces per display: no screen-sized backing store and no input interception.
enum DesktopFeedbackGeometry {
    static func edges(in screen: CGRect) -> [CGRect] {
        let thickness = min(80, min(screen.width, screen.height) / 10)
        return [CGRect(x: screen.minX, y: screen.minY, width: screen.width, height: thickness),
                CGRect(x: screen.minX, y: screen.maxY - thickness, width: screen.width, height: thickness),
                CGRect(x: screen.minX, y: screen.minY, width: thickness, height: screen.height),
                CGRect(x: screen.maxX - thickness, y: screen.minY, width: thickness, height: screen.height)]
    }
    static func banner(in visible: CGRect) -> CGRect {
        let width = min(560, max(240, visible.width - 32))
        return CGRect(x: visible.midX - width / 2, y: visible.maxY - 140, width: width, height: 124)
    }
}

final class DesktopFeedbackPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
    convenience init(frame: CGRect) {
        self.init(contentRect: frame, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        isOpaque = false; backgroundColor = .clear; hasShadow = false
        ignoresMouseEvents = true; hidesOnDeactivate = false; isReleasedWhenClosed = false
        level = NSWindow.Level(rawValue: NSWindow.Level.screenSaver.rawValue - 1)
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        isExcludedFromWindowsMenu = true; animationBehavior = .none
        setAccessibilityElement(false)
    }
}

@MainActor
final class DesktopFeedbackEdgeView: NSView {
    private let gradient = CAGradientLayer()
    init(frame: CGRect, edge: Int, working: Bool, failed: Bool) {
        super.init(frame: frame)
        wantsLayer = true
        gradient.frame = bounds
        let color = failed ? NSColor(srgbRed: 0.65, green: 0.25, blue: 0.20, alpha: 1) : Brand.controlCursorNS
        // A cubic tail reaches zero with zero slope. Full-height side strips overlap
        // the horizontal strips, so corners blend instead of ending at a hard seam.
        let stops = (0...16).map { CGFloat($0) / 16 }
        gradient.colors = stops.map { color.withAlphaComponent(0.82 * pow(1 - $0, 3)).cgColor }
        gradient.locations = stops.map { NSNumber(value: Double($0)) }
        gradient.startPoint = [CGPoint(x: 0.5, y: 0), CGPoint(x: 0.5, y: 1), CGPoint(x: 0, y: 0.5), CGPoint(x: 1, y: 0.5)][edge]
        gradient.endPoint = [CGPoint(x: 0.5, y: 1), CGPoint(x: 0.5, y: 0), CGPoint(x: 1, y: 0.5), CGPoint(x: 0, y: 0.5)][edge]
        layer?.addSublayer(gradient)
        if working && !NSWorkspace.shared.accessibilityDisplayShouldReduceMotion {
            let pulse = CABasicAnimation(keyPath: "opacity")
            pulse.fromValue = 0.65; pulse.toValue = 1; pulse.duration = 0.7
            pulse.autoreverses = true; pulse.repeatCount = .infinity
            gradient.add(pulse, forKey: "working")
        }
        setAccessibilityElement(false)
    }
    required init?(coder: NSCoder) { nil }
}

struct DesktopFeedbackBanner: View {
    let feedback: DesktopOperationFeedback
    var body: some View {
        VStack(alignment: .leading, spacing: 9) {
            HStack(spacing: 7) {
                Image(systemName: "paperplane.fill").foregroundStyle(Color(nsColor: Brand.controlCursorNS))
                Text("YTray · 电脑控制").font(.system(size: 11, weight: .medium)).foregroundStyle(.white.opacity(0.7))
                Spacer()
                Text(feedback.phase.label).font(.system(size: 11, weight: .semibold))
                    .padding(.horizontal, 9).padding(.vertical, 3)
                    .background(Color(nsColor: Brand.controlCursorNS).opacity(0.24), in: Capsule())
            }
            Text(feedback.summary).font(.system(size: 19, weight: .semibold)).lineLimit(1)
            Text(feedback.note.isEmpty ? feedback.context : feedback.note)
                .font(.system(size: 11)).foregroundStyle(.white.opacity(0.65)).lineLimit(1)
        }
        .foregroundStyle(.white).padding(18).frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
        .background(Color(red: 37 / 255, green: 30 / 255, blue: 28 / 255).opacity(0.96), in: RoundedRectangle(cornerRadius: 16))
        .overlay(RoundedRectangle(cornerRadius: 16).stroke(Color(nsColor: Brand.controlCursorNS).opacity(0.7), lineWidth: 1))
        .padding(3)
    }
}

/// All surfaces belong to the backend session and are excluded from its own desktop captures.
@MainActor
final class DesktopOperationOverlay {
    private var panels: [DesktopFeedbackPanel] = []
    private var dismissal: Task<Void, Never>?
    private var displayObserver: NSObjectProtocol?
    private var current: DesktopOperationFeedback?
    private var dismissalDeadline: TimeInterval?
    var windowIDs: Set<CGWindowID> { Set(panels.compactMap { $0.windowNumber > 0 ? CGWindowID($0.windowNumber) : nil }) }

    func update(_ feedback: DesktopOperationFeedback?) {
        dismissal?.cancel(); dismissal = nil
        if current != feedback {
            if let feedback, !feedback.phase.isWorking {
                let hold = feedback.phase == .completed ? DesktopFeedbackTiming.completedHold : DesktopFeedbackTiming.attentionHold
                dismissalDeadline = ProcessInfo.processInfo.systemUptime + Double(hold + DesktopFeedbackTiming.fadeWait) / 1_000_000_000
            } else { dismissalDeadline = nil }
        }
        current = feedback
        panels.forEach { $0.close() }; panels.removeAll()
        guard let feedback else { removeObserver(); return }
        if displayObserver == nil {
            displayObserver = NotificationCenter.default.addObserver(forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main) { [weak self] _ in
                Task { @MainActor [weak self] in
                    guard let self, let current = self.current else { return }
                    self.update(current)
                }
            }
        }
        for screen in NSScreen.screens {
            for (edge, rect) in DesktopFeedbackGeometry.edges(in: screen.frame).enumerated() {
                let panel = DesktopFeedbackPanel(frame: rect)
                panel.contentView = DesktopFeedbackEdgeView(frame: CGRect(origin: .zero, size: rect.size), edge: edge,
                    working: feedback.phase.isWorking, failed: feedback.phase == .failed || feedback.phase == .uncertain)
                panels.append(panel)
            }
        }
        if let screen = targetScreen(feedback.windowID) ?? NSScreen.main ?? NSScreen.screens.first {
            let panel = DesktopFeedbackPanel(frame: DesktopFeedbackGeometry.banner(in: screen.visibleFrame))
            let view = NSHostingView(rootView: DesktopFeedbackBanner(feedback: feedback))
            view.setAccessibilityElement(false); panel.contentView = view
            panels.append(panel)
        }
        panels.forEach { $0.orderFrontRegardless() }
        if let deadline = dismissalDeadline {
            // Screen/layout notifications rebuild the surfaces, but must not extend
            // the operation's original hold or restart its entire fade interval.
            dismissal = Task { @MainActor [weak self] in
                let hold = max(0, deadline - Double(DesktopFeedbackTiming.fadeWait) / 1_000_000_000 - ProcessInfo.processInfo.systemUptime)
                do { try await Task.sleep(nanoseconds: UInt64(hold * 1_000_000_000)) } catch { return }
                guard let self, self.current?.id == feedback.id else { return }
                let fade = max(0, deadline - ProcessInfo.processInfo.systemUptime)
                guard fade > 0 else { self.update(nil); return }
                NSAnimationContext.runAnimationGroup({ context in
                    context.duration = min(DesktopFeedbackTiming.fadeDuration, fade)
                    context.timingFunction = CAMediaTimingFunction(name: .easeInEaseOut)
                    self.panels.forEach { $0.animator().alphaValue = 0 }
                }, completionHandler: nil)
                let remaining = max(0, deadline - ProcessInfo.processInfo.systemUptime)
                do { try await Task.sleep(nanoseconds: UInt64(remaining * 1_000_000_000)) } catch { return }
                guard self.current?.id == feedback.id else { return }
                self.update(nil)
            }
        }
    }
    private func targetScreen(_ windowID: Int?) -> NSScreen? {
        guard let windowID, let cgID = CGWindowID(exactly: windowID),
              let info = (CGWindowListCopyWindowInfo(.optionIncludingWindow, cgID) as? [[String: Any]])?.first,
              let bounds = info[kCGWindowBounds as String] as? [String: Any],
              let frame = CGRect(dictionaryRepresentation: bounds as CFDictionary) else { return nil }
        let primaryTop = NSScreen.screens.first { ($0.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value == CGMainDisplayID() }?.frame.maxY ?? 0
        let center = DesktopCursorGeometry.appKitPoint(CGPoint(x: frame.midX, y: frame.midY), primaryDisplayHeight: primaryTop)
        return NSScreen.screens.first { $0.frame.contains(center) }
    }
    private func removeObserver() {
        if let displayObserver { NotificationCenter.default.removeObserver(displayObserver) }
        displayObserver = nil
    }
    deinit {
        dismissal?.cancel()
        if let displayObserver { NotificationCenter.default.removeObserver(displayObserver) }
    }
}
