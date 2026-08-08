import XCTest
@testable import InstanceDock

final class BrowserLauncherTests: XCTestCase {
    func testDockBadgeSequenceAndValidation() throws {
        XCTAssertEqual(DockBadgeLabel.defaultLabel(for: 1), "A")
        XCTAssertEqual(DockBadgeLabel.defaultLabel(for: 26), "Z")
        XCTAssertEqual(DockBadgeLabel.defaultLabel(for: 27), "AA")
        XCTAssertEqual(DockBadgeLabel.defaultLabel(for: 28), "AB")
        XCTAssertEqual(DockBadgeLabel.defaultLabel(for: 52), "AZ")
        XCTAssertEqual(DockBadgeLabel.defaultLabel(for: 53), "BA")
        XCTAssertEqual(DockBadgeLabel.defaultLabel(for: 702), "ZZ")
        XCTAssertEqual(try DockBadgeLabel.normalize(" ab "), "AB")
        XCTAssertThrowsError(try DockBadgeLabel.normalize(""))
        XCTAssertThrowsError(try DockBadgeLabel.normalize("ABC"))
        XCTAssertThrowsError(try DockBadgeLabel.normalize("A1"))
        XCTAssertThrowsError(try DockBadgeLabel.normalize("中"))
    }

    func testDockBadgeIconCanRenderOneOrTwoLetters() {
        let base = NSImage(size: NSSize(width: 256, height: 256))
        base.lockFocus()
        NSColor.systemBlue.setFill()
        NSBezierPath(ovalIn: NSRect(x: 16, y: 16, width: 224, height: 224)).fill()
        base.unlockFocus()

        for label in ["A", "AB"] {
            let icon = BrowserProcessIcon.renderIcon(baseIcon: base, badge: label, size: 512)
            XCTAssertEqual(icon.size, NSSize(width: 512, height: 512))
            XCTAssertNotNil(icon.tiffRepresentation)
        }
    }

    func testTrayIconHasCompactCanvasAndSafeArea() {
        let icon = TrayIconRenderer.makeImage()
        XCTAssertEqual(icon.size, NSSize(width: 18, height: 18))
        XCTAssertTrue(icon.isTemplate)
        XCTAssertGreaterThanOrEqual(TrayIconRenderer.contentBounds.minY, 0.25)
        XCTAssertLessThanOrEqual(TrayIconRenderer.contentBounds.maxY, 17.75)
        XCTAssertNotNil(icon.tiffRepresentation)
    }

    func testBrowserKindAndSystemSourceAreExplicit() {
        XCTAssertEqual(BrowserKind.infer(name: "Google Chrome for Testing", path: "/tmp/chrome"), .chromeForTesting)
        XCTAssertEqual(BrowserKind.infer(name: "Microsoft Edge", path: "/Applications/Microsoft Edge.app"), .edge)
        XCTAssertEqual(BrowserKind.infer(name: "Microsoft Edge Beta", path: "/Applications/Microsoft Edge Beta.app"), .edge)
        let runtime = BrowserRuntime(
            name: "Google Chrome",
            version: "151.0.7922.108",
            architecture: "macos-arm64",
            executablePath: "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
            source: .system,
            browserKind: .chrome
        )
        XCTAssertTrue(runtime.isSystemEnvironment)
        XCTAssertEqual(runtime.displayTitle, "Google Chrome")
        XCTAssertEqual(runtime.versionLabel, "151.0.7922.108")
    }

