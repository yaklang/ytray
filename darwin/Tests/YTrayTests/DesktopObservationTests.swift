import CoreGraphics
import XCTest
@testable import YTray

final class DesktopObservationTests: XCTestCase {
    private func row(_ i: Int, _ role: String = "AXButton", label: String = "", x: Double = 0, y: Double = 0) -> [String: Any] {
        ["element_index": i, "element_token": "snapshot-very-long-identity:\(i)", "role": role,
         "label": label, "actions": role == "AXButton" ? ["AXPress"] : [],
         "bounds": ["x": x, "y": y, "width": 40.0, "height": 20.0]]
    }
    private func raw(_ rows: [[String: Any]], focus: [String: Any]? = nil) -> [String: Any] {
        var state: [String: Any] = ["elements": rows, "pid": 123, "window_id": 456, "snapshot_id": "snapshot",
            "window_bounds": ["x": 10.0, "y": 20.0, "width": 1000.0, "height": 800.0], "truncated": false]
        state["focus"] = focus
        return SwiftDesktopBackend.result("old duplicated tree", state)
    }
    private func state(_ value: [String: Any]) -> [String: Any] { value["structuredContent"] as! [String: Any] }
    private func text(_ value: [String: Any]) -> String { (value["content"] as! [[String: Any]])[0]["text"] as! String }

    func testCompactContainsOneControlRepresentationAndOriginalIndexes() throws {
        let source = raw([row(0, "AXWindow"), row(17, label: "Save"), row(39, "AXStaticText", label: "Saved")])
        let compact = try DesktopObservation.format(source, arguments: [:])
        XCTAssertNil(state(compact)["elements"])
        XCTAssertTrue(text(compact).contains("[17] AXButton"))
        XCTAssertFalse(text(compact).contains("snapshot-very-long-identity"))
        XCTAssertFalse(text(compact).contains("[0]"))
        XCTAssertEqual((state(source)["elements"] as? [[String: Any]])?.count, 3)
        let full = try DesktopObservation.format(source, arguments: ["detail": "full"])
        XCTAssertEqual((state(full)["elements"] as? [[String: Any]])?.count, 3)
    }

    func testCompactPreservesLeafStatusAndReportsIncompleteAXCapture() throws {
        var source = raw([row(0, "AXWindow"), row(11, "AXGroup", label: "Saved successfully")])
        var metadata = state(source); metadata["truncated"] = true; metadata["window_title"] = "Report editor"; source["structuredContent"] = metadata
        let result = try DesktopObservation.format(source, arguments: [:])
        XCTAssertTrue(text(result).contains("Saved successfully"))
        XCTAssertTrue(text(result).contains("Report editor"))
        XCTAssertTrue(text(result).contains("AX capture incomplete"))
    }

    func testLegacyElementsFormatHasSummaryWithoutRepeatedTreeAndScaledBounds() throws {
        var source = raw([row(8, label: "Save", x: 100)])
        var metadata = state(source); metadata["coordinate_scale"] = 0.5; source["structuredContent"] = metadata
        let formatted = try DesktopObservation.format(source, arguments: ["tree_format": "elements"])
        XCTAssertFalse(text(formatted).contains("[8]"))
        let elements = state(formatted)["elements"] as! [[String: Any]]
        XCTAssertEqual((elements[0]["bounds"] as? [String: Double])?["x"], 50)
        XCTAssertEqual(elements[0]["element_token"] as? String, "snapshot-very-long-identity:8")
    }

    func testExplicitLegacyMarkdownKeepsActionableTokenReferences() throws {
        let result = try DesktopObservation.format(raw([row(17, label: "Save")]), arguments: ["tree_format": "markdown"])
        XCTAssertTrue(text(result).contains("- [snapshot-very-long-identity:17] AXButton"))
        XCTAssertNil(state(result)["elements"])
    }

