import XCTest
@testable import YTray

@MainActor
final class ApplicationRelauncherTests: XCTestCase {
    func testFailedHelperLaunchKeepsAppRunningAndAllowsRetry() throws {
        let (directory, app, executable) = try fixture()
        defer { try? FileManager.default.removeItem(at: directory) }
        var fail = true
        var events: [String] = []
        let controller = ApplicationRelauncher(applicationURL: { app }, executableURL: { executable }, launch: { _, _ in
            events.append("launch")
            if fail { throw SystemControlError("Fixture spawn failure") }
        }, terminate: { events.append("terminate") })
        controller.restart()
        XCTAssertFalse(controller.isRestarting)
        XCTAssertEqual(controller.errorMessage, "Fixture spawn failure")
        XCTAssertEqual(events, ["launch"])
        fail = false
        controller.restart()
        XCTAssertTrue(controller.isRestarting)
        XCTAssertNil(controller.errorMessage)
        XCTAssertEqual(events, ["launch", "launch", "terminate"])
        controller.restart()
        XCTAssertEqual(events, ["launch", "launch", "terminate"], "Repeated clicks must not create duplicate helpers")
    }

    func testRestartRejectsUnbundledOrExternalExecutables() throws {
        let (directory, app, executable) = try fixture()
        defer { try? FileManager.default.removeItem(at: directory) }
        for (application, binary) in [(directory, executable), (app, URL(fileURLWithPath: "/bin/sh"))] {
            var touchedProcess = false
            let controller = ApplicationRelauncher(applicationURL: { application }, executableURL: { binary },
                launch: { _, _ in touchedProcess = true }, terminate: { touchedProcess = true })
            controller.restart()
            XCTAssertFalse(touchedProcess)
            XCTAssertFalse(controller.isRestarting)
            XCTAssertNotNil(controller.errorMessage)
        }
    }

    private func fixture() throws -> (URL, URL, URL) {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("ytray-relaunch-tests-\(UUID())")
        let app = directory.appendingPathComponent("YTray.app")
        let executable = app.appendingPathComponent("Contents/MacOS/YTray")
        try FileManager.default.createDirectory(at: executable.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data("#!/bin/sh\nexit 0\n".utf8).write(to: executable)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: executable.path)
        return (directory, app, executable)
    }
}
