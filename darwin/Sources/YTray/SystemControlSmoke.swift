import AppKit
import Foundation

/// Explicit local smoke entry point; never reachable through MCP or a web request.
@MainActor
func runSystemControlSmoke(application: NSApplication) {
    application.setActivationPolicy(.accessory)
    let fixture = SystemControlFixture()
    let externalArgument = CommandLine.arguments.firstIndex(of: "--system-control-external-fixture")
    let externalPath = externalArgument.flatMap { $0 + 1 < CommandLine.arguments.count ? CommandLine.arguments[$0 + 1] : nil }
    if externalPath == nil { fixture.window.orderFront(nil) }
    let argument = CommandLine.arguments.firstIndex(of: "--system-control-output")
    let output = argument.flatMap { $0 + 1 < CommandLine.arguments.count ? URL(fileURLWithPath: CommandLine.arguments[$0 + 1]) : nil }
    if let output { try? FileManager.default.createDirectory(at: output, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700]) }
    Task { @MainActor in
        let suite = "io.yaklang.ytray.system-smoke.\(UUID())"
        let defaults = UserDefaults(suiteName: suite)!
        let controller = SystemControlController(defaults: defaults)
        let testForeground = CommandLine.arguments.contains("--system-control-test-foreground")
        controller.allowForegroundDraft = testForeground
        var externalProcess: Process?
        let externalState = output?.appendingPathComponent("external-fixture-state.json")
        @MainActor func currentState() -> [String: Any] {
            if externalPath != nil, let externalState,
               let data = try? Data(contentsOf: externalState), let state = try? JSONSerialization.jsonObject(with: data) as? [String: Any] { return state }
            return fixture.state
        }
        @MainActor func count() -> Int { currentState()["count"] as? Int ?? -1 }
        @MainActor func message() -> String { currentState()["message"] as? String ?? "" }
        @MainActor func submissions() -> Int { currentState()["submissions"] as? Int ?? -1 }
        @MainActor func scrollY() -> Double { (currentState()["scroll_y"] as? NSNumber)?.doubleValue ?? -1 }
        var targetPID = Int(ProcessInfo.processInfo.processIdentifier)
        var targetWindow = fixture.window.windowNumber
        var steps: [[String: Any]] = []
        var transcript: [[String: Any]] = []
        @MainActor func save(_ name: String, _ value: Any) {
            guard let output, let data = try? JSONSerialization.data(withJSONObject: value, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]) else { return }
            try? data.write(to: output.appendingPathComponent(name + ".json"), options: .atomic)
        }
        @MainActor func call(_ stage: String, _ tool: String, _ arguments: [String: Any] = [:]) async throws -> [String: Any] {
            let before = currentState()
            var arguments = arguments
            // Existing cases intentionally exercise the legacy capture/token lifecycle.
            if SystemControlController.actions.contains(tool), arguments["return_state"] == nil { arguments["return_state"] = false }
            let result = try await controller.perform(tool, arguments: arguments)
            let request: [String: Any] = ["name": tool, "arguments": arguments]
            save(stage + "-request", request)
            save(stage + "-response", result)
            if let output {
                try? SystemControlController.text(result).write(to: output.appendingPathComponent(stage + ".txt"), atomically: true, encoding: .utf8)
                for (index, image) in (result["content"] as? [[String: Any]] ?? []).enumerated() where image["type"] as? String == "image" {
                    if let encoded = image["data"] as? String, let data = Data(base64Encoded: encoded) {
                        let ext = image["mimeType"] as? String == "image/jpeg" ? "jpg" : "png"
                        try? data.write(to: output.appendingPathComponent("\(stage)-\(index).\(ext)"))
                    }
                }
            }
            transcript.append(["stage": stage, "request": request, "text": SystemControlController.text(result),
                               "structured": result["structuredContent"] ?? [:], "is_error": result["isError"] ?? false,
                               "fixture_before": before, "fixture_after": currentState()])
            return result
        }
        @MainActor func check(_ name: String, _ passed: Bool, detail: String) {
            steps.append(["name": name, "passed": passed, "detail": detail])
            print("system smoke \(passed ? "PASS" : "FAIL"): \(name): \(detail)")
        }
        @MainActor func observe(_ stage: String) async throws -> [String: Any] {
            for attempt in 0..<15 {
                let result = try await call(stage, "get_window_state", [
                    "pid": targetPID, "window_id": targetWindow,
                    "tree_format": "both", "include_accessibility_tree": true, "include_screenshot": true,
                    "max_elements": 200, "max_depth": 16
                ])
                let code = (result["structuredContent"] as? [String: Any])?["error_code"] as? String
                if attempt == 14 || (code != "window_unresolved" && code != "window_unavailable") { return result }
                // A just-launched fixture can publish CG metadata before its AX server is ready.
                // Only observation is retried; input is never replayed.
                try await Task.sleep(nanoseconds: 100_000_000)
            }
            throw SystemControlError("Fixture observation unavailable")
        }
        @MainActor func token(_ result: [String: Any], label: String) throws -> String {
            let elements = (result["structuredContent"] as? [String: Any])?["elements"] as? [[String: Any]] ?? []
            guard let value = elements.first(where: { element in
                guard element["label"] as? String == label else { return false }
                return label != "Message" || (element["role"] as? String ?? "").lowercased().replacingOccurrences(of: "_", with: "").replacingOccurrences(of: " ", with: "").contains("textfield")
            })?["element_token"] as? String else {
                throw SystemControlError("Fixture AX token missing: \(label)")
            }
            return value
        }
        @MainActor func settle(_ predicate: @MainActor () -> Bool) async {
            for _ in 0..<20 {
                if predicate() { return }
                try? await Task.sleep(nanoseconds: 100_000_000)
            }
        }
        var status = "failed"
        do {
            if let externalPath {
                guard let externalState else { throw SystemControlError("外部 fixture 测试需要 --system-control-output") }
                try? FileManager.default.removeItem(at: externalState)
                let process = Process()
                process.executableURL = URL(fileURLWithPath: externalPath)
                process.arguments = [externalState.path]
                try process.run()
                externalProcess = process
                for _ in 0..<50 {
                    if (currentState()["pid"] as? Int) == Int(process.processIdentifier) { break }
                    try await Task.sleep(nanoseconds: 100_000_000)
                }
                guard let statePID = currentState()["pid"] as? Int, statePID == Int(process.processIdentifier),
                      let stateWindow = currentState()["window_id"] as? Int else { throw SystemControlError("外部 fixture 启动失败") }
                targetPID = statePID; targetWindow = stateWindow
            }
            await controller.start()
            guard controller.phase == .running else { throw SystemControlError(controller.lastError) }
            print("system smoke: Swift native application identity verified")
            let permission = try await call("00-permissions", "check_permissions")
            print(SystemControlController.text(permission))
            let permissionData = permission["structuredContent"] as? [String: Any] ?? [:]
            if permissionData["accessibility"] as? Bool != true || permissionData["screen_recording"] as? Bool != true {
                _ = try await observe("01-blocked-observation")
                status = "blocked_by_permissions"
            } else {
                let target: [String: Any] = ["kind": "window", "pid": targetPID, "window_id": targetWindow]
                let desktop = try await call("01-desktop", "get_desktop_state", ["max_image_dimension": 640])
                let desktopState = desktop["structuredContent"] as? [String: Any] ?? [:]
                check("desktop capture display binding", desktop["isError"] as? Bool != true
                    && desktopState["screenshot_frame_valid"] as? Bool == true && desktopState["capture_id"] as? String != nil,
                    detail: "ScreenCaptureKit capture matches the current primary display")
                _ = try await call("01-windows", "list_windows", ["pid": targetPID])
                let initial = try await observe("02-before")
                let click = try await call("03-click", "click", ["target": target, "element_token": try token(initial, label: "Increment"), "delivery_mode": "background"])
                await settle { count() == 1 }
                check("click", click["isError"] as? Bool != true && count() == 1, detail: "count 0 -> \(count())")
                _ = try await observe("04-after-click")
                let stale = try await call("04-stale-token", "click", ["target": target, "element_token": try token(initial, label: "Increment"), "delivery_mode": "background"])
                check("stale token refusal", stale["isError"] as? Bool == true && count() == 1, detail: "old snapshot token cannot increment")
                let rejected = try await call("05-invalid-token", "click", ["target": target, "element_token": "s00000000:999999", "delivery_mode": "background"])
                check("invalid token refusal", rejected["isError"] as? Bool == true && count() == 1, detail: "invalid token must not increment")
                let forTyping = try await observe("06-before-type")
                let expectedMessage = "Hello AX 你好"
                let typed = try await call("07-type", "type_text", ["target": target, "element_token": try token(forTyping, label: "Message"), "text": expectedMessage, "delivery_mode": "background"])
                await settle { message() == expectedMessage }
                check("type_text", typed["isError"] as? Bool != true && message() == expectedMessage, detail: message())
                let afterType = try await observe("08-after-type")
                let key = try await call("09-key", "press_key", ["target": target, "element_token": try token(afterType, label: "Message"), "key": "Return", "delivery_mode": "background"])
                await settle { submissions() == 1 }
                check("press_key", key["isError"] as? Bool != true && submissions() == 1, detail: "Return submissions=\(submissions())")
                let oldY = scrollY()
                let wheel = try await call("10-scroll", "scroll", ["target": target, "x": fixture.scroll.frame.midX,
                                            "y": externalPath == nil ? fixture.window.frame.height - fixture.scroll.frame.midY : 392,
                                            "direction": "down", "by": "line", "amount": 3, "delivery_mode": "background"])
                await settle { scrollY() > oldY }
                check("scroll", wheel["isError"] as? Bool != true && scrollY() > oldY,
                      detail: "scroll y \(oldY) -> \(scrollY())")
                _ = try await observe("11-after-scroll")
                let images = (initial["content"] as? [[String: Any]] ?? []).filter { $0["type"] as? String == "image" }.count
                check("window screenshot", images > 0, detail: "\(images) image(s)")
                let scaled = try await call("13-scaled-observation", "get_window_state", ["pid": targetPID, "window_id": targetWindow,
                    "tree_format": "both", "max_dimension": 320, "include_screenshot": true])
                let scaledData = scaled["structuredContent"] as? [String: Any] ?? [:]
                let scaledElements = scaledData["elements"] as? [[String: Any]] ?? []
                guard let buttonBounds = scaledElements.first(where: { $0["label"] as? String == "Increment" })?["bounds"] as? [String: Double],
                      let captureID = scaledData["capture_id"] as? String else { throw SystemControlError("缩放截图或按钮坐标缺失") }
                let centerX = (buttonBounds["x"] ?? 0) + (buttonBounds["width"] ?? 0) / 2
                let centerY = (buttonBounds["y"] ?? 0) + (buttonBounds["height"] ?? 0) / 2
                let badCapture = try await call("13-invalid-capture", "click", ["target": target, "x": centerX, "y": centerY, "capture_id": "stale-capture", "delivery_mode": "background"])
                check("invalid capture refusal", badCapture["isError"] as? Bool == true && count() == 1, detail: "stale capture cannot click")
                let coordinateClick = try await call("13-scaled-coordinate-click", "click", ["target": target, "x": centerX, "y": centerY, "capture_id": captureID, "delivery_mode": "background"])
                await settle { count() == 2 }
                check("scaled screenshot coordinates", coordinateClick["isError"] as? Bool != true && count() == 2 && (scaledData["image_width"] as? Int) == 320,
                      detail: "320px image; scale=\(scaledData["coordinate_scale"] ?? "?"); count=\(count())")
                let beforePage = scrollY()
                let page = try await call("13-page-scroll", "scroll", ["target": target, "element_token": try token(scaled, label: "Rows"), "direction": "down", "by": "page", "amount": 1, "delivery_mode": "background"])
                await settle { scrollY() > beforePage }
                check("AX page scroll", page["isError"] as? Bool != true && scrollY() > beforePage, detail: "scroll y \(beforePage) -> \(scrollY())")
                _ = try await call("13-ax-only", "get_window_state", ["pid": targetPID, "window_id": targetWindow, "include_screenshot": false])
                let noCapture = try await call("13-no-capture-click", "click", ["target": target, "x": centerX, "y": centerY, "delivery_mode": "background"])
                check("fresh capture required", noCapture["isError"] as? Bool == true && count() == 2, detail: "AX-only observation cannot reuse an older screenshot")
                let focusPreserved = transcript.filter { row in
                    let request = row["request"] as? [String: Any] ?? [:]
                    return SystemControlController.actions.contains(request["name"] as? String ?? "")
                }.allSatisfy { row in
                    (row["fixture_before"] as? [String: Any])?["frontmost_pid"] as? Int == (row["fixture_after"] as? [String: Any])?["frontmost_pid"] as? Int
                }
                check("background focus", focusPreserved, detail: "foreground application unchanged across each input call")
                if testForeground {
                    let expectedForegroundCount = count() + 1
                    let beforeForeground = try await observe("13-before-foreground")
                    let foregroundClick = try await call("14-foreground-click", "click", ["target": target, "element_token": try token(beforeForeground, label: "Increment"), "delivery_mode": "foreground"])
                    await settle { count() == expectedForegroundCount }
                    check("foreground click", foregroundClick["isError"] as? Bool != true && count() == expectedForegroundCount, detail: "count -> \(count())")
                    let beforeText = try await observe("15-before-foreground-text")
                    let foregroundText = try await call("16-foreground-type", "type_text", ["target": target, "element_token": try token(beforeText, label: "Message"), "text": " 🚀", "delivery_mode": "foreground"])
                    await settle { message().contains("🚀") }
                    check("foreground Unicode", foregroundText["isError"] as? Bool != true && message().contains("🚀"), detail: message())
                    let foregroundKey = try await call("17-foreground-key", "press_key", ["target": target, "key": "Return", "delivery_mode": "foreground"])
                    await settle { submissions() == 2 }
                    check("foreground key", foregroundKey["isError"] as? Bool != true && submissions() == 2, detail: "Return submissions=\(submissions())")
                    _ = try await observe("18-after-foreground")
                }
                let full = try await observe("19-efficiency-full")
                let compact = try await call("20-efficiency-compact", "get_window_state", ["window_id": targetWindow, "include_screenshot": false])
                func textBytes(_ value: [String: Any]) -> Int {
                    var copy = value
                    copy["content"] = (value["content"] as? [[String: Any]] ?? []).filter { $0["type"] as? String != "image" }
                    return (try? JSONSerialization.data(withJSONObject: copy, options: [.sortedKeys]).count) ?? 0
                }
                let compactState = compact["structuredContent"] as? [String: Any] ?? [:]
                check("compact single representation", compactState["elements"] == nil && textBytes(compact) < textBytes(full),
                      detail: "full=\(textBytes(full)) bytes, compact=\(textBytes(compact)) bytes excluding images; not token counts")
                let all = (full["structuredContent"] as? [String: Any])?["elements"] as? [[String: Any]] ?? []
                guard let button = all.first(where: { $0["label"] as? String == "Increment" }),
                      let index = button["element_index"] as? Int, let region = button["bounds"] as? [String: Double] else { throw SystemControlError("Missing compact control mapping") }
                let regional = try await call("21-efficiency-region", "get_window_state", ["pid": targetPID, "window_id": targetWindow, "region": region])
                let regionalState = regional["structuredContent"] as? [String: Any] ?? [:]
                check("region without screenshot", !(regional["content"] as? [[String: Any]] ?? []).contains { $0["type"] as? String == "image" }
                      && SystemControlController.text(regional).contains("[\(index)]"), detail: "region=\(textBytes(regional)) bytes; original index=\(index)")
                let beforeIndexCount = count()
                let indexed = try await call("22-indexed-click", "click", ["window_id": targetWindow, "element_index": index,
                    "snapshot_id": regionalState["snapshot_id"] ?? "", "return_state": true])
                await settle { count() == beforeIndexCount + 1 }
                check("indexed action returns state", count() == beforeIndexCount + 1 && (indexed["structuredContent"] as? [String: Any])?["observation_available"] as? Bool == true,
                      detail: "one input call returned a fresh snapshot")
                let staleIndex = try await call("23-stale-index", "click", ["target": target, "element_index": index,
                    "snapshot_id": regionalState["snapshot_id"] ?? "", "return_state": true])
                check("old index snapshot refusal", staleIndex["isError"] as? Bool == true && count() == beforeIndexCount + 1, detail: "no repeated input")
                let typingState = try await observe("24-combined-before")
                let oldSubmissions = submissions()
                let combined = try await call("25-combined-type", "type_text", ["target": target,
                    "element_token": try token(typingState, label: "Message"), "text": "Combined 你好", "replace": true, "submit": true, "return_state": true])
                await settle { message() == "Combined 你好" && submissions() == oldSubmissions + 1 }
                check("replace submit and state", combined["isError"] as? Bool != true && message() == "Combined 你好" && submissions() == oldSubmissions + 1,
                      detail: "replace + confirm in one call, submissions=\(submissions())")
                if testForeground {
                    let before = try await observe("26-foreground-combined-before")
                    let expected = "Foreground 你好 👨‍👩‍👧‍👦"
                    let old = submissions()
                    let compound = try await call("27-foreground-combined", "type_text", ["target": target,
                        "element_token": try token(before, label: "Message"), "text": expected,
                        "replace": true, "submit": true, "return_state": true, "delivery_mode": "foreground", "restore_focus": false])
                    await settle { message() == expected && submissions() == old + 1 }
                    check("foreground replace submit", compound["isError"] as? Bool != true && message() == expected && submissions() == old + 1,
                          detail: message())
                    let selected = try await call("28-chord-selection", "press_key", ["window_id": targetWindow,
                        "key": "cmd+a", "return_state": true, "delivery_mode": "foreground", "restore_focus": false])
                    let focus = (selected["structuredContent"] as? [String: Any])?["focus"] as? [String: Any]
                    check("key chord actual selection", selected["isError"] as? Bool != true && focus?["selected_text"] as? String == expected,
                          detail: "AX selection=\(focus?["selected_text"] ?? "unavailable")")
                }
                status = steps.allSatisfy { $0["passed"] as? Bool == true } ? "passed" : "failed"
            }
            _ = try await call("12-end-session", "end_session")
            check("stop revocation", controller.phase == .stopped && controller.configurationJSON(port: 51373) == nil, detail: "session ended and credential revoked")
        } catch {
            status = "failed"
            controller.stop()
            steps.append(["name": "runtime", "passed": false, "detail": error.localizedDescription])
            print("system smoke FAILED: \(error.localizedDescription)")
        }
        if status == "passed" && steps.contains(where: { $0["passed"] as? Bool == false }) { status = "failed" }
        save("transcript", transcript)
        save("summary", ["status": status, "steps": steps, "fixture_final": currentState(),
                         "bundle_id": Bundle.main.bundleIdentifier ?? "", "team_id": AppSigningIdentity.current?.teamIdentifier ?? "",
                         "adhoc": AppSigningIdentity.current?.isAdHoc ?? true])
        print("system smoke status=\(status)")
        controller.stop()
        defaults.removePersistentDomain(forName: suite)
        if let process = externalProcess, process.isRunning { process.terminate() }
        fixture.window.close()
        application.terminate(nil)
    }
    application.run()
}

