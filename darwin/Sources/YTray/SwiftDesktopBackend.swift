import AppKit
import ApplicationServices
import CoreGraphics
import Foundation
import ScreenCaptureKit

/// YTray owns this runtime, AX handles, capture and cancellation in its own process.
/// Only Apple frameworks are used; there is no executable, daemon, socket or SDK dependency.
@MainActor
final class SwiftDesktopBackend: DesktopControlBackend {
    var onFailure: ((String) -> Void)?
    private let lease = DesktopSessionLease()
    private let indicator = DesktopActionIndicator()
    private let feedbackOverlay = DesktopOperationOverlay()
    private lazy var worker = NativeAXWorker(lease: lease, onAction: { [weak self] cue, id in
        DispatchQueue.main.sync {
            guard let self, self.generation == id else { return }
            self.indicator.show(cue)
        }
    })
    private var generation: UUID?
    private var desktopCapture: (id: String, date: Date, scale: CGFloat, frame: CGRect)?

    func start(hostBundleID: String) async throws {
        stop()
        guard Bundle.main.bundleIdentifier == hostBundleID else { throw SystemControlError("原生后端的应用身份不匹配") }
        generation = lease.begin()
    }
    func stop() { lease.end(); generation = nil; desktopCapture = nil; indicator.hide(); feedbackOverlay.update(nil); worker.clear() }
    func updateFeedback(_ feedback: DesktopOperationFeedback?) {
        guard generation != nil || feedback == nil else { return }
        feedbackOverlay.update(feedback)
    }
    func describeOperation(_ name: String, arguments: [String: Any]) async throws -> DesktopOperationFeedback {
        guard let id = generation else { throw SystemControlError("电脑控制会话已结束") }
        let context = try await worker.run(id) { $0.feedbackContext(arguments) }
        return DesktopOperationFeedback.make(name, arguments: arguments,
            windowTitle: context["title"] as? String, elementLabel: context["label"] as? String)
    }

