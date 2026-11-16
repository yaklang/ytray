import AppKit
import SwiftUI
import XCTest
@testable import YTray

final class DesktopFeedbackTests: XCTestCase {
    func testFeedbackDistinguishesUnknownInputFromFailedReadback() {
        let feedback = DesktopOperationFeedback.make("click", arguments: ["window_id": 10, "element_index": 3])
        XCTAssertEqual(feedback.summary, "点击控件 [3]")
        let named = DesktopOperationFeedback.make("click", arguments: ["window_id": 10, "element_index": 3], windowTitle: "演示窗口", elementLabel: "保存")
        XCTAssertEqual(named.summary, "点击「保存」")
        XCTAssertEqual(named.context, "演示窗口")
        XCTAssertEqual(feedback.finished(["isError": true, "structuredContent": ["execution_state": "Unknown"]]).phase, .uncertain)
        XCTAssertEqual(feedback.finished(["isError": true]).phase, .failed)
        let readFailure = feedback.finished(["structuredContent": ["observation_available": false]])
        XCTAssertEqual(readFailure.phase, .completed)
        XCTAssertTrue(readFailure.note.contains("暂未取得最新画面"))
    }

    func testPartiallyDispatchedApplicationActionCannotBeAutomaticallyReplayed() {
        let result = SwiftDesktopBackend.operationFailure(DesktopOperationError("focus_unverified", "Window focus changed"), mayHaveExecuted: true)
        let state = result["structuredContent"] as? [String: Any]
        XCTAssertEqual(state?["execution_state"] as? String, "Unknown")
        XCTAssertEqual(state?["automatic_replay_allowed"] as? Bool, false)
        let feedback = DesktopOperationFeedback.make("activate_window", arguments: ["window_id": 42]).finished(result)
        XCTAssertEqual(feedback.phase, .uncertain)
        let denied = SwiftDesktopBackend.operationFailure(DesktopOperationError("app_not_found", "Unknown app"), mayHaveExecuted: false)
        XCTAssertEqual(feedback.finished(denied).phase, .failed)
    }

    func testSummariesHideInputAndBoundUntrustedDisplayStrings() {
        let typed = DesktopOperationFeedback.make("type_text", arguments: ["text": "秘密🔐", "submit": true])
        XCTAssertEqual(typed.summary, "输入 3 个字符 · 按回车提交")
        let key = DesktopOperationFeedback.make("press_key", arguments: ["key": "l", "modifiers": ["cmd"]])
        XCTAssertEqual(key.summary, "按下 CMD+L")
        let long = DesktopOperationFeedback.make("launch_app", arguments: ["app": String(repeating: "x", count: 1000) + "\nsecret"])
        XCTAssertLessThan(long.summary.count, 80)
        XCTAssertFalse(long.summary.contains("\n"))
        XCTAssertEqual(DesktopOperationFeedback.make("scroll", arguments: ["direction": "down"]).summary, "向下滚动 1 步")
    }

    func testEdgesCoverAllFourSidesWithoutScreenSizedBuffers() {
        for frame in [CGRect(x: -1920, y: 0, width: 1920, height: 1080), CGRect(x: 0, y: 1440, width: 2560, height: 1440)] {
            let edges = DesktopFeedbackGeometry.edges(in: frame)
            XCTAssertEqual(edges.count, 4)
            XCTAssertTrue(edges.allSatisfy { frame.contains($0) })
            XCTAssertEqual(edges[0].minY, frame.minY)
            XCTAssertEqual(edges[1].maxY, frame.maxY)
            XCTAssertEqual(edges[2].minX, frame.minX)
            XCTAssertEqual(edges[3].maxX, frame.maxX)
            XCTAssertLessThan(edges.reduce(0) { $0 + $1.width * $1.height }, frame.width * frame.height / 2)
            XCTAssertTrue(frame.contains(DesktopFeedbackGeometry.banner(in: frame)))
        }
    }

    @MainActor
    func testFeedbackPanelNeverBecomesKeyOrInterceptsMouse() {
        let panel = DesktopFeedbackPanel(frame: CGRect(x: -10000, y: -10000, width: 300, height: 124))
        defer { panel.close() }
        XCTAssertFalse(panel.canBecomeKey)
        XCTAssertFalse(panel.canBecomeMain)
        XCTAssertTrue(panel.ignoresMouseEvents)
        XCTAssertFalse(panel.isOpaque)
    }

