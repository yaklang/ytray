import CryptoKit
import XCTest
@testable import YTray

final class RuntimeInstallerTests: XCTestCase {
    func testCancellingRuntimeDownloadReturnsPromptlyWithoutInstalling() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("ytray-runtime-tests-\(UUID())")
        defer { try? FileManager.default.removeItem(at: directory) }
        let archive = Data("unused archive".utf8)
        let began = expectation(description: "download began")
        let port = BrowserLauncher.nextAvailablePort(startingAt: Int.random(in: 52000...58000))
        let server = MCPHTTPServer(port: port) { _ in
            began.fulfill()
            try? await Task.sleep(nanoseconds: 2_000_000_000)
            return MCPHTTPResponse(status: 200, body: archive)
        }
        try server.start()
        defer { server.stop() }
        let release = Self.release(version: "140.0.2", port: port, archive: archive)
        let install = Task { try await RuntimeInstaller.install(version: release, into: directory) }
        await fulfillment(of: [began], timeout: 3)
        let cancelledAt = Date()
        install.cancel()
        do { _ = try await install.value; XCTFail("Cancelled install returned a runtime") }
        catch { XCTAssertTrue(AsyncCancellation.isExpected(error), error.localizedDescription) }
        XCTAssertLessThan(Date().timeIntervalSince(cancelledAt), 1.5, "Cancellation must not wait for the server to finish")
        XCTAssertNil(RuntimeInstaller.locateChrome(in: directory))
    }

    func testValidatedArchiveReplacesRuntimeAndLeavesNoStagingDirectories() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("ytray-runtime-tests-\(UUID())")
        defer { try? FileManager.default.removeItem(at: directory) }
        let source = directory.appendingPathComponent("source/Chrome.app/Contents/MacOS/Chrome")
        try FileManager.default.createDirectory(at: source.deletingLastPathComponent(), withIntermediateDirectories: true)
        let replacement = Data("#!/bin/sh\n# new working runtime\n".utf8)
        try replacement.write(to: source)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: source.path)
        let zip = directory.appendingPathComponent("valid.zip")
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
        process.arguments = ["-c", "-k", "--keepParent", source.deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().path, zip.path]
        try process.run(); process.waitUntilExit()
        XCTAssertEqual(process.terminationStatus, 0)
        let archive = try Data(contentsOf: zip)
        let port = BrowserLauncher.nextAvailablePort(startingAt: Int.random(in: 52000...58000))
        let server = MCPHTTPServer(port: port) { _ in MCPHTTPResponse(status: 200, body: archive) }
        try server.start()
        defer { server.stop() }
        let destination = directory.appendingPathComponent("installed")
        let versionDirectory = destination.appendingPathComponent("Runtimes/140.0.3")
        let previous = versionDirectory.appendingPathComponent(RuntimeInstaller.platform + "/old-marker")
        try FileManager.default.createDirectory(at: previous.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data("old runtime".utf8).write(to: previous)
        let runtime = try await RuntimeInstaller.install(version: Self.release(version: "140.0.3", port: port, archive: archive), into: destination)
        XCTAssertEqual(try Data(contentsOf: URL(fileURLWithPath: runtime.executablePath)), replacement)
        XCTAssertFalse(FileManager.default.fileExists(atPath: previous.path))
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: versionDirectory.path), [RuntimeInstaller.platform])
    }

    func testFailedReplacementPreservesInstalledRuntime() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("ytray-runtime-tests-\(UUID())")
        defer { try? FileManager.default.removeItem(at: directory) }
        let version = "140.0.1"
        let installed = directory.appendingPathComponent("Runtimes/\(version)/\(RuntimeInstaller.platform)/Chrome.app/Contents/MacOS/Chrome")
        try FileManager.default.createDirectory(at: installed.deletingLastPathComponent(), withIntermediateDirectories: true)
        let original = Data("#!/bin/sh\n# working runtime\n".utf8)
        try original.write(to: installed)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: installed.path)
        let archive = Data("This file has the advertised checksum but is not a ZIP.".utf8)
        let port = BrowserLauncher.nextAvailablePort(startingAt: Int.random(in: 52000...58000))
        let server = MCPHTTPServer(port: port) { _ in MCPHTTPResponse(status: 200, body: archive) }
        try server.start()
        defer { server.stop() }
        let release = Self.release(version: version, port: port, archive: archive)
        do { _ = try await RuntimeInstaller.install(version: release, into: directory); XCTFail("Invalid archive installed") } catch { }
        XCTAssertTrue(FileManager.default.fileExists(atPath: installed.path), "A failed repair must preserve the working browser")
        XCTAssertEqual(try? Data(contentsOf: installed), original)
    }

    private static func release(version: String, port: Int, archive: Data) -> MirrorVersion {
        MirrorVersion(version: version, artifacts: [MirrorArtifact(os: "macos", arch: RuntimeInstaller.architecture, format: "zip",
            url: "http://127.0.0.1:\(port)/runtime.zip", size: Int64(archive.count), sha256: SHA256.hash(data: archive).map { String(format: "%02x", $0) }.joined())])
    }
}