    func testRegionNeedsNoScreenshotAndRetainsSparseIndexes() throws {
        let source = raw([row(3, label: "Search", x: 100), row(72, label: "Sidebar", x: 800)])
        let options: [String: Any] = ["region": ["x": 40, "y": 0, "width": 80, "height": 40], "max_image_dimension": 500]
        XCTAssertFalse(try DesktopObservation.wantsScreenshot(options))
        XCTAssertTrue(try DesktopObservation.wantsScreenshot(options.merging(["include_screenshot": true]) { _, new in new }))
        let result = try DesktopObservation.format(source, arguments: options)
        XCTAssertTrue(text(result).contains("[3]"))
        XCTAssertFalse(text(result).contains("Sidebar"))
        XCTAssertEqual(state(result)["controls_shown"] as? Int, 1)
        for region: [String: Any] in [
            ["x": 1000, "y": 0, "width": 1, "height": 1], ["x": 0, "y": 0, "width": 0, "height": 1],
            ["x": -1, "y": 0, "width": 10, "height": 10], ["x": Double.infinity, "y": 0, "width": 10, "height": 10]
        ] { XCTAssertThrowsError(try DesktopObservation.format(source, arguments: ["region": region])) }
    }

    func testQueryDoesNotRenumberOrSearchTokenAndCanReadOffscreenControl() throws {
        let source = raw([row(17, label: "Search"), row(80, label: "Other", x: 2000)])
        let queried = try DesktopObservation.format(source, arguments: ["query": "OTHER"])
        XCTAssertTrue(text(queried).contains("[80]"))
        XCTAssertFalse(text(queried).contains("[17]"))
        let tokenQuery = try DesktopObservation.format(source, arguments: ["query": "snapshot-very-long"])
        XCTAssertEqual(state(tokenQuery)["controls_shown"] as? Int, 0)
    }

    func testDeduplicateExactDestinationsWithinSameWebAreaPrefersFocusedAlias() throws {
        var a = row(3, "AXLink", label: "https://example.com/path")
        a["url"] = "https://example.com/path"; a["web_area_index"] = 1
        var b = a; b["element_index"] = 20; b["label"] = "Descriptive title"
        var c = a; c["element_index"] = 41; c["url"] = "https://example.com/path#other"
        var d = a; d["element_index"] = 52; d["web_area_index"] = 2
        let source = raw([a, b, c, d], focus: ["status": "focused", "element_index": 3])
        let result = try DesktopObservation.format(source, arguments: [:])
        XCTAssertEqual(state(result)["duplicate_links_omitted"] as? Int, 1)
        XCTAssertTrue(text(result).contains("[3]")); XCTAssertFalse(text(result).contains("[20]"))
        XCTAssertTrue(text(result).contains("[41]")); XCTAssertTrue(text(result).contains("[52]"))
        let full = try DesktopObservation.format(source, arguments: ["detail": "full"])
        XCTAssertEqual((state(full)["elements"] as? [[String: Any]])?.count, 4)
    }

    func testDifferentLinkActionsAndStatesRemainIndependent() throws {
        var a = row(3, "AXLink", label: "Open"); a["url"] = "https://example.com"
        var b = a; b["element_index"] = 4; b["actions"] = ["AXPress"]
        var c = b; c["element_index"] = 5; c["enabled"] = false
        let result = try DesktopObservation.format(raw([a, b, c]), arguments: [:])
        XCTAssertEqual(state(result)["duplicate_links_omitted"] as? Int, 0)
        XCTAssertEqual(state(result)["controls_shown"] as? Int, 3)
    }

    func testRegionDoesNotReturnSelectionFromOutsideIt() throws {
        let source = raw([row(3, label: "Search", x: 100), row(72, label: "Private", x: 800)],
                         focus: ["status": "focused", "element_index": 72, "selected_text": "secret selection"])
        let result = try DesktopObservation.format(source, arguments: ["region": ["x": 0, "y": 0, "width": 200, "height": 100]])
        XCTAssertNil((state(result)["focus"] as? [String: Any])?["selected_text"])
        XCTAssertFalse(text(result).contains("secret"))
        XCTAssertTrue(text(result).contains("outside region"))
    }

