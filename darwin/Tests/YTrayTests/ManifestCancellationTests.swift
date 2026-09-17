import Foundation
import XCTest
@testable import YTray

final class ManifestCancellationTests: XCTestCase {
    @MainActor
    func testPageCancellationDoesNotPresentAnError() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        let store = InstanceStore(applicationDirectory: directory, discoverSystemBrowsers: false, monitorProcesses: false)
        for error: Error in [CancellationError(), URLError(.cancelled),
                            NSError(domain: NSURLErrorDomain, code: NSURLErrorCancelled)] {
            await store.refreshManifest { throw error }
            XCTAssertNil(store.errorMessage)
            await store.refreshExtensionManifest { throw error }
            XCTAssertNil(store.errorMessage)
            XCTAssertEqual(store.extensionStatusMessage, "")
        }
    }

    @MainActor
    func testCancellingAnInFlightPageTaskDoesNotPresentAnError() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        let store = InstanceStore(applicationDirectory: directory, discoverSystemBrowsers: false, monitorProcesses: false)
        let entered = expectation(description: "manifest request started")
        let task = Task {
            await store.refreshManifest {
                entered.fulfill()
                try await Task.sleep(nanoseconds: 30_000_000_000)
                return []
            }
        }
        await fulfillment(of: [entered], timeout: 2)
        task.cancel()
        await task.value
        XCTAssertNil(store.errorMessage)
        XCTAssertTrue(store.availableVersions.isEmpty)
    }

    @MainActor
    func testRealNetworkFailuresAreStillReported() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        let store = InstanceStore(applicationDirectory: directory, discoverSystemBrowsers: false, monitorProcesses: false)
        let error = URLError(.notConnectedToInternet)
        await store.refreshManifest { throw error }
        XCTAssertEqual(store.errorMessage, error.localizedDescription)
        store.errorMessage = nil
        await store.refreshExtensionManifest { throw error }
        XCTAssertEqual(store.errorMessage, error.localizedDescription)
        XCTAssertEqual(store.extensionStatusMessage, error.localizedDescription)
    }
}
