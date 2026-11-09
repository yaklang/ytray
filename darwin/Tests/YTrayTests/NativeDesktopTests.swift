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
