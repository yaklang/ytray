import AppKit
import ApplicationServices
import XCTest
@testable import YTray

/// Explicit opt-in exercises real AX and focus with a disposable external fixture.
/// Every case rejects before HID input; ordinary CI never takes desktop focus.
@MainActor
final class NativeActionRaceTests: XCTestCase {
    func testUnchangedControlReceivesExactlyOneClick() async throws {
        try await exercise("none", tool: "click", label: "Race Increment", arguments: [:], expectedCode: nil)
    }
    func testUnchangedFocusReceivesPlainText() async throws {
        try await exercise("none", tool: "type_text", label: nil, arguments: ["text": "fixture-only"], expectedCode: nil)
    }
    func testMovingControlDuringCueDoesNotClickOldPosition() async throws {
        try await exercise("move_control", tool: "click", label: "Race Increment", arguments: [:], expectedCode: "stale_pointer_target")
    }
    func testMovingWindowDuringCueDoesNotClickOldPosition() async throws {
        try await exercise("move_window", tool: "click", label: "Race Increment", arguments: [:], expectedCode: "stale_pointer_target")
    }
    func testExplicitKeyDoesNotFollowFocusToAnotherEditorDuringCue() async throws {
        try await exercise("change_focus", tool: "press_key", label: "Race Editor A", arguments: ["key": "return"], expectedCode: "focus_unverified")
    }
    func testPlainTypingBindsFocusBeforeCue() async throws {
        // Secure fields deliberately use native Unicode events, not AX value writes.
        try await exercise("change_focus", tool: "type_text", label: nil, arguments: ["text": "fixture-only"], expectedCode: "focus_unverified")
    }

    func testBackgroundValueFallbackDoesNotOverwriteEditsDuringCue() async throws {
        try await exercise("edit_value", tool: "type_text", label: "Race Value Only", arguments: ["text": "fixture-only"],
            expectedCode: "stale_text_selection", deliveryMode: "background")
    }

    private func exercise(_ change: String, tool: String, label: String?, arguments: [String: Any], expectedCode: String?,
                          deliveryMode: String = "foreground") async throws {
        guard let fixturePath = ProcessInfo.processInfo.environment["YTRAY_NATIVE_RACE_FIXTURE"],
              FileManager.default.isExecutableFile(atPath: fixturePath) else {
            throw XCTSkip("Set YTRAY_NATIVE_RACE_FIXTURE to the compiled NativeRaceFixture for isolated foreground AX checks")
        }
        guard AXIsProcessTrusted() else { throw XCTSkip("Native race fixture requires Accessibility permission") }
        let previous = NSWorkspace.shared.frontmostApplication
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("ytray-native-race-\(UUID())")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let stateURL = directory.appendingPathComponent("state.json"), commandURL = directory.appendingPathComponent("change.txt")
        let process = Process()
        process.executableURL = URL(fileURLWithPath: fixturePath)
        process.arguments = [stateURL.path, commandURL.path]
        process.standardOutput = FileHandle.nullDevice; process.standardError = FileHandle.nullDevice
        try process.run()
        defer {
            let restore = NSWorkspace.shared.frontmostApplication?.processIdentifier == process.processIdentifier
            if process.isRunning { process.terminate(); process.waitUntilExit() }
            try? FileManager.default.removeItem(at: directory)
            if restore { previous?.activate(options: []) }
        }
        func state() -> [String: Any] {
            guard let data = try? Data(contentsOf: stateURL) else { return [:] }
            return (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] ?? [:]
        }
        let pid = Int(process.processIdentifier)
        for _ in 0..<50 {
            if state()["frontmost_pid"] as? Int == pid { break }
            try await Task.sleep(nanoseconds: 100_000_000)
        }
        XCTAssertEqual(state()["frontmost_pid"] as? Int, pid, "Fixture must own focus before race checks")
        let window = try XCTUnwrap(state()["window_id"] as? Int)
        let lease = DesktopSessionLease()
        let generation = lease.begin()
        defer { lease.end() }
        let worker = NativeAXWorker(lease: lease, onAction: { _, _ in
            try? change.write(to: commandURL, atomically: true, encoding: .utf8)
        })
        var observation: [String: Any] = [:]
        for attempt in 0..<15 {
            do {
                observation = try await worker.run(generation) { try $0.observe(["pid": pid, "window_id": window], generation: generation) }
                break
            } catch let error as DesktopOperationError where attempt < 14 && ["window_unresolved", "window_unavailable"].contains(error.code) {
                try await Task.sleep(nanoseconds: 100_000_000)
            }
        }
        let observed = try XCTUnwrap(observation["structuredContent"] as? [String: Any])
        let elements = try XCTUnwrap(observed["elements"] as? [[String: Any]])
        var input = arguments
        input["pid"] = pid; input["window_id"] = window; input["delivery_mode"] = deliveryMode
        if let label {
            input["element_token"] = try XCTUnwrap(elements.first { $0["label"] as? String == label }?["element_token"] as? String)
        }
        do {
            _ = try await worker.run(generation) { try $0.act(tool, args: input, generation: generation, desktopCapture: nil) }
            if expectedCode != nil { XCTFail("Input was admitted after its target changed during the cue") }
        } catch {
            XCTAssertEqual((error as? DesktopOperationError)?.code, expectedCode, error.localizedDescription)
        }
        let clicked = expectedCode == nil && tool == "click"
        let typed = expectedCode == nil && tool == "type_text" ? arguments["text"] as? String ?? "" : ""
        for _ in 0..<30 {
            if state()["submissions"] as? Int == (clicked ? 1 : 0), state()["first"] as? String == typed { break }
            try await Task.sleep(nanoseconds: 20_000_000)
        }
        let final = state()
        XCTAssertEqual(final["cues"] as? Int, 1, "Fixture must change during the real cursor cue")
        XCTAssertEqual(final["submissions"] as? Int, clicked ? 1 : 0)
        XCTAssertEqual(final["first"] as? String, typed)
        XCTAssertEqual(final["second"] as? String, "")
        XCTAssertEqual(final["value_only"] as? String, change == "edit_value" ? "user edited" : "original")
    }
}