@MainActor
private final class SystemControlFixture: NSObject {
    let window = NSWindow(contentRect: NSRect(x: 80, y: 100, width: 640, height: 540), styleMask: [.titled, .closable], backing: .buffered, defer: false)
    private(set) var count = 0
    private(set) var submissions = 0
    let message = NSTextField()
    let scroll = NSScrollView()
    private let counter = NSTextField(labelWithString: "Count 0")
    var state: [String: Any] {
        ["count": count, "message": message.stringValue, "submissions": submissions,
         "scroll_y": scroll.contentView.bounds.origin.y, "window_id": window.windowNumber,
         "frontmost_pid": NSWorkspace.shared.frontmostApplication.map { Int($0.processIdentifier) } ?? 0]
    }
    override init() {
        super.init()
        window.title = "YTray System Fixture"
        window.isReleasedWhenClosed = false
        let title = NSTextField(labelWithString: "YTray · AX interaction fixture")
        title.font = .systemFont(ofSize: 20, weight: .semibold)
        title.frame = NSRect(x: 24, y: 480, width: 580, height: 28)
        window.contentView?.addSubview(title)
        let button = NSButton(title: "Increment", target: self, action: #selector(increment))
        button.setAccessibilityIdentifier("demo.increment")
        button.frame = NSRect(x: 24, y: 430, width: 130, height: 32)
        counter.frame = NSRect(x: 180, y: 434, width: 180, height: 25)
        counter.setAccessibilityLabel("Counter")
        window.contentView?.addSubview(button)
        window.contentView?.addSubview(counter)
        let label = NSTextField(labelWithString: "Message")
        label.frame = NSRect(x: 24, y: 390, width: 76, height: 25)
        window.contentView?.addSubview(label)
        message.frame = NSRect(x: 110, y: 386, width: 500, height: 30)
        message.placeholderString = "Type here"
        message.setAccessibilityLabel("Message")
        message.setAccessibilityIdentifier("demo.message")
        message.target = self
        message.action = #selector(submit)
        window.contentView?.addSubview(message)
        scroll.frame = NSRect(x: 24, y: 24, width: 586, height: 330)
        scroll.hasVerticalScroller = true
        scroll.autohidesScrollers = false
        scroll.borderType = .bezelBorder
        scroll.setAccessibilityLabel("Rows")
        let text = NSTextView(frame: NSRect(x: 0, y: 0, width: 560, height: 1800))
        text.isEditable = false
        text.font = .monospacedSystemFont(ofSize: 14, weight: .regular)
        text.string = (1...80).map { String(format: "Row %02d | YTray AX / click / type / scroll", $0) }.joined(separator: "\n")
        text.setAccessibilityLabel("Scrollable rows")
        scroll.documentView = text
        window.contentView?.addSubview(scroll)
        window.makeFirstResponder(message)
    }
    @objc private func increment() { count += 1; counter.stringValue = "Count \(count)" }
    @objc private func submit() { submissions += 1 }
}
