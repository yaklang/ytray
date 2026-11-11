import Foundation

struct SystemControlError: LocalizedError {
    let message: String
    var errorDescription: String? { message }
    init(_ message: String) { self.message = message }
}

@MainActor
protocol DesktopControlBackend: AnyObject {
    var onFailure: ((String) -> Void)? { get set }
    func start(hostBundleID: String) async throws
    func request(_ method: String, params: [String: Any]) async throws -> [String: Any]
    func describeOperation(_ name: String, arguments: [String: Any]) async throws -> DesktopOperationFeedback
    func updateFeedback(_ feedback: DesktopOperationFeedback?)
    func stop()
}

extension DesktopControlBackend {
    func describeOperation(_ name: String, arguments: [String: Any]) async throws -> DesktopOperationFeedback {
        DesktopOperationFeedback.make(name, arguments: arguments)
    }
    func updateFeedback(_ feedback: DesktopOperationFeedback?) {}
}

struct DesktopOperationError: LocalizedError {
    let code: String
    let message: String
    var errorDescription: String? { message }
    init(_ code: String, _ message: String) { self.code = code; self.message = message }
    var toolResult: [String: Any] {
        ["isError": true, "content": [["type": "text", "text": "\(code): \(message)"]],
         "structuredContent": ["error_code": code, "message": message, "backend": "swift-native"]]
    }
}

/// Cancellation is shared with the AX worker so stop revokes queued work immediately.
/// An OS call already admitted cannot be undone; its late result is discarded.
final class DesktopSessionLease: @unchecked Sendable {
    private let lock = NSLock()
    private var current: UUID?
    func begin() -> UUID { lock.lock(); defer { lock.unlock() }; let id = UUID(); current = id; return id }
    func end() { lock.lock(); current = nil; lock.unlock() }
    func check(_ id: UUID) throws {
        lock.lock(); let active = current == id; lock.unlock()
        guard active else { throw DesktopOperationError("session_ended", "电脑控制会话已结束") }
    }
}

enum DesktopNativeTools {
    static var definitions: [[String: Any]] {
        let integer: [String: Any] = ["type": "integer", "minimum": 1]
        let string: [String: Any] = ["type": "string"]
        let boolean: [String: Any] = ["type": "boolean"]
        let point: [String: Any] = ["type": "number", "minimum": 0]
        let target: [String: Any] = ["type": "object", "additionalProperties": false, "required": ["kind"], "properties": [
            "kind": ["type": "string", "enum": ["window", "desktop"]], "pid": integer, "window_id": integer,
            "display_id": ["type": "string", "enum": ["primary"]]
        ]]
        let input: [String: Any] = ["target": target, "pid": integer, "window_id": integer,
                                    "delivery_mode": ["type": "string", "enum": ["background", "foreground"], "default": "background"],
                                    "element_index": ["type": "integer", "minimum": 0, "maximum": 999], "snapshot_id": string,
                                    "include_screenshot": boolean, "return_state": ["type": "boolean", "default": true],
                                    "restore_focus": ["type": "boolean", "default": true, "description": "Foreground only: false keeps the target active for consecutive actions."]]
        func tool(_ name: String, _ description: String, _ properties: [String: Any], _ required: [String] = [], readOnly: Bool = true) -> [String: Any] {
            ["name": name, "description": description + (["click", "type_text", "press_key", "scroll"].contains(name) ? " Window actions return fresh compact state. element_index is the latest snapshot's original [N]; add snapshot_id to reject old indexes. return_state=false skips observation. Images default off except coordinate actions." : ""), "inputSchema": ["type": "object", "properties": properties,
                "required": required, "additionalProperties": false],
             "annotations": ["readOnlyHint": readOnly, "destructiveHint": !readOnly]]
        }
        return [
            tool("list_apps", "List running applications; optionally filter by app name or bundle ID.", ["query": string]),
            tool("list_windows", "List CG windows with exact owner PID, window ID and screen-point bounds.", ["pid": integer, "on_screen_only": boolean]),
            tool("launch_app", "Open an installed app by exact bundle ID, e.g. com.apple.TextEdit. Requires local foreground authorization. Returns its windows; no shell commands or URLs.", ["app": ["type": "string", "maxLength": 255]], ["app"], readOnly: false),
            tool("activate_window", "Activate an exact window and return its fresh state. Requires local foreground authorization.", ["window_id": integer, "include_screenshot": boolean], ["window_id"], readOnly: false),
            tool("check_permissions", "Read YTray's actual Accessibility and Screen Recording preflight state. Does not prompt.", [:]),
            tool("get_window_state", "Observe window_id from list_windows (pid optional, resolved from its actual owner). Default compact controls appear once as [N]; detail=full or tree_format exposes raw tokens. Region uses window-image pixels and defaults to no screenshot. Tokens are scoped to the latest snapshot of that window and this session. Screenshot coordinates use the returned coordinate scale.", [
                "pid": integer, "window_id": integer, "query": string, "include_accessibility_tree": boolean, "include_screenshot": boolean,
                "max_elements": ["type": "integer", "minimum": 1, "maximum": 1000],
                "max_depth": ["type": "integer", "minimum": 1, "maximum": 32],
                "timeout_ms": ["type": "integer", "minimum": 100, "maximum": 3000],
                "max_dimension": ["type": "integer", "minimum": 100, "maximum": 4096],
                "max_image_dimension": ["type": "integer", "minimum": 100, "maximum": 4096],
                "detail": ["type": "string", "enum": ["compact", "full"], "default": "compact"],
                "max_text_chars": ["type": "integer", "minimum": 256, "maximum": 30000],
                "region": ["type": "object", "additionalProperties": false, "required": ["x", "y", "width", "height"],
                    "properties": ["x": point, "y": point, "width": ["type": "number", "minimum": 0.01], "height": ["type": "number", "minimum": 0.01]]],
                "tree_format": ["type": "string", "enum": ["markdown", "elements", "both"]]
            ], ["window_id"]),
            tool("get_desktop_state", "Capture the primary display using Apple's ScreenCaptureKit. Does not install or launch another runtime.", ["max_image_dimension": ["type": "integer", "minimum": 100, "maximum": 4096]]),
            tool("click", "Background: AXPress/AXShowMenu on the selected element. Pixel hit-testing can resolve an AX control. Arbitrary pixels require explicitly authorized foreground mode. Never escalates automatically.", input.merging([
                "element_token": string, "capture_id": string, "x": point, "y": point,
                "button": ["type": "string", "enum": ["left", "right"]], "count": ["type": "integer", "minimum": 1, "maximum": 2]
            ]) { _, new in new }, readOnly: false),
            tool("type_text", "replace=true replaces text; submit=true confirms after typing if supported. Background inserts through AXSelectedText or a writable AX text value. Unsupported controls refuse; generic Unicode key events require foreground mode.", input.merging([
                "element_token": string, "capture_id": string, "x": point, "y": point, "text": ["type": "string", "maxLength": 20000], "replace": boolean, "submit": boolean
            ]) { _, new in new }, ["text"], readOnly: false),
            tool("press_key", "Background supports advertised AXConfirm/AXCancel/AXPress actions. Chords such as cmd+l are accepted. General keys and modifiers require foreground mode and exact window focus.", input.merging([
                "element_token": string, "key": string, "modifiers": ["type": "array", "items": string]
            ]) { _, new in new }, ["key"], readOnly: false),
            tool("scroll", "Background scroll uses a window's AX scrollbar. Coordinates are within its latest screenshot; amount counts app-defined AX steps. Foreground uses native wheel events.", input.merging([
                "element_token": string, "capture_id": string, "x": point, "y": point,
                "direction": ["type": "string", "enum": ["up", "down", "left", "right"]],
                "by": ["type": "string", "enum": ["line", "page"]], "amount": ["type": "integer", "minimum": 1, "maximum": 50]
            ]) { _, new in new }, ["direction"], readOnly: false)
        ]
    }