    func request(_ method: String, params: [String: Any]) async throws -> [String: Any] {
        guard let id = generation else { throw SystemControlError("电脑控制会话已结束") }
        try lease.check(id)
        if method == "tools/list" { return ["tools": DesktopNativeTools.definitions] }
        guard method == "tools/call", let name = params["name"] as? String else { throw SystemControlError("无效的原生后端调用") }
        let args = params["arguments"] as? [String: Any] ?? [:]
        var mayHaveExecuted = false
        do {
            if name == "health_report" {
                return Self.result("YTray Swift native desktop backend", ["checks": [["name": "bundle_identity", "status": "pass", "data": [
                    "bundle_identifier": Bundle.main.bundleIdentifier ?? "", "identity_source": "application",
                    "process_id": Int(ProcessInfo.processInfo.processIdentifier), "backend": "swift-native"
                ]]]])
            }
            if name == "end_session" { stop(); return Self.result("Session ended", ["ended": true]) }
            guard let definition = DesktopNativeTools.definitions.first(where: { $0["name"] as? String == name }),
                  let schema = definition["inputSchema"] as? [String: Any], let properties = schema["properties"] as? [String: Any],
                  Set(args.keys).isSubset(of: Set(properties.keys)) else {
                throw DesktopOperationError("invalid_arguments", "不支持的工具或参数")
            }
            try NativeAXWorker.validate(args, schema: schema)
            let supplied = args
            let args = supplied["pid"] == nil && supplied["target"] == nil && supplied["window_id"] != nil
                ? try await worker.run(id) { try $0.resolveOwner(supplied) } : supplied
            switch name {
            case "check_permissions":
                let grants = ComputerControlPermissions.current()
                return Self.result("Accessibility: \(grants.accessibility); Screen Recording: \(grants.screenRecording)", [
                    "accessibility": grants.accessibility, "screen_recording": grants.screenRecording,
                    "backend": "swift-native", "source": ["attribution": "host", "host_bundle_id": Bundle.main.bundleIdentifier ?? "", "pid": Int(ProcessInfo.processInfo.processIdentifier)],
                    "capabilities": ["background_element_actions": true, "background_pixel_events": false,
                                     "general_background_keys": false, "foreground_input_requires_authorization": true,
                                     "capture": "ScreenCaptureKit", "window_matching": "unique_pid_bounds_title", "agent_cursor_overlay": true,
                                     "agent_cursor_style": "terracotta_dart", "operation_feedback": true, "ambient_screen_edges": true]
                ])
            case "list_apps":
                let query = args["query"] as? String ?? ""
                let apps: [[String: Any]] = NSWorkspace.shared.runningApplications.filter {
                    !$0.isTerminated && (query.isEmpty || ($0.localizedName ?? "").localizedCaseInsensitiveContains(query) || ($0.bundleIdentifier ?? "").localizedCaseInsensitiveContains(query))
                }.map {
                    ["pid": Int($0.processIdentifier), "name": $0.localizedName ?? "", "bundle_id": $0.bundleIdentifier ?? "",
                     "running": true, "active": $0.isActive]
                }
                return Self.result("Found \(apps.count) running apps", ["apps": apps])
            case "list_windows": return try await worker.run(id) { try $0.listWindows(args) }
            case "launch_app":
                guard let bundleID = args["app"] as? String,
                      let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID),
                      Bundle(url: url)?.bundleIdentifier == bundleID else {
                    throw DesktopOperationError("app_not_found", "请指定已安装应用的准确 Bundle ID，例如 com.apple.TextEdit")
                }
                try lease.check(id)
                let configuration = NSWorkspace.OpenConfiguration()
                configuration.activates = true
                mayHaveExecuted = true
                let app = try await NSWorkspace.shared.openApplication(at: url, configuration: configuration)
                try lease.check(id)
                let listed: [String: Any]
                do { listed = try await worker.run(id) { try $0.listWindows(["pid": Int(app.processIdentifier)]) } }
                catch {
                    try lease.check(id)
                    listed = DesktopOperationError("observation_unavailable", "应用已打开，暂未取得窗口列表").toolResult
                }
                return Self.finishAction(Self.result("Application opened", ["pid": Int(app.processIdentifier), "bundle_id": bundleID, "delivered": true]), observation: listed)
            case "activate_window":
                guard AXIsProcessTrusted() else { throw DesktopOperationError("permission_denied", "YTray 尚未获得辅助功能权限") }
                let target = try NativeAXWorker.windowTarget(args)
                mayHaveExecuted = true
                try await worker.run(id) { try $0.activateWindow(target); return [:] }
                try lease.check(id)
                try await activateApplication(target, generation: id)
                try await worker.run(id) { try $0.verifyActivatedWindow(target, generation: id); return [:] }
                let observed = try await request("tools/call", params: ["name": "get_window_state", "arguments": [
                    "window_id": Int(target.windowID), "include_screenshot": args["include_screenshot"] as? Bool ?? false]])
                return Self.finishAction(Self.result("Window activated", ["delivered": true]), observation: observed)
            case "get_window_state":
                var result = try await worker.run(id) { try $0.observe(args, generation: id) }
                if try DesktopObservation.wantsScreenshot(args) {
                    do {
                        let pid = try NativeAXWorker.positiveInt(args, "pid")
                        let windowID = try NativeAXWorker.positiveInt(args, "window_id")
                        let capture = try await captureWindow(pid: pid_t(pid), windowID: CGWindowID(windowID), maximum: try Self.maximum(args))
                        try lease.check(id)
                        let imageBlock = try Self.image(capture.image)
                        let snapshot = (result["structuredContent"] as? [String: Any])?["snapshot_id"] as? String
                        if let snapshot {
                            try await worker.run(id) { try $0.setCapture(snapshot, scale: capture.scale, frame: capture.frame); return [:] }
                        }
                        var structured = result["structuredContent"] as? [String: Any] ?? [:]
                        structured["screenshot_frame_valid"] = true
                        structured["capture_id"] = snapshot
                        structured["image_width"] = capture.image.width
                        structured["image_height"] = capture.image.height
                        structured["coordinate_scale"] = capture.scale
                        result["structuredContent"] = structured
                        var content = result["content"] as? [[String: Any]] ?? []
                        content.append(imageBlock)
                        result["content"] = content
                    } catch {
                        try lease.check(id)
                        var structured = result["structuredContent"] as? [String: Any] ?? [:]
                        structured["screenshot_frame_valid"] = false
                        structured["screenshot_error"] = ["code": "capture_unavailable", "reason": error.localizedDescription]
                        structured["degraded"] = true
                        result["structuredContent"] = structured
                    }
                }
                try lease.check(id)
                return try DesktopObservation.format(result, arguments: args)
            case "get_desktop_state":
                guard CGPreflightScreenCaptureAccess() else { throw DesktopOperationError("permission_denied", "YTray 尚未获得屏幕录制权限") }
                let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
                try lease.check(id)
                guard let display = content.displays.first(where: { $0.displayID == CGMainDisplayID() }) else { throw DesktopOperationError("display_unavailable", "找不到主显示器") }
                let overlayIDs = feedbackOverlay.windowIDs.union(indicator.windowID.map { [$0] } ?? [])
                let filter = SCContentFilter(display: display, excludingWindows: content.windows.filter { overlayIDs.contains($0.windowID) })
                let capture = try await Self.capture(filter: filter, frame: display.frame, maximum: try Self.maximum(args))
                try lease.check(id)
                let captureID = UUID().uuidString
                desktopCapture = (captureID, Date(), capture.scale, display.frame)
                var result = Self.result("Primary display \(capture.image.width)x\(capture.image.height)", [
                    "display_id": "primary", "capture_id": captureID, "image_width": capture.image.width,
                    "image_height": capture.image.height, "coordinate_scale": capture.scale, "screenshot_frame_valid": true
                ])
                result["content"] = [["type": "text", "text": "Primary display capture"], try Self.image(capture.image)]
                return result
            default:
                let normalized = try await worker.run(id) { try $0.actionArguments(args) }
                let result: [String: Any]
                do { result = try await performAction(name, args: normalized, generation: id) }
                catch let error as DesktopOperationError {
                    var failure = error.toolResult
                    var state = failure["structuredContent"] as? [String: Any] ?? [:]
                    state["execution_state"] = "Unknown"
                    state["automatic_replay_allowed"] = false
                    failure["structuredContent"] = state
                    result = failure
                }
                try lease.check(id)
                guard try NativeAXWorker.bool(args, "return_state", fallback: true),
                      (args["target"] as? [String: Any])?["kind"] as? String != "desktop" else { return result }
                let target = try NativeAXWorker.windowTarget(normalized)
                let observation: [String: Any] = ["pid": Int(target.pid), "window_id": Int(target.windowID),
                    "include_screenshot": try NativeAXWorker.bool(args, "include_screenshot", fallback: args["x"] != nil || args["y"] != nil)]
                let latest = try await request("tools/call", params: ["name": "get_window_state", "arguments": observation])
                try lease.check(id)
                return Self.finishAction(result, observation: latest)
            }
        } catch let error as DesktopOperationError { return Self.operationFailure(error, mayHaveExecuted: mayHaveExecuted) }
        catch {
            try lease.check(id)
            return Self.operationFailure(DesktopOperationError("native_error", "\(type(of: error)): \(error.localizedDescription)"), mayHaveExecuted: mayHaveExecuted)
        }
    }

    nonisolated static func operationFailure(_ error: DesktopOperationError, mayHaveExecuted: Bool) -> [String: Any] {
        var result = error.toolResult
        if mayHaveExecuted {
            var state = result["structuredContent"] as? [String: Any] ?? [:]
            state["execution_state"] = "Unknown"
            state["automatic_replay_allowed"] = false
            result["structuredContent"] = state
        }
        return result
    }

    private func performAction(_ name: String, args: [String: Any], generation id: UUID) async throws -> [String: Any] {
        guard AXIsProcessTrusted() else { throw DesktopOperationError("permission_denied", "YTray 尚未获得辅助功能权限") }
        let foreground = args["delivery_mode"] as? String == "foreground"
        let desktop = (args["target"] as? [String: Any])?["kind"] as? String == "desktop"
        if desktop && !foreground { throw DesktopOperationError("background_unavailable", "桌面目标只支持明确授权的前台输入") }
        let indicatorRevision = indicator.revision
        defer { if generation == id, indicator.revision != indicatorRevision { indicator.finish() } }
        let previous = NSWorkspace.shared.frontmostApplication
        let target = desktop ? nil : try NativeAXWorker.windowTarget(args)
        defer {
            // Restore only our own temporary activation; respect a user's intervening app switch.
            if foreground, args["restore_focus"] as? Bool != false, let target, let previous, !previous.isTerminated, generation == id,
               NSWorkspace.shared.frontmostApplication?.processIdentifier == target.pid,
               previous.processIdentifier != target.pid {
                previous.activate(options: [])
            }
        }
        if foreground, let target {
            try await worker.run(id) { try $0.raiseWindow(target, tool: name, args: args); return [:] }
            try lease.check(id)
            try await activateApplication(target, generation: id)
            try lease.check(id)
        }
        let capture = desktopCapture
        let result = try await worker.run(id) { try $0.act(name, args: args, generation: id, desktopCapture: capture) }
        if foreground {
            // CGEvent posting is asynchronous. Keep the admitted target active long
            // enough to consume the final key-up before restoring our activation.
            try await Task.sleep(nanoseconds: 40_000_000)
            try lease.check(id)
        }
        return result
    }

    private func activateApplication(_ target: NativeAXWorker.Target, generation id: UUID) async throws {
        try lease.check(id)
        guard let app = NSRunningApplication(processIdentifier: target.pid), !app.isTerminated else {
            throw DesktopOperationError("foreground_unavailable", "目标应用已退出")
        }
        if !app.isActive, !app.activate(options: []) {
            try await worker.run(id) { try $0.activateApplication(target); return [:] }
        }
        try lease.check(id)
    }


    /// Preserve execution errors independently of whether the post-action read works.
    nonisolated static func finishAction(_ action: [String: Any], observation: [String: Any]) -> [String: Any] {
        guard observation["isError"] as? Bool != true else {
            var result = action
            var state = result["structuredContent"] as? [String: Any] ?? [:]
            state["observation_error"] = observation["structuredContent"]
            state["observation_available"] = false
            result["structuredContent"] = state
            return result
        }
        var result = observation
        var state = result["structuredContent"] as? [String: Any] ?? [:]
        state["action"] = action["structuredContent"] ?? [:]
        if action["isError"] as? Bool == true, let failure = action["structuredContent"] as? [String: Any] {
            for key in ["error_code", "message", "execution_state", "automatic_replay_allowed"] { state[key] = failure[key] }
        }
        state["observation_available"] = true
        result["structuredContent"] = state
        result["isError"] = action["isError"] as? Bool ?? false
        let message = ((action["content"] as? [[String: Any]] ?? []).first?["text"] as? String ?? "Action returned")
        var content = result["content"] as? [[String: Any]] ?? []
        if !content.isEmpty { content[0]["text"] = message + "\n" + (content[0]["text"] as? String ?? "") }
        result["content"] = content
        return result
    }

    private func captureWindow(pid: pid_t, windowID: CGWindowID, maximum: Int) async throws -> (image: CGImage, scale: CGFloat, frame: CGRect) {
        guard CGPreflightScreenCaptureAccess() else { throw DesktopOperationError("permission_denied", "YTray 尚未获得屏幕录制权限") }
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
        guard let window = content.windows.first(where: { $0.windowID == windowID && $0.owningApplication?.processID == pid }) else {
            throw DesktopOperationError("window_unavailable", "ScreenCaptureKit 中不存在此 PID 的确切窗口")
        }
        let capture = try await Self.capture(filter: SCContentFilter(desktopIndependentWindow: window), frame: window.frame, maximum: maximum)
        return (capture.image, capture.scale, window.frame)
    }
    private static func maximum(_ args: [String: Any]) throws -> Int {
        try NativeAXWorker.integer(args, args["max_image_dimension"] == nil ? "max_dimension" : "max_image_dimension", fallback: 2048, range: 100...4096)
    }
    private static func capture(filter: SCContentFilter, frame: CGRect, maximum: Int) async throws -> (image: CGImage, scale: CGFloat) {
        let scale = min(1, CGFloat(maximum) / max(frame.width, frame.height))
        let config = SCStreamConfiguration()
        config.width = max(1, Int((frame.width * scale).rounded()))
        config.height = max(1, Int((frame.height * scale).rounded()))
        config.showsCursor = false
        config.ignoreShadowsSingleWindow = true
        let image = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: config)
        return (image, CGFloat(image.width) / frame.width)
    }
    nonisolated static func result(_ text: String, _ structured: [String: Any]) -> [String: Any] {
        ["content": [["type": "text", "text": text]], "structuredContent": structured, "isError": false]
    }
    private static func image(_ image: CGImage) throws -> [String: Any] {
        guard let png = NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:]) else {
            throw DesktopOperationError("capture_encoding_failed", "无法编码原生截图")
        }
        return ["type": "image", "mimeType": "image/png", "data": png.base64EncodedString()]
    }
}
