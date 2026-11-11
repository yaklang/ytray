import CoreGraphics
import Foundation

/// A presentation of one complete AX snapshot. Filtering never renumbers or edits
/// the worker's handles; full/raw reads remain available for inspection.
enum DesktopObservation {
    static func wantsScreenshot(_ args: [String: Any]) throws -> Bool {
        try NativeAXWorker.bool(args, "include_screenshot", fallback: args["region"] == nil)
    }

    static func format(_ raw: [String: Any], arguments: [String: Any]) throws -> [String: Any] {
        guard var state = raw["structuredContent"] as? [String: Any], raw["isError"] as? Bool != true else { return raw }
        let all = state["elements"] as? [[String: Any]] ?? []
        // Explicit legacy formats keep their meaning. Default responses carry the
        // controls once, in text, rather than repeating markdown and JSON trees.
        let full = arguments["detail"] as? String == "full" || arguments["tree_format"] != nil
        let region = try readRegion(arguments, state: state)
        let query = arguments["query"] as? String ?? ""
        let viewport = rect(state["window_bounds"]).map { CGRect(origin: .zero, size: $0.size) }
        let parents = Set(all.compactMap { $0["parent_index"] as? Int })
        var rows = all.filter { row in
            if let region { guard let bounds = rect(row["bounds"]), region.intersects(bounds) else { return false } }
            if !query.isEmpty && !["label", "value", "identifier", "role", "url"].contains(where: {
                String(describing: row[$0] ?? "").localizedCaseInsensitiveContains(query)
            }) { return false }
            if full || !query.isEmpty { return true }
            if row["hidden"] as? Bool == true { return false }
            if let viewport, let bounds = rect(row["bounds"]), !viewport.intersects(bounds) { return false }
            let role = row["role"] as? String ?? ""
            if ["AXWindow", "AXGroup", "AXLayoutArea", "AXToolbar", "AXSplitter", "AXImage"].contains(role),
               (row["actions"] as? [String] ?? []).isEmpty, row["focused"] as? Bool != true {
                if role == "AXWindow" || parents.contains(index(row)) || ((row["label"] as? String ?? "").isEmpty && row["value"] == nil) { return false }
            }
            return true
        }
        let focus = state["focus"] as? [String: Any]
        let focusedIndex = focus?["element_index"] as? Int
        let beforeDedup = rows.count
        if !full {
            var keep: [String: [String: Any]] = [:]
            for row in rows {
                guard let key = linkKey(row) else { continue }
                if let previous = keep[key] {
                    if score(row, focused: focusedIndex) > score(previous, focused: focusedIndex) { keep[key] = row }
                } else { keep[key] = row }
            }
            rows = rows.filter { row in
                guard let key = linkKey(row), let kept = keep[key] else { return true }
                return row["element_index"] as? Int == kept["element_index"] as? Int
            }
        }
        let duplicateCount = beforeDedup - rows.count
        let maximum = try NativeAXWorker.integer(arguments, "max_text_chars", fallback: full ? 30000 : 6000, range: 256...30000)
        var remaining = maximum, lines: [(Int, String)] = [], omitted = 0
        // Only controls actually emitted may suppress their duplicate passive text.
        // Hidden, off-region or budget-omitted links must not erase visible content.
        var labels = Set<String>()
        var clipped = false
        // Focused and editable controls survive the budget before passive text.
        let ordered = rows.sorted {
            let a = priority($0, focused: focusedIndex), b = priority($1, focused: focusedIndex)
            return a == b ? index($0) < index($1) : a < b
        }
        for row in ordered {
            let label = row["label"] as? String ?? ""
            let passive = row["role"] as? String == "AXStaticText"
            if !full && passive && !label.isEmpty && labels.contains(label) { omitted += 1; continue }
            var cap = full ? 8000 : 180
            if (row["label"] as? String ?? "").count > cap || String(describing: row["value"] ?? "").count > cap { clipped = true }
            let legacy = arguments["tree_format"] as? String
            var line = legacy == "markdown" || legacy == "both" ? describeLegacy(row) : describe(row, full: full)
            // A long URL/value must not make the focused field disappear from a
            // small observation budget. Preserve its original actionable index.
            if !full && priority(row, focused: focusedIndex) <= 0 {
                while line.count + 1 > remaining && cap > 8 {
                    cap = max(8, cap / 2)
                    line = describe(row, full: false, textLimit: cap)
                    clipped = true
                }
            }
            if (!full && lines.count >= 80) || line.count + 1 > remaining { omitted += 1; continue }
            lines.append((index(row), line)); remaining -= line.count + 1
            if !label.isEmpty { labels.insert(label) }
        }
        state["controls_total"] = all.count
        state["controls_shown"] = lines.count
        state["controls_omitted"] = omitted
        state["duplicate_links_omitted"] = duplicateCount
        state["context_truncated"] = omitted > 0 || clipped
        state["detail"] = full ? "full" : "compact"
        if let region { state["region_points"] = bounds(region) }
        var focusLine = ""
        if var focus {
            if let region {
                let inside = all.first { $0["element_index"] as? Int == focusedIndex }.flatMap { rect($0["bounds"]) }.map(region.intersects) ?? false
                if !inside { focus.removeValue(forKey: "selected_text"); focus["outside_region"] = true }
            }
            focusLine = "Focus: " + (focusedIndex.map { "[\($0)]" } ?? (focus["status"] as? String ?? "unavailable"))
            if focus["status"] as? String == "background" { focusLine += " (background)" }
            if focus["outside_region"] as? Bool == true { focusLine += " (outside region)" }
            else if let selected = focus["selected_text"] as? String { focusLine += " selected=" + quote(clip(selected, 160)) }
            state["focus"] = focus
        }
        let tree = lines.sorted { $0.0 < $1.0 }.map(\.1).joined(separator: "\n")
        let title = clip(state["window_title"] as? String ?? "", 120)
        let summary = (title.isEmpty ? "" : quote(title) + " ") + "window_id=\(state["window_id"] ?? "") pid=\(state["pid"] ?? "") snapshot_id=\(state["snapshot_id"] ?? "") controls=\(lines.count)/\(all.count)"
        var text = summary + (focusLine.isEmpty ? "" : "\n" + focusLine) + (tree.isEmpty ? "\nNo matching accessible controls." : "\n" + tree)
        let screenshotError = state["screenshot_error"] as? [String: Any]
        let hasAXErrors = !(state["ax_errors"] as? [String] ?? []).isEmpty
        if state["truncated"] as? Bool == true || hasAXErrors || (state["degraded"] as? Bool == true && screenshotError == nil) {
            text += "\nAX capture incomplete; inspect capture limits or retry observation."
        }
        if let screenshotError {
            let reason = clip(screenshotError["reason"] as? String ?? "capture unavailable", 180)
            text += "\nScreenshot unavailable: " + quote(reason) + ". Use available AX controls; pixel input requires a fresh valid capture."
        } else if state["screenshot_frame_valid"] as? Bool == true,
                  let width = state["image_width"] as? Int, let height = state["image_height"] as? Int,
                  let capture = state["capture_id"] as? String {
            text += "\nImage: \(width)x\(height) pixels; capture_id=\(capture). Coordinates use image pixels."
        }
        if omitted > 0 || clipped { text += "\nMore: use query, a smaller region, or detail=full." }
        let legacy = arguments["tree_format"] as? String
        if full && legacy != "markdown" {
            let scale = (state["coordinate_scale"] as? NSNumber)?.doubleValue ?? 1
            state["elements"] = rows.map { row in
                var copy = row
                if let bounds = row["bounds"] as? [String: Double] { copy["bounds"] = bounds.mapValues { $0 * scale } }
                return copy
            }
        }
        else { state.removeValue(forKey: "elements") }
        if legacy == "elements" { text = summary }
        var result = raw
        result["structuredContent"] = state
        let images = (raw["content"] as? [[String: Any]] ?? []).filter { $0["type"] as? String == "image" }
        result["content"] = [["type": "text", "text": text]] + images
        return result
    }