    /// Advertise a compact common path; legacy native arguments remain accepted by the gateway.
    static func publicTool(_ tool: [String: Any]) -> [String: Any] {
        guard let name = tool["name"] as? String,
              var schema = tool["inputSchema"] as? [String: Any],
              var properties = schema["properties"] as? [String: Any] else { return tool }
        let hidden: Set<String> = ["pid", "element_token", "target", "tree_format", "max_dimension", "max_elements", "max_depth", "timeout_ms", "include_accessibility_tree", "modifiers"]
        if name != "list_windows" { properties = properties.filter { !hidden.contains($0.key) } }
        var result = tool
        let descriptions = [
            "get_window_state": "Read window_id. Controls are [N]; detail=full expands them. Set include_screenshot=false for text only. Region uses image pixels and defaults to no image. Read before input; snapshot_id rejects stale controls.",
            "get_desktop_state": "Capture the primary screen. Use window_id=0 and foreground mode for desktop input; coordinates are returned image pixels.",
            "click": "Click element_index or screenshot x/y. Use capture_id for coordinates. Background uses AX; arbitrary pixels require locally authorized foreground mode.",
            "type_text": "Type into element_index or the focused field. replace replaces text; submit presses Enter. Plain text is hidden from the activity display.",
            "press_key": "Press a key or chord, e.g. cmd+l. General shortcuts require locally authorized foreground mode.",
            "scroll": "Scroll up/down/left/right; amount is AX steps or foreground wheel lines. Target element_index or screenshot x/y."
        ]
        if let description = descriptions[name] { result["description"] = description }
        if ["click", "type_text", "press_key", "scroll"].contains(name) {
            properties["window_id"] = ["type": "integer", "minimum": 0, "description": "Exact window ID; 0 is the primary desktop (foreground only)."]
            schema["required"] = Array(Set((schema["required"] as? [String] ?? []) + ["window_id"])).sorted()
            result["description"] = (result["description"] as? String ?? "") + " Window actions return fresh compact state; do not immediately reread or replay uncertain outcomes."
        }
        schema["properties"] = properties
        result["inputSchema"] = schema
        return result
    }
}
