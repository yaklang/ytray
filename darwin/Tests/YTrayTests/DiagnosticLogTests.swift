import XCTest
@testable import YTray

final class DiagnosticLogTests: XCTestCase {
    private var applicationDirectory: URL!

    override func setUpWithError() throws {
        try super.setUpWithError()
        DiagnosticLog.resetForTesting()
        applicationDirectory = FileManager.default.temporaryDirectory
            .appendingPathComponent("ytray-log-test-\(UUID().uuidString)", isDirectory: true)
    }

    override func tearDownWithError() throws {
        DiagnosticLog.resetForTesting()
        try? FileManager.default.removeItem(at: applicationDirectory)
        applicationDirectory = nil
        try super.tearDownWithError()
    }

    func testInitializationCreatesBothSupportLogs() throws {
        DiagnosticLog.initialize(applicationDirectory: applicationDirectory)

        XCTAssertTrue(FileManager.default.fileExists(atPath: DiagnosticLog.mainLogURL.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: DiagnosticLog.errorLogURL.path))
        let main = try String(contentsOf: DiagnosticLog.mainLogURL, encoding: .utf8)
        XCTAssertTrue(main.contains("[app.start]"))
        let errorSize = try DiagnosticLog.errorLogURL.resourceValues(forKeys: [.fileSizeKey]).fileSize
        XCTAssertEqual(errorSize, 0)
    }

    func testErrorsAreWrittenToBothLogsWithSensitiveValuesRedacted() throws {
        DiagnosticLog.initialize(applicationDirectory: applicationDirectory)
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        let error = NSError(
            domain: "YTrayTests",
            code: 1,
            userInfo: [NSLocalizedDescriptionKey:
                "path=\(home)/secret password=hunter2 token=abc123 "
                + "url=https://alice:private@example.test/file\n"
                + "Authorization: Bearer private-bearer\n"
                + #"json={"secret":"private-json"}"#]
        )

        DiagnosticLog.error("test.error", error)

        let main = try String(contentsOf: DiagnosticLog.mainLogURL, encoding: .utf8)
        let errors = try String(contentsOf: DiagnosticLog.errorLogURL, encoding: .utf8)
        XCTAssertTrue(main.contains("[test.error]"))
        XCTAssertTrue(errors.contains("[test.error]"))
        XCTAssertTrue(errors.contains("%USERPROFILE%"))
        XCTAssertTrue(errors.contains("password=***"))
        XCTAssertTrue(errors.contains("token=***"))
        XCTAssertTrue(errors.contains("https://***:***@example.test/file"))
        XCTAssertFalse(errors.contains("hunter2"))
        XCTAssertFalse(errors.contains("abc123"))
        XCTAssertFalse(errors.contains("private-bearer"))
        XCTAssertFalse(errors.contains("private-json"))
    }

    func testRotationKeepsNumberedBackups() throws {
        let logs = applicationDirectory.appendingPathComponent("Logs", isDirectory: true)
        try FileManager.default.createDirectory(at: logs, withIntermediateDirectories: true)
        let path = logs.appendingPathComponent("ytray.log")
        let previous = logs.appendingPathComponent("ytray.1.log")
        try Data(repeating: 0x61, count: 32).write(to: path)
        try Data("older".utf8).write(to: previous)

        try DiagnosticLog.rotateIfNeeded(path, maximumBytes: 16, backupCount: 2)

        XCTAssertFalse(FileManager.default.fileExists(atPath: path.path))
        XCTAssertEqual(try Data(contentsOf: logs.appendingPathComponent("ytray.1.log")).count, 32)
        XCTAssertEqual(
            try String(contentsOf: logs.appendingPathComponent("ytray.2.log"), encoding: .utf8),
            "older"
        )
    }

    func testRuntimeProgressFormatsRealBytesAndPercentage() {
        XCTAssertEqual(RuntimeInstaller.downloadPercent(bytesReceived: 50, totalBytes: 100), 50)
        XCTAssertEqual(RuntimeInstaller.downloadPercent(bytesReceived: 110, totalBytes: 100), 100)
        XCTAssertEqual(RuntimeInstaller.downloadPercent(bytesReceived: 50, totalBytes: nil), 0)
        XCTAssertEqual(RuntimeInstaller.formatBytes(1_572_864), "1.5 MB")
        let progress = RuntimeInstallProgress(
            phase: .downloading,
            percent: 50,
            bytesReceived: 1_048_576,
            totalBytes: 2_097_152,
            message: "正在下载"
        )
        XCTAssertEqual(progress.byteSummary, "1.0 MB / 2.0 MB")
    }
}
