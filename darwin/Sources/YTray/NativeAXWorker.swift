import AppKit
import ApplicationServices
import CoreGraphics
import Foundation

/// Only public AX/CG APIs are used. Window IDs are matched by PID and unique geometry/title;
/// an ambiguous mapping fails closed instead of guessing which window receives input.
enum NativeWindowMatcher {
    struct Candidate { let frame: CGRect; let title: String }
    static func index(frame: CGRect, title: String, candidates: [Candidate]) throws -> Int {
        let matches = candidates.indices.filter { i in
            let other = candidates[i]
            return abs(frame.minX - other.frame.minX) <= 1 && abs(frame.minY - other.frame.minY) <= 1
                && abs(frame.width - other.frame.width) <= 1 && abs(frame.height - other.frame.height) <= 1
                && (title.isEmpty || other.title == title)
        }
        guard matches.count == 1 else {
            throw DesktopOperationError(matches.isEmpty ? "window_unresolved" : "window_ambiguous",
                                        "无法通过 PID、位置、尺寸和标题唯一匹配 AX 窗口，请重新观察")
        }
        return matches[0]
    }
}

final class NativeAXWorker: @unchecked Sendable {
    struct Target: Hashable { let pid: pid_t; let windowID: CGWindowID }
    private struct Window { let target: Target; let frame: CGRect; let title: String; let element: AXUIElement }
    private struct Entry { let element: AXUIElement; let depth: Int; let parent: Int?; var data: [String: Any] }
    private struct Snapshot {
        let id: String; let window: Window; let createdAt: Date; let entries: [Entry]
        var scale: CGFloat = 1; var hasCapture = false
    }
    private let onAction: (@Sendable (DesktopActionCue, UUID) -> Void)?
    private let lease: DesktopSessionLease
    private let queue = DispatchQueue(label: "io.yaklang.ytray.native-ax", qos: .userInitiated)
    private var snapshots: [Target: Snapshot] = [:]
    private var operationGeneration: UUID?
    init(lease: DesktopSessionLease, onAction: (@Sendable (DesktopActionCue, UUID) -> Void)? = nil) { self.lease = lease; self.onAction = onAction }
    func clear() { queue.async { self.snapshots.removeAll() } }
    @discardableResult
    func run(_ generation: UUID, _ body: @escaping (NativeAXWorker) throws -> [String: Any]) async throws -> [String: Any] {
        try await withCheckedThrowingContinuation { continuation in
            queue.async {
                do {
                    try self.lease.check(generation)
                    self.operationGeneration = generation
                    defer { self.operationGeneration = nil }
                    let result = try body(self)
                    try self.lease.check(generation)
                    continuation.resume(returning: result)
                } catch { continuation.resume(throwing: error) }
            }
        }
    }

    static func integer(_ args: [String: Any], _ name: String, fallback: Int, range: ClosedRange<Int>) throws -> Int {
        guard let value = args[name] else { return fallback }
        guard let number = value as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID(),
              number.doubleValue.isFinite, number.doubleValue.rounded() == number.doubleValue,
              number.doubleValue >= Double(range.lowerBound), number.doubleValue <= Double(range.upperBound) else {
            throw DesktopOperationError("invalid_arguments", "\(name) 必须为 \(range) 范围内的整数")
        }
        return number.intValue
    }
    static func positiveInt(_ args: [String: Any], _ name: String) throws -> Int {
        guard args[name] != nil else { throw DesktopOperationError("invalid_arguments", "缺少 \(name)") }
        return try integer(args, name, fallback: 0, range: 1...Int(Int32.max))
    }
    static func bool(_ args: [String: Any], _ name: String, fallback: Bool) throws -> Bool {
        guard let value = args[name] else { return fallback }
        guard let number = value as? NSNumber, CFGetTypeID(number) == CFBooleanGetTypeID() else {
            throw DesktopOperationError("invalid_arguments", "\(name) 必须为布尔值")
        }
        return number.boolValue
    }
    static func windowTarget(_ args: [String: Any]) throws -> Target {
        if let target = args["target"] as? [String: Any] {
            guard target["kind"] as? String == "window" else { throw DesktopOperationError("invalid_arguments", "需要窗口目标") }
            let result = Target(pid: pid_t(try positiveInt(target, "pid")), windowID: CGWindowID(try positiveInt(target, "window_id")))
            if args["pid"] != nil || args["window_id"] != nil {
                guard try positiveInt(args, "pid") == Int(result.pid), try positiveInt(args, "window_id") == Int(result.windowID) else {
                    throw DesktopOperationError("invalid_arguments", "target 与顶层窗口参数冲突")
                }
            }
            return result
        }
        return Target(pid: pid_t(try positiveInt(args, "pid")), windowID: CGWindowID(try positiveInt(args, "window_id")))
    }
    static func validate(_ value: Any, schema: [String: Any], path: String = "arguments") throws {
        func invalid() -> DesktopOperationError { DesktopOperationError("invalid_arguments", "\(path) 不符合工具参数定义") }
        if let choices = schema["enum"] as? [String] { guard let s = value as? String, choices.contains(s) else { throw invalid() } }
        switch schema["type"] as? String {
        case "object":
            guard let object = value as? [String: Any] else { throw invalid() }
            let properties = schema["properties"] as? [String: Any] ?? [:]
            if schema["additionalProperties"] as? Bool == false && !Set(object.keys).isSubset(of: Set(properties.keys)) { throw invalid() }
            for required in schema["required"] as? [String] ?? [] where object[required] == nil { throw invalid() }
            for (key, child) in object { if let definition = properties[key] as? [String: Any] { try validate(child, schema: definition, path: "\(path).\(key)") } }
        case "string":
            guard let s = value as? String, s.utf16.count <= (schema["maxLength"] as? Int ?? 100_000) else { throw invalid() }
        case "boolean":
            guard let n = value as? NSNumber, CFGetTypeID(n) == CFBooleanGetTypeID() else { throw invalid() }
        case "integer", "number":
            guard let n = value as? NSNumber, CFGetTypeID(n) != CFBooleanGetTypeID(), n.doubleValue.isFinite else { throw invalid() }
            if schema["type"] as? String == "integer" && n.doubleValue.rounded() != n.doubleValue { throw invalid() }
            if let min = (schema["minimum"] as? NSNumber)?.doubleValue, n.doubleValue < min { throw invalid() }
            if let max = (schema["maximum"] as? NSNumber)?.doubleValue, n.doubleValue > max { throw invalid() }
        case "array":
            guard let array = value as? [Any], array.count <= 20 else { throw invalid() }
            if let item = schema["items"] as? [String: Any] { for child in array { try validate(child, schema: item, path: path + "[]") } }
        default: break
        }
    }

