import Darwin
import XCTest
@testable import YTray

final class RuntimeArchiveExtractorTests: XCTestCase {
    func testChattyFailureDrainsMoreThanAPipeBufferAndBoundsErrorText() async throws {
        let script = "i=0; while [ \"$i\" -lt 12000 ]; do printf 'archive-error-abcdefghijklmnopqrstuvwxyz0123456789\\n' >&2; i=$((i+1)); done; exit 7"
        do {
            try await RuntimeArchiveExtractor().run(executable: URL(fileURLWithPath: "/bin/sh"), arguments: ["-c", script])
            XCTFail("Failing extractor returned success")
        } catch {
            XCTAssertTrue(error.localizedDescription.contains("archive-error"))
            XCTAssertTrue(error.localizedDescription.contains("已截断"))
            XCTAssertLessThan(error.localizedDescription.utf8.count, RuntimeArchiveExtractor.maximumErrorBytes + 200)
        }
    }

    func testCancellationTerminatesOwnedExtractionBeforeReturning() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("ytray-extractor-tests-\(UUID())")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let marker = directory.appendingPathComponent("pid")
        let task = Task {
            try await RuntimeArchiveExtractor().run(executable: URL(fileURLWithPath: "/bin/sh"),
                arguments: ["-c", "printf '%s' \"$$\" > \"$1\"; exec /bin/sleep 30", "extractor-fixture", marker.path])
        }
        let deadline = Date().addingTimeInterval(3)
        while !FileManager.default.fileExists(atPath: marker.path), Date() < deadline {
            try await Task.sleep(nanoseconds: 10_000_000)
        }
        let pid = (try? String(contentsOf: marker, encoding: .utf8)).flatMap(Int32.init)
        XCTAssertNotNil(pid, "Owned extractor did not start")
        let cancelledAt = Date()
        task.cancel()
        do { try await task.value; XCTFail("Cancelled extraction returned success") }
        catch { XCTAssertTrue(AsyncCancellation.isExpected(error), error.localizedDescription) }
        XCTAssertLessThan(Date().timeIntervalSince(cancelledAt), 2)
        if let pid { XCTAssertNotEqual(Darwin.kill(pid, 0), 0, "The cancelled child must be reaped before staging cleanup") }
    }

    func testPreCancelledExtractionDoesNotLaunchAndLaunchFailureReturns() async throws {
        let marker = FileManager.default.temporaryDirectory.appendingPathComponent("ytray-extractor-never-\(UUID())")
        defer { try? FileManager.default.removeItem(at: marker) }
        let task = Task {
            withUnsafeCurrentTask { $0?.cancel() }
            try await RuntimeArchiveExtractor().run(executable: URL(fileURLWithPath: "/bin/sh"),
                arguments: ["-c", "printf unexpected > \"$1\"", "extractor-fixture", marker.path])
        }
        do { try await task.value; XCTFail("Already cancelled extraction returned success") }
        catch { XCTAssertTrue(AsyncCancellation.isExpected(error)) }
        XCTAssertFalse(FileManager.default.fileExists(atPath: marker.path))
        do {
            try await RuntimeArchiveExtractor().run(executable: marker, arguments: [])
            XCTFail("Missing extractor returned success")
        } catch { XCTAssertFalse(AsyncCancellation.isExpected(error)) }
    }
}
