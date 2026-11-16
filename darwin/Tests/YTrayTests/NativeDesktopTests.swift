import AppKit
import CoreGraphics
import XCTest
@testable import YTray

final class NativeDesktopTests: XCTestCase {
    private let frame = CGRect(x: 10, y: 20, width: 640, height: 568)

    func testPublicWindowMatchingRequiresUniqueGeometryAndTitle() throws {
        let candidates: [NativeWindowMatcher.Candidate] = [
            .init(frame: frame, title: "A"), .init(frame: frame, title: "B"),
            .init(frame: frame.offsetBy(dx: 100, dy: 0), title: "A")
        ]
        XCTAssertEqual(try NativeWindowMatcher.index(frame: frame, title: "B", candidates: candidates), 1)
        XCTAssertThrowsError(try NativeWindowMatcher.index(frame: frame, title: "", candidates: candidates)) {
            XCTAssertEqual(($0 as? DesktopOperationError)?.code, "window_ambiguous")
        }
        XCTAssertThrowsError(try NativeWindowMatcher.index(frame: frame, title: "C", candidates: candidates)) {
            XCTAssertEqual(($0 as? DesktopOperationError)?.code, "window_unresolved")
        }
        XCTAssertThrowsError(try NativeWindowMatcher.index(frame: frame.offsetBy(dx: 2, dy: 0), title: "B", candidates: candidates))
    }

    func testWindowTargetRejectsConflictingAndNonIntegralIdentifiers() throws {
        let target: [String: Any] = ["kind": "window", "pid": 123, "window_id": 456]
        XCTAssertEqual(try NativeAXWorker.windowTarget(["target": target]), .init(pid: 123, windowID: 456))
        XCTAssertThrowsError(try NativeAXWorker.windowTarget(["target": target, "pid": 124, "window_id": 456]))
        for value: Any in [true, 1.5, 0, -1, Double.infinity, Int64.max] {
            XCTAssertThrowsError(try NativeAXWorker.windowTarget(["pid": value, "window_id": 456]))
        }
    }

    func testPointerAdmissionRejectsWindowAndControlMotionDuringCursorCue() throws {
        let point = CGPoint(x: 200, y: 100)
        try NativePointerValidation.validate(window: frame, currentWindow: frame, point: point, currentPoint: point)
        // Same focused window, but moved, resized, or with a relaid-out control.
        for (currentWindow, currentPoint) in [
            (frame.offsetBy(dx: 10, dy: 0), point),
            (CGRect(x: frame.minX, y: frame.minY, width: frame.width + 10, height: frame.height), point),
            (frame, CGPoint(x: point.x + 10, y: point.y)),
            (frame, CGPoint(x: Double.nan, y: point.y))
        ] {
            XCTAssertThrowsError(try NativePointerValidation.validate(window: frame, currentWindow: currentWindow, point: point, currentPoint: currentPoint)) {
                XCTAssertEqual(($0 as? DesktopOperationError)?.code, "stale_pointer_target")
            }
        }
    }

    func testChromiumDecoratedTitlesStillRequireExactApplicationAndUniqueGeometry() throws {
        let windows: [NativeWindowMatcher.Candidate] = [
            .init(frame: frame, title: "Example — 首页 - Google Chrome - Work"),
            .init(frame: frame.offsetBy(dx: 100, dy: 0), title: "Example — 首页 - Google Chrome - Work")
        ]
        XCTAssertEqual(try NativeWindowMatcher.index(frame: frame, title: "Example — 首页", candidates: windows, applicationName: "Google Chrome"), 0)
        for title in ["Example", "Example — 首页 - Other", "Unrelated"] {
            XCTAssertThrowsError(try NativeWindowMatcher.index(frame: frame, title: title, candidates: windows, applicationName: "Google Chrome"))
        }
        for name: String? in [nil, "", "Chrome", "Other Browser"] {
            XCTAssertThrowsError(try NativeWindowMatcher.index(frame: frame, title: "Example — 首页", candidates: windows, applicationName: name))
        }
        XCTAssertThrowsError(try NativeWindowMatcher.index(frame: frame, title: "Example — 首页", candidates: [windows[0], windows[0]], applicationName: "Google Chrome")) {
            XCTAssertEqual(($0 as? DesktopOperationError)?.code, "window_ambiguous")
        }
    }

    /// Read-only integration check; opt in with an existing window, never launches or activates an app.
    func testReadExistingNativeWindowWhenRequested() async throws {
        let environment = ProcessInfo.processInfo.environment
        guard let rawPID = environment["YTRAY_NATIVE_TEST_PID"], let pid = Int(rawPID),
              let rawWindow = environment["YTRAY_NATIVE_TEST_WINDOW"], let window = Int(rawWindow) else {
            throw XCTSkip("Set YTRAY_NATIVE_TEST_PID and YTRAY_NATIVE_TEST_WINDOW for read-only native AX integration")
        }
        let lease = DesktopSessionLease()
        let generation = lease.begin()
        defer { lease.end() }
        let worker = NativeAXWorker(lease: lease)
        let result = try await worker.run(generation) {
            try $0.observe(["pid": pid, "window_id": window, "max_elements": 80], generation: generation)
        }
        let state = try XCTUnwrap(result["structuredContent"] as? [String: Any])
        XCTAssertEqual(state["window_id"] as? Int, window)
        XCTAssertFalse(try XCTUnwrap(state["elements"] as? [[String: Any]]).isEmpty)
        XCTAssertNotNil(state["snapshot_id"])
    }

    func testActualToolSchemasRejectUnknownArgumentsAndBadTypes() throws {
        let schema = try XCTUnwrap(DesktopNativeTools.definitions.first { $0["name"] as? String == "get_window_state" }?["inputSchema"] as? [String: Any])
        try NativeAXWorker.validate(["pid": 123, "window_id": 456, "include_screenshot": true, "max_elements": 200], schema: schema)
        for args: [String: Any] in [
            ["pid": true, "window_id": 456], ["pid": 123, "window_id": 456, "include_screenshot": 1],
            ["pid": 123, "window_id": 456, "max_elements": 1001], ["pid": 123, "window_id": 456, "timeout_ms": 0],
            ["pid": 123], ["pid": 123, "window_id": 456, "tree_format": "html"],
            ["pid": 123, "window_id": 456, "screenshot_out_file": "/tmp/forbidden"]
        ] { XCTAssertThrowsError(try NativeAXWorker.validate(args, schema: schema)) }
        let scroll = try XCTUnwrap(DesktopNativeTools.definitions.first { $0["name"] as? String == "scroll" }?["inputSchema"] as? [String: Any])
        try NativeAXWorker.validate(["direction": "down", "element_token": "snapshot:1", "amount": 3], schema: scroll)
        XCTAssertThrowsError(try NativeAXWorker.validate(["direction": "diagonal"], schema: scroll))
    }

    func testRevocationPreventsQueuedWorkAndOldGenerationCannotRevive() async throws {
        let lease = DesktopSessionLease()
        let first = lease.begin()
        let worker = NativeAXWorker(lease: lease)
        lease.end()
        _ = lease.begin()
        var entered = false
        do {
            _ = try await worker.run(first) { _ in entered = true; return [:] }
            XCTFail("Revoked generation entered native worker")
        } catch { XCTAssertEqual((error as? DesktopOperationError)?.code, "session_ended") }
        XCTAssertFalse(entered)
        lease.end()
    }
}