    // AX optimizes requests to our own PID into direct AppKit calls on the calling thread.
    // Those must run on the main thread. External applications remain on the AX queue.
    private func onAXThread<T>(_ element: AXUIElement, _ body: () throws -> T) rethrows -> T {
        var pid: pid_t = 0
        AXUIElementGetPid(element, &pid)
        if pid == ProcessInfo.processInfo.processIdentifier && !Thread.isMainThread {
            return try DispatchQueue.main.sync(execute: body)
        }
        return try body()
    }
    private func writeAX<T>(_ element: AXUIElement, _ body: () -> T) throws -> T {
        let generation = operationGeneration
        return try onAXThread(element) {
            guard let generation else { throw DesktopOperationError("session_ended", "没有可用的输入会话") }
            try lease.check(generation)
            AXUIElementSetMessagingTimeout(element, 1.5)
            return body()
        }
    }

    private func copy(_ element: AXUIElement, _ name: String) -> CFTypeRef? {
        var value: CFTypeRef?
        return onAXThread(element) { AXUIElementCopyAttributeValue(element, name as CFString, &value) } == .success ? value : nil
    }
    private func string(_ element: AXUIElement, _ name: String) -> String { copy(element, name) as? String ?? "" }
    private func elements(_ element: AXUIElement, _ name: String) -> [AXUIElement] { copy(element, name) as? [AXUIElement] ?? [] }
    private func element(_ source: AXUIElement, _ name: String) -> AXUIElement? {
        guard let v = copy(source, name), CFGetTypeID(v) == AXUIElementGetTypeID() else { return nil }
        return (v as! AXUIElement)
    }
    private func frame(_ element: AXUIElement) -> CGRect? {
        guard let p = copy(element, kAXPositionAttribute), CFGetTypeID(p) == AXValueGetTypeID(),
              let s = copy(element, kAXSizeAttribute), CFGetTypeID(s) == AXValueGetTypeID() else { return nil }
        var point = CGPoint.zero, size = CGSize.zero
        guard AXValueGetValue(p as! AXValue, .cgPoint, &point), AXValueGetValue(s as! AXValue, .cgSize, &size) else { return nil }
        return CGRect(origin: point, size: size)
    }
    private func actions(_ element: AXUIElement) -> [String] {
        var names: CFArray?
        guard onAXThread(element, { AXUIElementCopyActionNames(element, &names) }) == .success else { return [] }
        return names as? [String] ?? []
    }
    private func rows(onScreen: Bool) -> [[String: Any]] {
        CGWindowListCopyWindowInfo(onScreen ? [.optionOnScreenOnly, .excludeDesktopElements] : [.optionAll, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] ?? []
    }
    private func rect(_ row: [String: Any]) -> CGRect? {
        guard let bounds = row[kCGWindowBounds as String] as? [String: Any] else { return nil }
        return CGRect(dictionaryRepresentation: bounds as CFDictionary)
    }
    func listWindows(_ args: [String: Any]) throws -> [String: Any] {
        let pid = try args["pid"].map { _ in try Self.positiveInt(args, "pid") }
        let onScreen = try Self.bool(args, "on_screen_only", fallback: true)
        let windows: [[String: Any]] = rows(onScreen: onScreen).compactMap { row in
            guard let owner = row[kCGWindowOwnerPID as String] as? Int, pid == nil || pid == owner,
                  let number = row[kCGWindowNumber as String] as? Int, let frame = rect(row), frame.width > 0, frame.height > 0,
                  row[kCGWindowLayer as String] as? Int == 0 else { return nil }
            return ["pid": owner, "window_id": number, "title": row[kCGWindowName as String] as? String ?? "",
                    "app_name": row[kCGWindowOwnerName as String] as? String ?? "", "bounds": Self.bounds(frame),
                    "on_screen": row[kCGWindowIsOnscreen as String] as? Bool ?? false]
        }
        return SwiftDesktopBackend.result("Found \(windows.count) windows", ["windows": windows, "backend": "swift-native"])
    }
    private func resolve(_ target: Target) throws -> Window {
        guard AXIsProcessTrusted() else { throw DesktopOperationError("permission_denied", "缺少辅助功能权限") }
        guard let row = rows(onScreen: false).first(where: { ($0[kCGWindowNumber as String] as? Int) == Int(target.windowID)
            && ($0[kCGWindowOwnerPID as String] as? Int) == Int(target.pid) }), let cgFrame = rect(row) else {
            throw DesktopOperationError("window_unavailable", "此 PID 不拥有指定的 CG 窗口")
        }
        let application = AXUIElementCreateApplication(target.pid)
        AXUIElementSetMessagingTimeout(application, 0.25)
        let windows = elements(application, kAXWindowsAttribute)
        var candidates: [NativeWindowMatcher.Candidate] = [], valid: [AXUIElement] = []
        for window in windows {
            AXUIElementSetMessagingTimeout(window, 0.25)
            guard let f = frame(window) else { continue }
            valid.append(window); candidates.append(.init(frame: f, title: string(window, kAXTitleAttribute)))
        }
        let title = row[kCGWindowName as String] as? String ?? ""
        let index = try NativeWindowMatcher.index(frame: cgFrame, title: title, candidates: candidates)
        return Window(target: target, frame: cgFrame, title: candidates[index].title, element: valid[index])
    }
    private static func bounds(_ frame: CGRect) -> [String: Double] { ["x": Double(frame.minX), "y": Double(frame.minY), "width": Double(frame.width), "height": Double(frame.height)] }
    func observe(_ args: [String: Any], generation: UUID) throws -> [String: Any] {
        let target = try Self.windowTarget(args)
        let window = try resolve(target)
        let maximum = try Self.integer(args, "max_elements", fallback: 500, range: 1...1000)
        let depthLimit = try Self.integer(args, "max_depth", fallback: 16, range: 1...32)
        let timeout = try Self.integer(args, "timeout_ms", fallback: 2000, range: 100...3000)
        let includeTree = try Self.bool(args, "include_accessibility_tree", fallback: true)
        let id = "s" + UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
        let deadline = ProcessInfo.processInfo.systemUptime + Double(timeout) / 1000
        var entries: [Entry] = [], seen = Set<CFHashCode>(), truncated = false, errors: [String] = []
        let names = [kAXRoleAttribute, kAXSubroleAttribute, kAXTitleAttribute, kAXDescriptionAttribute, kAXValueAttribute,
                     kAXHelpAttribute, kAXIdentifierAttribute, kAXEnabledAttribute, kAXFocusedAttribute, kAXChildrenAttribute,
                     kAXPositionAttribute, kAXSizeAttribute]
        func visit(_ node: AXUIElement, depth: Int, parent: Int?) throws {
            try lease.check(generation)
            guard entries.count < maximum, ProcessInfo.processInfo.systemUptime < deadline else { truncated = true; return }
            // AX refs can reappear in group hierarchies; equality, not just hash, avoids dropping hash collisions.
            let hash = CFHash(node)
            if seen.contains(hash) && entries.contains(where: { CFEqual($0.element, node) }) { return }
            seen.insert(hash)
            AXUIElementSetMessagingTimeout(node, 0.10)
            var raw: CFArray?
            let status = onAXThread(node) { AXUIElementCopyMultipleAttributeValues(node, names as CFArray, [], &raw) }
            guard status == .success, let values = raw as? [Any], values.count == names.count else {
                errors.append("AX attribute read failed: \(status.rawValue)"); return
            }
            let valuesByName = Dictionary(uniqueKeysWithValues: zip(names, values))
            let role = valuesByName[kAXRoleAttribute] as? String ?? "AXUnknown"
            let title = valuesByName[kAXTitleAttribute] as? String ?? ""
            let description = valuesByName[kAXDescriptionAttribute] as? String ?? ""
            let identifier = valuesByName[kAXIdentifierAttribute] as? String ?? ""
            let secure = (valuesByName[kAXSubroleAttribute] as? String ?? "").contains("Secure")
            let row = entries.count
            var data: [String: Any] = ["role": role, "label": title.isEmpty ? description : title, "depth": depth,
                "element_token": "\(id):\(row)", "actions": actions(node)]
            if !identifier.isEmpty { data["identifier"] = identifier }
            if let subrole = valuesByName[kAXSubroleAttribute] as? String, !subrole.isEmpty { data["subrole"] = subrole }
            if let parent { data["parent_index"] = parent }
            if let v = valuesByName[kAXEnabledAttribute] as? Bool { data["enabled"] = v }
            if let v = valuesByName[kAXFocusedAttribute] as? Bool { data["focused"] = v }
            if secure { data["value"] = "[secure]" }
            else if let v = valuesByName[kAXValueAttribute] as? String { data["value"] = String(v.prefix(8000)) }
            else if let v = valuesByName[kAXValueAttribute] as? NSNumber { data["value"] = v }
            if let position = valuesByName[kAXPositionAttribute], CFGetTypeID(position as CFTypeRef) == AXValueGetTypeID(),
               let size = valuesByName[kAXSizeAttribute], CFGetTypeID(size as CFTypeRef) == AXValueGetTypeID() {
                var p = CGPoint.zero, s = CGSize.zero
                if AXValueGetValue(position as! AXValue, .cgPoint, &p), AXValueGetValue(size as! AXValue, .cgSize, &s) {
                    data["bounds"] = Self.bounds(CGRect(x: p.x - window.frame.minX, y: p.y - window.frame.minY, width: s.width, height: s.height))
                }
            }
            entries.append(Entry(element: node, depth: depth, parent: parent, data: data))
            let children = valuesByName[kAXChildrenAttribute] as? [AXUIElement] ?? []
            if depth >= depthLimit { if !children.isEmpty { truncated = true }; return }
            for child in children {
                if entries.count >= maximum || ProcessInfo.processInfo.systemUptime >= deadline { truncated = true; break }
                try visit(child, depth: depth + 1, parent: row)
            }
        }
        if includeTree { try visit(window.element, depth: 0, parent: nil) }
        try lease.check(generation)
        snapshots[target] = Snapshot(id: id, window: window, createdAt: Date(), entries: entries)
        while snapshots.count > 16, let oldest = snapshots.min(by: { $0.value.createdAt < $1.value.createdAt })?.key { snapshots.removeValue(forKey: oldest) }
        let query = (args["query"] as? String ?? "").lowercased()
        let visible = entries.filter { query.isEmpty || String(describing: $0.data).lowercased().contains(query) }
        var structured: [String: Any] = ["pid": Int(target.pid), "window_id": Int(target.windowID), "snapshot_id": id,
            "backend": "swift-native", "elements": visible.map(\.data), "truncated": truncated, "degraded": !errors.isEmpty,
            "ax_errors": errors, "coordinate_scale": 1, "screenshot_frame_valid": false,
            "window_bounds": Self.bounds(window.frame), "token_ttl_seconds": 60]
        let tree = visible.map { entry -> String in
            let row = entry.data["element_token"] as? String ?? ""
            let label = (entry.data["label"] as? String ?? "").debugDescription
            let value = entry.data["value"].map { " value=" + String(describing: $0).debugDescription } ?? ""
            return String(repeating: "  ", count: entry.depth) + "- [\(row)] \(entry.data["role"] ?? "") \(label)\(value) actions=\(entry.data["actions"] ?? [])"
        }.joined(separator: "\n")
        if args["tree_format"] as? String == "markdown" { structured.removeValue(forKey: "elements") }
        let text = "window_id=\(target.windowID) pid=\(target.pid) size=\(Int(window.frame.width))x\(Int(window.frame.height)) elements=\(entries.count) snapshot_id=\(id) backend=swift-native\n" + tree
        return SwiftDesktopBackend.result(text, structured)
    }
    func setCapture(_ id: String, scale: CGFloat, frame: CGRect) throws {
        guard let target = snapshots.first(where: { $0.value.id == id })?.key,
              snapshots[target]?.window.frame == frame else {
            throw DesktopOperationError("capture_changed", "AX 观察和截图的窗口范围不同，请重新观察")
        }
        snapshots[target]?.hasCapture = true; snapshots[target]?.scale = scale
    }
    func raiseWindow(_ target: Target, tool: String, args: [String: Any]) throws {
        let snapshot = try fresh(target)
        if tool == "click" || tool == "scroll" || args["element_token"] != nil || args["x"] != nil || args["y"] != nil {
            if args["element_token"] != nil { _ = try selected(args, snapshot: snapshot) }
            else { _ = try point(args, snapshot: snapshot) }
        }
        if tool == "press_key", Self.keyCodes[(args["key"] as? String ?? "").lowercased()] == nil {
            throw DesktopOperationError("unsupported_key", "不支持的键名")
        }
        if tool == "scroll", (args["by"] as? String ?? "line") != "line" {
            throw DesktopOperationError("unsupported_scroll_unit", "前台滚轮只接受 line")
        }
        if writable(snapshot.window.element, kAXMainAttribute) { try set(snapshot.window.element, kAXMainAttribute, kCFBooleanTrue) }
        try perform(snapshot.window.element, kAXRaiseAction)
    }
    private func fresh(_ target: Target) throws -> Snapshot {
        guard let snapshot = snapshots[target], Date().timeIntervalSince(snapshot.createdAt) <= 60 else {
            throw DesktopOperationError("stale_observation", "请先 get_window_state 观察此窗口；观察有效期为 60 秒")
        }
        let current = try resolve(target)
        guard CFEqual(current.element, snapshot.window.element), current.frame == snapshot.window.frame else {
            snapshots.removeValue(forKey: target)
            throw DesktopOperationError("stale_observation", "窗口已改变，请重新观察")
        }
        return snapshot
    }
    private func belongs(_ element: AXUIElement, to window: AXUIElement) -> Bool {
        if CFEqual(element, window) { return true }
        if let root = self.element(element, kAXWindowAttribute) { return CFEqual(root, window) }
        var current = element
        for _ in 0..<64 {
            guard let parent = self.element(current, kAXParentAttribute), !CFEqual(parent, current) else { return false }
            if CFEqual(parent, window) { return true }; current = parent
        }
        return false
    }
    private func point(_ args: [String: Any], snapshot: Snapshot) throws -> CGPoint {
        guard snapshot.hasCapture, let x = args["x"] as? NSNumber, let y = args["y"] as? NSNumber else {
            throw DesktopOperationError("capture_required", "坐标操作需要本窗口的最新成功截图以及 x/y")
        }
        if let id = args["capture_id"] as? String, id != snapshot.id { throw DesktopOperationError("stale_capture", "截图标识已过期") }
        let p = CGPoint(x: x.doubleValue / Double(snapshot.scale), y: y.doubleValue / Double(snapshot.scale))
        guard p.x >= 0, p.y >= 0, p.x < snapshot.window.frame.width, p.y < snapshot.window.frame.height else {
            throw DesktopOperationError("invalid_coordinates", "坐标超出目标窗口截图")
        }
        return CGPoint(x: snapshot.window.frame.minX + p.x, y: snapshot.window.frame.minY + p.y)
    }
    private func selected(_ args: [String: Any], snapshot: Snapshot) throws -> AXUIElement {
        if let token = args["element_token"] as? String {
            let prefix = snapshot.id + ":"
            guard token.hasPrefix(prefix), let index = Int(token.dropFirst(prefix.count)), snapshot.entries.indices.contains(index) else {
                throw DesktopOperationError("invalid_element_token", "元素令牌不属于此窗口的最新观察")
            }
            let node = snapshot.entries[index].element
            guard belongs(node, to: snapshot.window.element) else { throw DesktopOperationError("stale_element", "元素已离开目标窗口") }
            if let id = args["capture_id"] as? String, id != snapshot.id { throw DesktopOperationError("stale_capture", "截图标识已过期") }
            return node
        }
        if args["x"] != nil || args["y"] != nil {
            let p = try point(args, snapshot: snapshot)
            var found: AXUIElement?
            let application = AXUIElementCreateApplication(snapshot.window.target.pid)
            AXUIElementSetMessagingTimeout(application, 0.25)
            let error = onAXThread(application) { AXUIElementCopyElementAtPosition(application, Float(p.x), Float(p.y), &found) }
            guard error == .success, let found, belongs(found, to: snapshot.window.element) else {
                throw DesktopOperationError("hit_test_failed", "目标窗口在该位置没有可确定归属的 AX 元素（AXError \(error.rawValue)）")
            }
            return found
        }
        let application = AXUIElementCreateApplication(snapshot.window.target.pid)
        guard let focused = element(application, kAXFocusedUIElementAttribute), belongs(focused, to: snapshot.window.element) else {
            throw DesktopOperationError("element_required", "请提供元素令牌或截图坐标；此窗口没有可用的焦点元素")
        }
        return focused
    }
    private func perform(_ element: AXUIElement, _ action: String) throws {
        guard actions(element).contains(action) else { throw DesktopOperationError("background_unavailable", "控件未提供 \(action)；不会自动改用前台输入") }
        let status = try writeAX(element) { AXUIElementPerformAction(element, action as CFString) }
        guard status == .success else { throw DesktopOperationError("ax_action_failed", "\(action) 返回 AXError \(status.rawValue)，结果可能未知，请重新观察") }
    }
    private func writable(_ element: AXUIElement, _ name: String) -> Bool {
        var writable: DarwinBoolean = false
        return onAXThread(element) { AXUIElementIsAttributeSettable(element, name as CFString, &writable) } == .success && writable.boolValue
    }
    private func set(_ element: AXUIElement, _ name: String, _ value: CFTypeRef) throws {
        let status = try writeAX(element) { AXUIElementSetAttributeValue(element, name as CFString, value) }
        guard status == .success else { throw DesktopOperationError("ax_write_failed", "\(name) 返回 AXError \(status.rawValue)，请重新观察") }
    }
    private func showCursor(_ kind: DesktopActionCue.Kind, at point: CGPoint, generation: UUID) throws {
        guard let onAction, point.x.isFinite, point.y.isFinite else { return }
        try lease.check(generation)
        onAction(DesktopActionCue(point: point, kind: kind), generation)
        // Let the nonactivating overlay render and reach the target before input is admitted.
        // This runs only on the AX queue; the application main thread stays responsive.
        Thread.sleep(forTimeInterval: 0.14)
        try lease.check(generation)
    }
    private func indicate(_ kind: DesktopActionCue.Kind, args: [String: Any], snapshot: Snapshot, node: AXUIElement, generation: UUID) throws {
        if args["element_token"] == nil && (args["x"] != nil || args["y"] != nil) {
            try showCursor(kind, at: point(args, snapshot: snapshot), generation: generation)
            return
        }
        var current = node
        for _ in 0..<32 {
            if let rect = frame(current), let p = DesktopCursorGeometry.visibleCenter(of: rect, in: snapshot.window.frame) {
                try showCursor(kind, at: p, generation: generation); return
            }
            guard let parent = element(current, kAXParentAttribute), !CFEqual(parent, current), belongs(parent, to: snapshot.window.element) else { return }
            current = parent
        }
    }
    func act(_ name: String, args: [String: Any], generation: UUID,
             desktopCapture: (id: String, date: Date, scale: CGFloat, frame: CGRect)?) throws -> [String: Any] {
        try lease.check(generation)
        let foreground = args["delivery_mode"] as? String == "foreground"
        let desktop = (args["target"] as? [String: Any])?["kind"] as? String == "desktop"
        var snapshot: Snapshot?
        if !desktop { snapshot = try fresh(Self.windowTarget(args)) }
        if foreground {
            let frame: CGRect, scale: CGFloat
            if let snapshot {
                try verifyFocus(snapshot.window, generation: generation)
                frame = snapshot.window.frame; scale = snapshot.scale
            } else {
                guard desktop, let capture = desktopCapture, Date().timeIntervalSince(capture.date) <= 60 else { throw DesktopOperationError("capture_required", "请先截取主显示器") }
                frame = capture.frame; scale = capture.scale
                if let id = args["capture_id"] as? String, id != capture.id { throw DesktopOperationError("stale_capture", "桌面截图已过期") }
            }
            return try foregroundInput(name, args: args, snapshot: snapshot, frame: frame, scale: scale, generation: generation)
        }
        guard let snapshot else { throw DesktopOperationError("background_unavailable", "后台输入必须指定窗口") }
        let node = try selected(args, snapshot: snapshot)
        try lease.check(generation)
        var mechanism = ""
        switch name {
        case "click":
            guard try Self.integer(args, "count", fallback: 1, range: 1...2) == 1 else { throw DesktopOperationError("background_unavailable", "AX 不提供通用后台双击") }
            let action = args["button"] as? String == "right" ? kAXShowMenuAction : kAXPressAction
            if actions(node).contains(action) { try indicate(.click, args: args, snapshot: snapshot, node: node, generation: generation) }
            try perform(node, action); mechanism = action
        case "type_text":
            guard let text = args["text"] as? String else { throw DesktopOperationError("invalid_arguments", "缺少 text") }
            guard !string(node, kAXSubroleAttribute).contains("Secure") else { throw DesktopOperationError("secure_input", "安全输入控件不支持后台写入") }
            if writable(node, kAXSelectedTextAttribute) {
                if !text.isEmpty { try indicate(.type, args: args, snapshot: snapshot, node: node, generation: generation) }
                try set(node, kAXSelectedTextAttribute, text as CFString); mechanism = kAXSelectedTextAttribute
            } else if writable(node, kAXValueAttribute), let current = copy(node, kAXValueAttribute) as? String {
                let original = current as NSString
                var range = CFRange(location: original.length, length: 0)
                if let selected = copy(node, kAXSelectedTextRangeAttribute), CFGetTypeID(selected) == AXValueGetTypeID() {
                    _ = AXValueGetValue(selected as! AXValue, .cfRange, &range)
                }
                guard range.location >= 0, range.length >= 0, range.location <= original.length, range.length <= original.length - range.location else { throw DesktopOperationError("invalid_selection", "文本选择范围已改变") }
                let replacement = original.replacingCharacters(in: NSRange(location: range.location, length: range.length), with: text)
                if !text.isEmpty { try indicate(.type, args: args, snapshot: snapshot, node: node, generation: generation) }
                try set(node, kAXValueAttribute, replacement as CFString)
                if writable(node, kAXSelectedTextRangeAttribute) {
                    var after = CFRange(location: range.location + text.utf16.count, length: 0)
                    if let value = AXValueCreate(.cfRange, &after) { try lease.check(generation); try set(node, kAXSelectedTextRangeAttribute, value) }
                }
                mechanism = kAXValueAttribute
            } else { throw DesktopOperationError("background_unavailable", "此控件没有可写的 AX 文本属性") }
        case "press_key":
            guard (args["modifiers"] as? [String] ?? []).isEmpty else { throw DesktopOperationError("background_unavailable", "组合键需要明确授权前台输入") }
            let key = (args["key"] as? String ?? "").lowercased()
            let action: String
            switch key { case "return", "enter": action = kAXConfirmAction; case "escape", "esc": action = kAXCancelAction
            case "space", " ": action = kAXPressAction; default: throw DesktopOperationError("background_unavailable", "该按键没有通用 AX 动作，需要前台输入") }
            if actions(node).contains(action) { try indicate(.key, args: args, snapshot: snapshot, node: node, generation: generation) }
            try perform(node, action); mechanism = action
        case "scroll":
            mechanism = try scroll(node, args: args, snapshot: snapshot, generation: generation)
        default: throw DesktopOperationError("unknown_tool", "不支持的输入工具")
        }
        try lease.check(generation)
        return SwiftDesktopBackend.result("\(name) delivered via \(mechanism). Observe again to verify the application result.",
            ["backend": "swift-native", "delivery_mode": "background", "mechanism": mechanism, "delivered": true, "verification": "observe_required"])
    }
    private func scroll(_ node: AXUIElement, args: [String: Any], snapshot: Snapshot, generation: UUID) throws -> String {
        let direction = args["direction"] as? String ?? "down"
        let vertical = direction == "up" || direction == "down"
        let positive = direction == "down" || direction == "right"
        let amount = try Self.integer(args, "amount", fallback: 1, range: 1...50)
        var current = node
        for _ in 0..<64 {
            if string(current, kAXRoleAttribute) == kAXScrollAreaRole { break }
            guard let parent = element(current, kAXParentAttribute), !CFEqual(parent, current), belongs(parent, to: snapshot.window.element) else {
                throw DesktopOperationError("background_unavailable", "所选控件没有属于此窗口的 AX 滚动容器")
            }
            current = parent
        }
        guard string(current, kAXRoleAttribute) == kAXScrollAreaRole else { throw DesktopOperationError("background_unavailable", "找不到 AX 滚动容器") }
        if args["by"] as? String == "page" {
            // AppKit's scroll-container actions can use content-motion direction rather than
            // viewport direction. Prefer the scrollbar's explicit increment/decrement page.
            if let bar = element(current, vertical ? kAXVerticalScrollBarAttribute : kAXHorizontalScrollBarAttribute) {
                let pageRole = positive ? kAXIncrementPageSubrole as String : kAXDecrementPageSubrole as String
                let pages = elements(bar, kAXChildrenAttribute).filter { string($0, kAXSubroleAttribute) == pageRole }
                if pages.count == 1, let page = pages.first, actions(page).contains(kAXPressAction) {
                    try indicate(.scroll, args: args, snapshot: snapshot, node: current, generation: generation)
                    for _ in 0..<amount { try lease.check(generation); try perform(page, kAXPressAction) }
                    return "\(pageRole).AXPress"
                }
            }
            let action = vertical ? (positive ? "AXScrollDownByPage" : "AXScrollUpByPage") : (positive ? "AXScrollRightByPage" : "AXScrollLeftByPage")
            if actions(current).contains(action) { try indicate(.scroll, args: args, snapshot: snapshot, node: current, generation: generation) }
            for _ in 0..<amount { try lease.check(generation); try perform(current, action) }
            return action
        }
        guard let bar = element(current, vertical ? kAXVerticalScrollBarAttribute : kAXHorizontalScrollBarAttribute) else {
            throw DesktopOperationError("background_unavailable", "滚动容器没有 AX 滚动条")
        }
        let action = positive ? kAXIncrementAction : kAXDecrementAction
        if actions(bar).contains(action) {
            try indicate(.scroll, args: args, snapshot: snapshot, node: current, generation: generation)
            for _ in 0..<amount { try lease.check(generation); try perform(bar, action) }
            return action
        }
        if let button = element(bar, positive ? kAXIncrementButtonAttribute as String : kAXDecrementButtonAttribute as String), actions(button).contains(kAXPressAction) {
            try indicate(.scroll, args: args, snapshot: snapshot, node: current, generation: generation)
            for _ in 0..<amount { try lease.check(generation); try perform(button, kAXPressAction) }
            return "AXScrollBar.\(positive ? "increment" : "decrement").AXPress"
        }
        let subrole = positive ? kAXIncrementArrowSubrole as String : kAXDecrementArrowSubrole as String
        let arrows = elements(bar, kAXChildrenAttribute).filter { string($0, kAXSubroleAttribute) == subrole }
        if arrows.count == 1, let arrow = arrows.first, actions(arrow).contains(kAXPressAction) {
            try indicate(.scroll, args: args, snapshot: snapshot, node: current, generation: generation)
            for _ in 0..<amount { try lease.check(generation); try perform(arrow, kAXPressAction) }
            return "\(subrole).AXPress"
        }
        throw DesktopOperationError("background_unavailable", "滚动条没有提供 AX 步进动作，需要前台滚轮输入")
    }
    private func verifyFocus(_ window: Window, generation: UUID) throws {
        let application = AXUIElementCreateApplication(window.target.pid)
        for _ in 0..<20 {
            try lease.check(generation)
            if copy(application, kAXFrontmostAttribute) as? Bool == true,
               let focused = element(application, kAXFocusedWindowAttribute), CFEqual(focused, window.element) { return }
            Thread.sleep(forTimeInterval: 0.025)
        }
        throw DesktopOperationError("focus_unverified", "不能确认目标窗口已获得前台焦点，拒绝发送事件")
    }
    private func foregroundInput(_ name: String, args: [String: Any], snapshot: Snapshot?, frame: CGRect, scale: CGFloat, generation: UUID) throws -> [String: Any] {
        func post(_ event: CGEvent?) throws {
            guard let event else { throw DesktopOperationError("event_creation_failed", "无法创建系统输入事件") }
            try lease.check(generation)
            if let snapshot { try verifyFocus(snapshot.window, generation: generation) }
            try lease.check(generation)
            event.post(tap: .cghidEventTap)
        }
        func stroke(_ down: CGEvent?, _ up: CGEvent?) throws {
            guard let down, let up else { throw DesktopOperationError("event_creation_failed", "无法创建完整的输入手势") }
            try post(down)
            up.post(tap: .cghidEventTap) // Complete the admitted gesture even if a stop arrives between down/up.
        }
        func location() throws -> CGPoint {
            if let snapshot {
                if args["element_token"] != nil {
                    let node = try selected(args, snapshot: snapshot)
                    guard let f = self.frame(node), frame.intersects(f) else { throw DesktopOperationError("element_offscreen", "控件没有位于窗口内的有效位置") }
                    let visible = frame.intersection(f)
                    return CGPoint(x: visible.midX, y: visible.midY)
                }
                return try point(args, snapshot: snapshot)
            }
            guard let x = args["x"] as? NSNumber, let y = args["y"] as? NSNumber else { throw DesktopOperationError("invalid_coordinates", "桌面输入需要 x/y") }
            let p = CGPoint(x: x.doubleValue / Double(scale), y: y.doubleValue / Double(scale))
            guard p.x >= 0, p.y >= 0, p.x < frame.width, p.y < frame.height else { throw DesktopOperationError("invalid_coordinates", "坐标超出主显示器") }
            return CGPoint(x: frame.minX + p.x, y: frame.minY + p.y)
        }
        func keyboardPoint() -> CGPoint {
            let source = snapshot.map { AXUIElementCreateApplication($0.window.target.pid) } ?? AXUIElementCreateSystemWide()
            if let focused = element(source, kAXFocusedUIElementAttribute),
               let rect = self.frame(focused), let p = DesktopCursorGeometry.visibleCenter(of: rect, in: frame) { return p }
            return CGPoint(x: frame.midX, y: frame.midY)
        }
        switch name {
        case "click":
            let p = try location(), right = args["button"] as? String == "right"
            let count = try Self.integer(args, "count", fallback: 1, range: 1...2)
            try showCursor(.click, at: p, generation: generation)
            for i in 1...count {
                let down = CGEvent(mouseEventSource: nil, mouseType: right ? .rightMouseDown : .leftMouseDown, mouseCursorPosition: p, mouseButton: right ? .right : .left)
                let up = CGEvent(mouseEventSource: nil, mouseType: right ? .rightMouseUp : .leftMouseUp, mouseCursorPosition: p, mouseButton: right ? .right : .left)
                down?.setIntegerValueField(.mouseEventClickState, value: Int64(i))
                up?.setIntegerValueField(.mouseEventClickState, value: Int64(i))
                try stroke(down, up)
            }
        case "type_text":
            guard let text = args["text"] as? String else { throw DesktopOperationError("invalid_arguments", "缺少 text") }
            if args["element_token"] != nil || args["x"] != nil || args["y"] != nil {
                let p = try location()
                if !text.isEmpty { try showCursor(.type, at: p, generation: generation) }
                try stroke(CGEvent(mouseEventSource: nil, mouseType: .leftMouseDown, mouseCursorPosition: p, mouseButton: .left),
                           CGEvent(mouseEventSource: nil, mouseType: .leftMouseUp, mouseCursorPosition: p, mouseButton: .left))
            } else if !text.isEmpty { try showCursor(.type, at: keyboardPoint(), generation: generation) }
            // Keep composed characters together and avoid splitting UTF-16 surrogate pairs.
            for character in text {
                let units = Array(String(character).utf16)
                let down = CGEvent(keyboardEventSource: nil, virtualKey: 0, keyDown: true)
                let up = CGEvent(keyboardEventSource: nil, virtualKey: 0, keyDown: false)
                units.withUnsafeBufferPointer { buffer in
                    down?.keyboardSetUnicodeString(stringLength: buffer.count, unicodeString: buffer.baseAddress!)
                    up?.keyboardSetUnicodeString(stringLength: buffer.count, unicodeString: buffer.baseAddress!)
                }
                try stroke(down, up)
            }
        case "press_key":
            if let snapshot, args["element_token"] != nil {
                let node = try selected(args, snapshot: snapshot)
                let application = AXUIElementCreateApplication(snapshot.window.target.pid)
                guard let focused = element(application, kAXFocusedUIElementAttribute), CFEqual(node, focused) else {
                    throw DesktopOperationError("focus_unverified", "指定控件并非当前焦点，请先点击并重新观察")
                }
            }
            let key = (args["key"] as? String ?? "").lowercased()
            guard let code = Self.keyCodes[key] else { throw DesktopOperationError("unsupported_key", "不支持的键名 \(key)") }
            var flags: CGEventFlags = []
            for modifier in args["modifiers"] as? [String] ?? [] {
                switch modifier.lowercased() {
                case "command", "cmd", "meta": flags.insert(.maskCommand); case "control", "ctrl": flags.insert(.maskControl)
                case "option", "alt": flags.insert(.maskAlternate); case "shift": flags.insert(.maskShift)
                default: throw DesktopOperationError("unsupported_modifier", "不支持的修饰键")
                }
            }
            try showCursor(.key, at: keyboardPoint(), generation: generation)
            let down = CGEvent(keyboardEventSource: nil, virtualKey: code, keyDown: true)
            let up = CGEvent(keyboardEventSource: nil, virtualKey: code, keyDown: false)
            down?.flags = flags; up?.flags = flags
            try stroke(down, up)
        case "scroll":
            let p = try location()
            guard (args["by"] as? String ?? "line") == "line" else { throw DesktopOperationError("unsupported_scroll_unit", "原生滚轮只接受 line；page 请使用应用的 AX 翻页动作") }
            let amount = try Self.integer(args, "amount", fallback: 1, range: 1...50)
            let direction = args["direction"] as? String ?? "down"
            let vertical = direction == "up" || direction == "down"
            let delta = Int32(amount * (direction == "up" || direction == "left" ? 1 : -1))
            try showCursor(.scroll, at: p, generation: generation)
            try post(CGEvent(mouseEventSource: nil, mouseType: .mouseMoved, mouseCursorPosition: p, mouseButton: .left))
            let event = CGEvent(scrollWheelEvent2Source: nil, units: .line, wheelCount: 2, wheel1: vertical ? delta : 0, wheel2: vertical ? 0 : delta, wheel3: 0)
            event?.location = p; try post(event)
        default: throw DesktopOperationError("unknown_tool", "未知工具")
        }
        return SwiftDesktopBackend.result("\(name) posted as native foreground events. Observe again to verify.",
            ["backend": "swift-native", "delivery_mode": "foreground", "mechanism": "CGEvent", "delivered": true, "verification": "observe_required"])
    }
    private static let keyCodes: [String: CGKeyCode] = [
        "a": 0, "s": 1, "d": 2, "f": 3, "h": 4, "g": 5, "z": 6, "x": 7, "c": 8, "v": 9, "b": 11,
        "q": 12, "w": 13, "e": 14, "r": 15, "y": 16, "t": 17, "1": 18, "2": 19, "3": 20, "4": 21, "6": 22,
        "5": 23, "=": 24, "9": 25, "7": 26, "-": 27, "8": 28, "0": 29, "]": 30, "o": 31, "u": 32, "[": 33,
        "i": 34, "p": 35, "return": 36, "enter": 36, "l": 37, "j": 38, "'": 39, "k": 40, ";": 41, "\\": 42,
        ",": 43, "/": 44, "n": 45, "m": 46, ".": 47, "tab": 48, "space": 49, " ": 49, "`": 50,
        "delete": 51, "backspace": 51, "escape": 53, "esc": 53, "f1": 122, "f2": 120, "f3": 99, "f4": 118,
        "f5": 96, "f6": 97, "f7": 98, "f8": 100, "f9": 101, "f10": 109, "f11": 103, "f12": 111,
        "home": 115, "pageup": 116, "forwarddelete": 117, "end": 119, "pagedown": 121,
        "left": 123, "right": 124, "down": 125, "up": 126
    ]
}
