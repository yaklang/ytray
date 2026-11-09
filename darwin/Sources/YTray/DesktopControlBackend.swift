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
    func stop()
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
                                    "delivery_mode": ["type": "string", "enum": ["background", "foreground"], "default": "background"]]
        func tool(_ name: String, _ description: String, _ properties: [String: Any], _ required: [String] = [], readOnly: Bool = true) -> [String: Any] {
            ["name": name, "description": description, "inputSchema": ["type": "object", "properties": properties,
                "required": required, "additionalProperties": false],
             "annotations": ["readOnlyHint": readOnly, "destructiveHint": !readOnly]]
        }
        return [
            tool("list_apps", "List running applications in the current user's macOS desktop.", [:]),
            tool("list_windows", "List CG windows with exact owner PID, window ID and screen-point bounds.", ["pid": integer, "on_screen_only": boolean]),
            tool("check_permissions", "Read YTray's actual Accessibility and Screen Recording preflight state. Does not prompt.", [:]),
            tool("get_window_state", "Observe one exact window. Tokens are scoped to the latest snapshot of that window and this session. Screenshot coordinates use the returned coordinate scale.", [
                "pid": integer, "window_id": integer, "query": string, "include_accessibility_tree": boolean, "include_screenshot": boolean,
                "max_elements": ["type": "integer", "minimum": 1, "maximum": 1000],
                "max_depth": ["type": "integer", "minimum": 1, "maximum": 32],
                "timeout_ms": ["type": "integer", "minimum": 100, "maximum": 3000],
                "max_dimension": ["type": "integer", "minimum": 100, "maximum": 4096],
                "max_image_dimension": ["type": "integer", "minimum": 100, "maximum": 4096],
                "tree_format": ["type": "string", "enum": ["markdown", "elements", "both"]]
            ], ["pid", "window_id"]),
            tool("get_desktop_state", "Capture the primary display using Apple's ScreenCaptureKit. Does not install or launch another runtime.", ["max_image_dimension": ["type": "integer", "minimum": 100, "maximum": 4096]]),
            tool("click", "Background: AXPress/AXShowMenu on the selected element. Pixel hit-testing can resolve an AX control. Arbitrary pixels require explicitly authorized foreground mode. Never escalates automatically.", input.merging([
                "element_token": string, "capture_id": string, "x": point, "y": point,
                "button": ["type": "string", "enum": ["left", "right"]], "count": ["type": "integer", "minimum": 1, "maximum": 2]
            ]) { _, new in new }, readOnly: false),
            tool("type_text", "Background inserts through AXSelectedText or a writable AX text value. Unsupported controls refuse; generic Unicode key events require foreground mode.", input.merging([
                "element_token": string, "x": point, "y": point, "text": ["type": "string", "maxLength": 20000]
            ]) { _, new in new }, ["text"], readOnly: false),
            tool("press_key", "Background supports advertised AXConfirm/AXCancel/AXPress actions. General keys and modifiers require foreground mode and exact window focus.", input.merging([
                "element_token": string, "key": string, "modifiers": ["type": "array", "items": string]
            ]) { _, new in new }, ["key"], readOnly: false),
            tool("scroll", "Background scroll uses a window's AX scrollbar. Coordinates are within its latest screenshot; amount counts app-defined AX steps. Foreground uses native wheel events.", input.merging([
                "element_token": string, "x": point, "y": point,
                "direction": ["type": "string", "enum": ["up", "down", "left", "right"]],
                "by": ["type": "string", "enum": ["line", "page"]], "amount": ["type": "integer", "minimum": 1, "maximum": 50]
            ]) { _, new in new }, ["direction"], readOnly: false)
        ]
    }
}