    func testTextBudgetPrioritizesFocusedInputOverPassiveContent() throws {
        var rows = (0..<100).map { row($0, "AXStaticText", label: String(repeating: "long document \($0) ", count: 50)) }
        rows.append(row(150, "AXTextField", label: "Focused field"))
        let result = try DesktopObservation.format(raw(rows, focus: ["status": "focused", "element_index": 150]), arguments: ["max_text_chars": 256])
        XCTAssertTrue(text(result).contains("[150]"))
        XCTAssertTrue(state(result)["context_truncated"] as? Bool == true)
        XCTAssertLessThan(text(result).count, 600)
    }

    func testActionFailureSurvivesSuccessfulObservationAndReadFailureDoesNotUndoDelivery() {
        let failure = DesktopOperationError("background_unavailable", "refused").toolResult
        let merged = SwiftDesktopBackend.finishAction(failure, observation: raw([row(3)]))
        XCTAssertEqual(merged["isError"] as? Bool, true)
        XCTAssertEqual(state(merged)["error_code"] as? String, "background_unavailable")
        XCTAssertEqual((state(merged)["action"] as? [String: Any])?["error_code"] as? String, "background_unavailable")
        let delivered = SwiftDesktopBackend.result("delivered", ["delivered": true])
        let failedRead = SwiftDesktopBackend.finishAction(delivered, observation: failure)
        XCTAssertEqual(failedRead["isError"] as? Bool, false)
        XCTAssertEqual(state(failedRead)["delivered"] as? Bool, true)
        XCTAssertEqual(state(failedRead)["observation_available"] as? Bool, false)
    }

    func testUnicodeChunksPreserveComposedTextAndBoundNormalEventSizes() {
        let value = String(repeating: "你好👨‍👩‍👧‍👦e\u{301}", count: 20)
        let chunks = NativeAXWorker.unicodeChunks(value)
        XCTAssertEqual(chunks.flatMap { $0 }, Array(value.utf16))
        XCTAssertTrue(chunks.allSatisfy { !$0.isEmpty && $0.count <= 32 })
        XCTAssertTrue(NativeAXWorker.unicodeChunks("").isEmpty)
        for chunk in chunks { XCTAssertFalse(String(decoding: chunk, as: UTF16.self).contains("�")) }
    }

    func testKeyChordsRejectConflictingOrUnknownModifiers() throws {
        let chord = try NativeAXWorker.keyArguments(["key": "cmd+shift+l"])
        XCTAssertEqual(chord["key"] as? String, "l")
        XCTAssertEqual(chord["modifiers"] as? [String], ["cmd", "shift"])
        for args: [String: Any] in [["key": "cmd+l", "modifiers": ["ctrl"]], ["key": "super+l"], ["key": "cmd++"]] {
            XCTAssertThrowsError(try NativeAXWorker.keyArguments(args))
        }
    }

    func testSchemasAdvertiseEfficiencyOptionsAndRejectMalformedRegions() throws {
        let read = DesktopNativeTools.definitions.first { $0["name"] as? String == "get_window_state" }!["inputSchema"] as! [String: Any]
        try NativeAXWorker.validate(["window_id": 2, "detail": "compact", "region": ["x": 0, "y": 0, "width": 20, "height": 30]], schema: read)
        XCTAssertThrowsError(try NativeAXWorker.validate(["pid": 1, "window_id": 2, "region": ["x": 0, "y": 0, "width": true, "height": 30]], schema: read))
        let typing = DesktopNativeTools.definitions.first { $0["name"] as? String == "type_text" }!["inputSchema"] as! [String: Any]
        try NativeAXWorker.validate(["text": "hello", "element_index": 9, "replace": true, "submit": true, "return_state": false], schema: typing)
        XCTAssertThrowsError(try NativeAXWorker.validate(["text": "hello", "element_index": true], schema: typing))
    }
}