    static func rect(_ value: Any?) -> CGRect? {
        guard let b = value as? [String: Any], let x = (b["x"] as? NSNumber)?.doubleValue, let y = (b["y"] as? NSNumber)?.doubleValue,
              let w = (b["width"] as? NSNumber)?.doubleValue, let h = (b["height"] as? NSNumber)?.doubleValue,
              [x, y, w, h].allSatisfy(\.isFinite), w > 0, h > 0 else { return nil }
        return CGRect(x: x, y: y, width: w, height: h)
    }
    private static func readRegion(_ args: [String: Any], state: [String: Any]) throws -> CGRect? {
        guard let requested = args["region"] else { return nil }
        guard let region = rect(requested), region.minX >= 0, region.minY >= 0, let window = rect(state["window_bounds"]) else {
            throw DesktopOperationError("invalid_region", "region 需要有限的 x/y 和大于零的 width/height")
        }
        let maximum = try NativeAXWorker.integer(args, args["max_image_dimension"] == nil ? "max_dimension" : "max_image_dimension", fallback: 2048, range: 100...4096)
        let scale = min(1, Double(maximum) / max(window.width, window.height))
        let width = state["image_width"] as? Int ?? max(1, Int((window.width * scale).rounded()))
        let height = state["image_height"] as? Int ?? max(1, Int((window.height * scale).rounded()))
        let pixels = CGRect(x: 0, y: 0, width: width, height: height)
        guard region.intersects(pixels) else { throw DesktopOperationError("invalid_region", "region 不与窗口图片相交") }
        let clipped = region.intersection(pixels)
        return CGRect(x: clipped.minX * window.width / Double(width), y: clipped.minY * window.height / Double(height),
                      width: clipped.width * window.width / Double(width), height: clipped.height * window.height / Double(height))
    }
    private static func bounds(_ rect: CGRect) -> [String: Double] {
        ["x": rect.minX, "y": rect.minY, "width": rect.width, "height": rect.height]
    }
    private static func index(_ row: [String: Any]) -> Int { row["element_index"] as? Int ?? -1 }
    private static func priority(_ row: [String: Any], focused: Int?) -> Int {
        if let focused, index(row) == focused { return -1 }
        let role = row["role"] as? String ?? ""
        if ["AXTextField", "AXTextArea", "AXComboBox", "AXSearchField"].contains(role) { return 0 }
        if !(row["actions"] as? [String] ?? []).isEmpty { return 1 }
        return role == "AXStaticText" ? 3 : 2
    }
    private static func linkKey(_ row: [String: Any]) -> String? {
        guard row["role"] as? String == "AXLink", let address = row["url"] as? String,
              let url = URL(string: address), ["http", "https"].contains(url.scheme?.lowercased() ?? "") else { return nil }
        let actions = (row["actions"] as? [String] ?? []).sorted().joined(separator: ",")
        return "\(row["web_area_index"] ?? -1)|\(row["enabled"] ?? true)|\(row["selected"] ?? "")|\(row["expanded"] ?? "")|\(actions)|" + url.absoluteString
    }
    private static func score(_ row: [String: Any], focused: Int?) -> Int {
        if let focused, index(row) == focused { return 10000 }
        let label = row["label"] as? String ?? ""
        return URL(string: label)?.scheme == nil ? min(160, label.count) : 0
    }
    private static func describeLegacy(_ row: [String: Any]) -> String {
        let indent = String(repeating: "  ", count: min(32, max(0, row["depth"] as? Int ?? 0)))
        var text = indent + "- [\(row["element_token"] ?? "")] \(row["role"] ?? "AXUnknown") " + quote(clip(row["label"] as? String ?? "", 8000))
        if let value = row["value"] { text += " value=" + quote(clip(String(describing: value), 8000)) }
        text += " actions=\(row["actions"] ?? [])"
        return text
    }
    private static func describe(_ row: [String: Any], full: Bool, textLimit: Int? = nil) -> String {
        var text = "[\(index(row))] \(row["role"] ?? "AXUnknown")"
        let cap = textLimit ?? (full ? 8000 : 180)
        if let label = row["label"] as? String, !label.isEmpty { text += " " + quote(clip(label, cap)) }
        if let value = row["value"], String(describing: value) != row["label"] as? String { text += " = " + quote(clip(String(describing: value), cap)) }
        for name in ["focused", "selected", "expanded"] where row[name] as? Bool == true { text += " " + name }
        if row["enabled"] as? Bool == false { text += " disabled" }
        if full, let token = row["element_token"] as? String { text += " token=" + token }
        return text
    }
    private static func clip(_ text: String, _ count: Int) -> String {
        let value = text.split(whereSeparator: \.isWhitespace).joined(separator: " ")
        return value.count <= count ? value : String(value.prefix(count)) + "…"
    }
    private static func quote(_ text: String) -> String {
        // JSON strings keep newlines, quotes and control characters on one row.
        guard let data = try? JSONSerialization.data(withJSONObject: [text]), let array = String(data: data, encoding: .utf8) else { return "\"\"" }
        return String(array.dropFirst().dropLast())
    }
}