    @MainActor
    func testOverlayCleanupMovesBackgroundReleaseToMainThread() async {
        let panel = DesktopFeedbackPanel(frame: CGRect(x: -10000, y: -10000, width: 300, height: 124))
        panel.orderFrontRegardless()
        XCTAssertTrue(panel.isVisible)
        let closed = expectation(description: "Window cleanup returned to main queue")
        DispatchQueue.global().async {
            DesktopFeedbackPanel.closeOnMain([panel])
            DispatchQueue.main.async { closed.fulfill() }
        }
        await fulfillment(of: [closed], timeout: 1)
        XCTAssertFalse(panel.isVisible)
    }

    @MainActor
    func testRenderedGlowHasNoCornerSeamsAndFadesToTransparent() throws {
        let width = 600, height = 400
        let context = try XCTUnwrap(CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: width * 4,
            space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        let frame = CGRect(x: 0, y: 0, width: width, height: height)
        for (edge, rect) in DesktopFeedbackGeometry.edges(in: frame).enumerated() {
            let view = DesktopFeedbackEdgeView(frame: CGRect(origin: .zero, size: rect.size), edge: edge, working: false, failed: false)
            context.saveGState()
            context.translateBy(x: rect.minX, y: rect.minY)
            view.layer?.render(in: context)
            context.restoreGState()
        }
        let pixels = try XCTUnwrap(context.data).assumingMemoryBound(to: UInt8.self)
        func alpha(_ x: Int, _ y: Int) -> Int { Int(pixels[(y * width + x) * 4 + 3]) }
        // At both old joins, the side glow must remain continuous across the horizontal strip's end.
        for x in [2, 12, 24, width - 3, width - 13, width - 25] {
            for seam in [40, height - 40] {
                XCTAssertLessThanOrEqual(abs(alpha(x, seam - 1) - alpha(x, seam + 1)), 3, "Visible hard join at \(x),\(seam)")
            }
        }
        XCTAssertGreaterThan(alpha(2, height / 2), 120, "The edge glow must be visible")
        XCTAssertLessThan(alpha(39, height / 2), 2, "The inner tail must reach transparency smoothly")
        XCTAssertEqual(alpha(width / 2, height / 2), 0)
    }

    func testPublicAPIRemovesLegacyNoiseAndKeepsFreshObservationGuards() throws {
        let definitions = DesktopNativeTools.definitions.map(DesktopNativeTools.publicTool)
        let click = try XCTUnwrap(definitions.first { $0["name"] as? String == "click" })
        let schema = try XCTUnwrap(click["inputSchema"] as? [String: Any])
        let properties = try XCTUnwrap(schema["properties"] as? [String: Any])
        for hidden in ["pid", "target", "element_token", "modifiers"] { XCTAssertNil(properties[hidden]) }
        for required in ["element_index", "snapshot_id", "capture_id", "window_id", "delivery_mode"] { XCTAssertNotNil(properties[required]) }
        XCTAssertTrue((schema["required"] as? [String] ?? []).contains("window_id"))
        XCTAssertEqual((properties["delivery_mode"] as? [String: Any])?["default"] as? String, "background")
        XCTAssertEqual(Set(definitions.compactMap { $0["name"] as? String }).count, definitions.count)
    }

    @MainActor
    func testInspectionResetDiscardsLateResultAndError() async throws {
        let model = DesktopInspectionModel()
        for fails in [false, true] {
            let started = expectation(description: "Read started")
            var resume: CheckedContinuation<[String: Any], Error>?
            let pending = model.inspect("list_windows") {
                try await withCheckedThrowingContinuation { continuation in
                    resume = continuation
                    started.fulfill()
                }
            }
            await fulfillment(of: [started], timeout: 1)
            XCTAssertTrue(model.isLoading)
            model.reset()
            if fails { resume?.resume(throwing: SystemControlError("Old session error")) }
            else { resume?.resume(returning: ["structuredContent": ["windows": [["window_id": 99]], "text": "Old window"]]) }
            await pending.value
            XCTAssertTrue(model.windows.isEmpty)
            XCTAssertTrue(model.observation.isEmpty)
            XCTAssertTrue(model.error.isEmpty)
            XCTAssertNil(model.screenshot)
            XCTAssertFalse(model.isLoading)
        }
    }

    @MainActor
    func testReplacedInspectionCannotOverwriteNewResult() async throws {
        let model = DesktopInspectionModel()
        let started = expectation(description: "First read started")
        var resume: CheckedContinuation<[String: Any], Never>?
        let old = model.inspect("list_windows") {
            await withCheckedContinuation { continuation in
                resume = continuation
                started.fulfill()
            }
        }
        await fulfillment(of: [started], timeout: 1)
        await model.inspect("list_windows") {
            ["structuredContent": ["windows": [["window_id": 42]], "text": "Current window"]]
        }.value
        resume?.resume(returning: ["structuredContent": ["windows": [["window_id": 99]], "text": "Old window"]])
        await old.value
        XCTAssertEqual(model.windows.first?["window_id"] as? Int, 42)
        XCTAssertTrue(model.observation.contains("Current window"))
        XCTAssertFalse(model.observation.contains("Old window"))
        XCTAssertFalse(model.isLoading)
        model.reset()
        XCTAssertTrue(model.observation.isEmpty)
    }

    @MainActor
    func testResetBeforeInspectionStartsDoesNotCallBackend() async {
        let model = DesktopInspectionModel()
        var calls = 0
        let pending = model.inspect("list_windows") { calls += 1; return [:] }
        model.reset()
        await pending.value
        XCTAssertEqual(calls, 0)
        XCTAssertFalse(model.isLoading)
        await model.inspect("get_window_state") {
            ["isError": true, "structuredContent": ["error_code": "stale_observation", "message": "Read the window again"]]
        }.value
        XCTAssertEqual(model.error, "stale_observation: Read the window again")
        XCTAssertFalse(model.isLoading)
        model.reset()
        XCTAssertTrue(model.error.isEmpty)
    }

    /// Export real native views without activating an app or reading/clicking the user's desktop.
    @MainActor
    func testExportNativeFeedbackPreviewWhenRequested() async throws {
        guard let output = ProcessInfo.processInfo.environment["YTRAY_FEEDBACK_PREVIEW_DIR"] else { return }
        let directory = URL(fileURLWithPath: output)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let size = CGSize(width: 1100, height: 700)
        let canvas = NSView(frame: CGRect(origin: .zero, size: size))
        canvas.wantsLayer = true
        canvas.layer?.backgroundColor = NSColor(srgbRed: 0.13, green: 0.14, blue: 0.16, alpha: 1).cgColor
        let label = NSTextField(labelWithString: "YTray · 原生视图预览\n陶土红氛围灯 / 飞镖光标 / 操作提示")
        label.font = .systemFont(ofSize: 24, weight: .medium)
        label.textColor = .secondaryLabelColor
        label.frame = CGRect(x: 140, y: 320, width: 800, height: 100)
        canvas.addSubview(label)
        for (edge, frame) in DesktopFeedbackGeometry.edges(in: canvas.bounds).enumerated() {
            let view = DesktopFeedbackEdgeView(frame: CGRect(origin: .zero, size: frame.size), edge: edge, working: false, failed: false)
            view.frame = frame; canvas.addSubview(view)
        }
        var feedback = DesktopOperationFeedback.make("type_text", arguments: ["window_id": 42, "text": "这是一次原生控制效果预览", "replace": true])
        feedback.phase = .running
        let banner = NSHostingView(rootView: DesktopFeedbackBanner(feedback: feedback))
        banner.frame = DesktopFeedbackGeometry.banner(in: canvas.bounds)
        canvas.addSubview(banner)
        let cursor = DesktopCursorView(frame: CGRect(x: 610, y: 205, width: 112, height: 112))
        canvas.addSubview(cursor)
        try await export(canvas, to: directory.appendingPathComponent("native-feedback.png"))

        let scratch = directory.appendingPathComponent("store")
        let suite = "io.yaklang.ytray.preview.\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite); try? FileManager.default.removeItem(at: scratch) }
        let store = InstanceStore(applicationDirectory: scratch, discoverSystemBrowsers: false)
        let mcp = YTrayMCPController(store: store, defaults: defaults)
        let page = NSHostingView(rootView: SystemControlPage(controller: store.systemControl, mcp: mcp))
        page.frame = CGRect(x: 0, y: 0, width: 820, height: 680)
        try await export(page, to: directory.appendingPathComponent("computer-control-page.png"))
    }

    @MainActor
    private func export(_ view: NSView, to file: URL) async throws {
        let window = NSWindow(contentRect: CGRect(x: -12000, y: -12000, width: view.frame.width, height: view.frame.height), styleMask: [.borderless], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.contentView = view
        window.appearance = NSAppearance(named: .darkAqua)
        defer { window.close() }
        view.layoutSubtreeIfNeeded()
        try await Task.sleep(nanoseconds: 100_000_000)
        view.layoutSubtreeIfNeeded()
        let bitmap = try XCTUnwrap(view.bitmapImageRepForCachingDisplay(in: view.bounds))
        view.cacheDisplay(in: view.bounds, to: bitmap)
        try XCTUnwrap(bitmap.representation(using: .png, properties: [:])).write(to: file)
    }
}