    func testLegacyStateWithoutBrowserMetadataStillDecodes() throws {
        let fixture = #"""
        {
          "runtimes": [{
            "id": "11111111-1111-1111-1111-111111111111",
            "name": "Google Chrome", "version": "150.0.0.0",
            "architecture": "macos-arm64",
            "executablePath": "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
            "source": "local", "createdAt": "2026-08-08T00:00:00Z"
          }],
          "plugins": [],
          "instances": [{
            "id": "22222222-2222-2222-2222-222222222222",
            "name": "浏览器实例 1",
            "runtimeID": "11111111-1111-1111-1111-111111111111",
            "runtimeName": "Google Chrome", "mode": "quick",
            "processID": 123, "debugPort": 9222,
            "profilePath": "/tmp/profile", "startURL": "chrome://newtab",
            "startedAt": "2026-08-08T00:00:00Z", "status": "stopped"
          }],
          "settings": {
            "homeURL": "chrome://newtab", "proxyServer": "", "debugPort": 9222,
            "restrictWebRTC": true, "disableNotifications": true,
            "ignoreCertificateErrors": false, "additionalFlags": "", "defaultPluginIDs": []
          }
        }
        """#
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        let state = try decoder.decode(PersistedState.self, from: Data(fixture.utf8))
        XCTAssertEqual(state.runtimes.first?.kind, .chrome)
        XCTAssertNil(state.instances.first?.runtimeVersion)
        XCTAssertEqual(state.instances.first?.runtimeKind, nil)
        XCTAssertNil(state.instances.first?.lastPageTitle)
        XCTAssertNil(state.instances.first?.dockBadge)
        XCTAssertEqual(state.settings.dockBadge, "")
    }

    func testProcessBootstrapExecutesOriginalBrowserWithoutChangingItsBundle() {
        let arguments = BrowserLauncher.buildProcessArguments(
            iconURL: URL(fileURLWithPath: "/tmp/instance-A.png"),
            browserExecutable: URL(fileURLWithPath: "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"),
            browserArguments: ["--user-data-dir=/tmp/profile", "chrome://newtab"]
        )
        XCTAssertEqual(arguments, [
            "--browser-process",
            "/tmp/instance-A.png",
            "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
            "--user-data-dir=/tmp/profile",
            "chrome://newtab",
        ])
        XCTAssertFalse(arguments.contains(where: { $0.contains("codesign") || $0.contains("xattr") }))
    }

    func testWidgetUsesCompactStateDependentHeight() {
        XCTAssertEqual(WidgetMetrics.height(runningCount: 0, historyCount: 0), 323)
        XCTAssertEqual(WidgetMetrics.height(runningCount: 1, historyCount: 0), 329)
        XCTAssertLessThan(WidgetMetrics.height(runningCount: 0, historyCount: 1), 370)
        XCTAssertEqual(
            WidgetMetrics.height(runningCount: 8, historyCount: 0),
            WidgetMetrics.height(runningCount: 4, historyCount: 0)
        )
    }

    func testWidgetPositionStaysAttachedToTrayAndInsideScreen() {
        let frame = WidgetPositioning.frame(
            size: NSSize(width: 390, height: 447),
            trayAnchor: NSRect(x: 980, y: 1_415, width: 40, height: 25),
            visibleFrame: NSRect(x: 0, y: 91, width: 1_440, height: 1_324)
        )
        XCTAssertEqual(frame.midX, 1_000, accuracy: 0.5)
        XCTAssertEqual(frame.maxY, 1_409, accuracy: 0.5)

        let rightEdge = WidgetPositioning.frame(
            size: NSSize(width: 390, height: 447),
            trayAnchor: NSRect(x: 1_420, y: 1_415, width: 20, height: 25),
            visibleFrame: NSRect(x: 0, y: 91, width: 1_440, height: 1_324)
        )
        XCTAssertEqual(rightEdge.maxX, 1_432, accuracy: 0.5)
        XCTAssertGreaterThanOrEqual(rightEdge.minY, 99)

        let appKitTray = WidgetPositioning.appKitFrame(
            fromQuartz: CGRect(x: 2_527, y: 0, width: 54, height: 24),
            primaryScreenTop: 1_440
        )
        XCTAssertEqual(appKitTray, NSRect(x: 2_527, y: 1_416, width: 54, height: 24))
    }

    func testTransientWidgetDismissalPolicy() {
        XCTAssertTrue(WidgetDismissalPolicy.shouldHide(isPinned: false, hasAttachedSheet: false))
        XCTAssertFalse(WidgetDismissalPolicy.shouldHide(isPinned: true, hasAttachedSheet: false))
        XCTAssertFalse(WidgetDismissalPolicy.shouldHide(isPinned: false, hasAttachedSheet: true))
    }

    @MainActor
    func testHistoryCanBeRenamedAndDeletedWithoutTouchingRunningInstances() {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("instance-dock-history-test-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let store = InstanceStore(applicationDirectory: directory, discoverSystemBrowsers: false)
        let runtimeID = UUID()
        let history = BrowserInstance(
            name: "浏览器实例 1", runtimeID: runtimeID, runtimeName: "Google Chrome",
            mode: .quick, processID: 0, debugPort: 9222, profilePath: "/tmp/history",
            startURL: "chrome://newtab", status: .stopped, lastPageTitle: "登录页"
        )
        let running = BrowserInstance(
            name: "浏览器实例 2", runtimeID: runtimeID, runtimeName: "Google Chrome",
            mode: .quick, processID: Int32(ProcessInfo.processInfo.processIdentifier), debugPort: 9223,
            profilePath: "/tmp/running", startURL: "chrome://newtab"
        )
        store.instances = [history, running]

        store.renameHistory(history, name: "登录调试")
        store.renameHistory(running, name: "不应修改")
        XCTAssertEqual(store.historyInstances.first?.name, "登录调试")
        XCTAssertEqual(store.runningInstances.first?.name, "浏览器实例 2")

        store.removeHistory(history)
        XCTAssertTrue(store.historyInstances.isEmpty)
        XCTAssertEqual(store.runningInstances.count, 1)
    }

    @MainActor
    func testHistoryKeepsNewestEntryPerDockBadgeAndCanClearAll() throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("instance-dock-history-merge-test-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let runtimeID = UUID()
        let older = BrowserInstance(
            name: "旧 A", runtimeID: runtimeID, runtimeName: "Google Chrome",
            mode: .quick, processID: 0, debugPort: 9222, profilePath: "/tmp/old-a",
            startURL: "chrome://newtab", startedAt: Date(timeIntervalSince1970: 1),
            status: .stopped, dockBadge: "A"
        )
        let newest = BrowserInstance(
            name: "新 A", runtimeID: runtimeID, runtimeName: "Google Chrome",
            mode: .quick, processID: 0, debugPort: 9223, profilePath: "/tmp/new-a",
            startURL: "chrome://newtab", startedAt: Date(timeIntervalSince1970: 2),
            status: .stopped, dockBadge: "A"
        )
        let running = BrowserInstance(
            name: "运行中 B", runtimeID: runtimeID, runtimeName: "Google Chrome",
            mode: .quick, processID: Int32(ProcessInfo.processInfo.processIdentifier), debugPort: 9224,
            profilePath: "/tmp/running-b", startURL: "chrome://newtab", dockBadge: "B"
        )
        let state = PersistedState(instances: [older, newest, running])
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        try encoder.encode(state).write(to: directory.appendingPathComponent("state.json"))

        let store = InstanceStore(applicationDirectory: directory, discoverSystemBrowsers: false)
        XCTAssertEqual(store.historyInstances.map(\.name), ["新 A"])
        XCTAssertEqual(store.runningInstances.map(\.name), ["运行中 B"])

        store.removeAllHistory()
        XCTAssertTrue(store.historyInstances.isEmpty)
        XCTAssertEqual(store.runningInstances.map(\.name), ["运行中 B"])
    }

    @MainActor
    func testLaunchFailureClearsLoadingStateAndReportsError() {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("instance-dock-launch-state-test-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let store = InstanceStore(applicationDirectory: directory, discoverSystemBrowsers: false)

        store.launch(mode: .quick)

        XCTAssertFalse(store.isLaunching)
        XCTAssertEqual(store.launchPhase, .idle)
        XCTAssertNil(store.launchingMode)
        XCTAssertEqual(store.launchMessage, "")
        XCTAssertNotNil(store.errorMessage)
    }

    func testMirrorManifestMatchesPublishedArrayShape() throws {
        let fixture = #"""
        {
          "generated_at": "2026-08-08T04:21:15Z",
          "versions": [{
            "version": "151.0.7922.77",
            "artifacts": [{
              "os": "macos", "arch": "arm64", "format": "zip",
              "url": "https://example.test/chrome.zip", "size": 187103225,
              "sha256": "4b3caaabb967070f1541ff5b0fd2c95b2ba839be33a58842a8a877ec5f3fbd9b"
            }]
          }]
        }
        """#
        let manifest = try JSONDecoder().decode(MirrorManifest.self, from: Data(fixture.utf8))
        XCTAssertEqual(manifest.versions.first?.artifacts.first?.os, "macos")
        XCTAssertEqual(manifest.versions.first?.artifacts.first?.arch, "arm64")
        XCTAssertEqual(manifest.versions.first?.artifacts.first?.format, "zip")
    }

    func testQuickArgumentsKeepEveryInstanceIsolatedAndDebugLocal() throws {
        var settings = LaunchSettings()
        settings.proxyServer = "http://127.0.0.1:8080"
        let arguments = try BrowserLauncher.buildArguments(
            mode: .quick,
            settings: settings,
            profilePath: "/tmp/instance-dock-test-profile",
            debugPort: 9333,
            plugins: []
        )
        XCTAssertTrue(arguments.contains("--user-data-dir=/tmp/instance-dock-test-profile"))
        XCTAssertTrue(arguments.contains("--remote-debugging-address=127.0.0.1"))
        XCTAssertTrue(arguments.contains("--remote-debugging-port=9333"))
        XCTAssertTrue(arguments.contains("--remote-allow-origins=http://127.0.0.1:9333"))
        XCTAssertTrue(arguments.contains("--no-first-run"))
        XCTAssertTrue(arguments.contains("--no-default-browser-check"))
        XCTAssertTrue(arguments.contains("--proxy-server=http://127.0.0.1:8080"))
        XCTAssertTrue(arguments.contains("--force-webrtc-ip-handling-policy=disable_non_proxied_udp"))
    }

    func testIsolatedModeDoesNotCopyProxyPluginsOrExtraFlags() throws {
        var settings = LaunchSettings()
        settings.proxyServer = "socks5://127.0.0.1:1080"
        settings.additionalFlags = "--incognito"
        let plugin = BrowserPlugin(name: "Test", version: "1", path: "/tmp/plugin", manifestVersion: 3)
        let arguments = try BrowserLauncher.buildArguments(
            mode: .isolated,
            settings: settings,
            profilePath: "/tmp/profile",
            debugPort: 9444,
            plugins: [plugin]
        )
        XCTAssertFalse(arguments.contains(where: { $0.hasPrefix("--proxy-server") }))
        XCTAssertFalse(arguments.contains(where: { $0.hasPrefix("--load-extension") }))
        XCTAssertFalse(arguments.contains("--incognito"))
        XCTAssertTrue(arguments.contains("--user-data-dir=/tmp/profile"))
    }

    func testRestoreUsesExistingProfileAndRestoresSessionWithoutOpeningExtraHomePage() throws {
        var settings = LaunchSettings()
        settings.homeURL = "https://should-not-open.example/"
        let arguments = try BrowserLauncher.buildArguments(
            mode: .quick,
            settings: settings,
            profilePath: "/tmp/existing-profile",
            debugPort: 9445,
            plugins: [],
            restoreLastSession: true
        )
        XCTAssertTrue(arguments.contains("--user-data-dir=/tmp/existing-profile"))
        XCTAssertTrue(arguments.contains("--restore-last-session"))
        XCTAssertFalse(arguments.contains("https://should-not-open.example/"))
    }

    func testCustomFlagsCannotOverrideIsolationBoundary() {
        var settings = LaunchSettings()
        settings.additionalFlags = "--user-data-dir=/tmp/shared"
        XCTAssertThrowsError(try BrowserLauncher.buildArguments(
            mode: .custom,
            settings: settings,
            profilePath: "/tmp/isolated",
            debugPort: 9555,
            plugins: []
        ))
    }

    func testUnpackedPluginsUseBothChromeFlags() throws {
        var settings = LaunchSettings()
        settings.defaultPluginIDs = []
        let plugin = BrowserPlugin(name: "Local", version: "1.0", path: "/tmp/local-extension", manifestVersion: 3)
        let arguments = try BrowserLauncher.buildArguments(
            mode: .quick,
            settings: settings,
            profilePath: "/tmp/profile",
            debugPort: 9666,
            plugins: [plugin]
        )
        XCTAssertTrue(arguments.contains("--load-extension=/tmp/local-extension"))
        XCTAssertTrue(arguments.contains("--disable-extensions-except=/tmp/local-extension"))
    }
}
