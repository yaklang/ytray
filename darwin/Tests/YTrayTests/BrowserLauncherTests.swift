import XCTest
import Network
@testable import YTray

final class BrowserLauncherTests: XCTestCase {
    @MainActor
    func testEnabledPluginsBecomeDefaultsForNewInstances() throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("ytray-plugin-default-test-\(UUID().uuidString)", isDirectory: true)
        let pluginDirectory = directory.appendingPathComponent("extension", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        try FileManager.default.createDirectory(at: pluginDirectory, withIntermediateDirectories: true)
        try Data(#"{"name":"Local","version":"1.0","manifest_version":3}"#.utf8)
            .write(to: pluginDirectory.appendingPathComponent("manifest.json"))

        let store = InstanceStore(applicationDirectory: directory, discoverSystemBrowsers: false)
        store.addPlugin(directory: pluginDirectory)
        var plugin = try XCTUnwrap(store.plugins.first)
        XCTAssertEqual(store.settings.defaultPluginIDs, [plugin.id])

        plugin.enabled = false
        store.updatePlugin(plugin)
        XCTAssertTrue(store.settings.defaultPluginIDs.isEmpty)

        plugin.enabled = true
        store.updatePlugin(plugin)
        XCTAssertEqual(store.settings.defaultPluginIDs, [plugin.id])
    }

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
        XCTAssertNil(state.instances.first?.thumbnailPath)
        XCTAssertNil(state.instances.first?.thumbnailUpdatedAt)
        XCTAssertNil(state.instances.first?.dockBadge)
        XCTAssertEqual(state.settings.dockBadge, "")
        XCTAssertTrue(state.settings.ignoreCertificateErrors)
        XCTAssertEqual(state.settings.presetProxyServer, "http://127.0.0.1:8083")
    }

    func testCurrentSettingsRespectUserChoiceAfterCertificateDefaultMigration() throws {
        let fixture = #"{"configurationVersion":2,"ignoreCertificateErrors":false}"#
        let settings = try JSONDecoder().decode(LaunchSettings.self, from: Data(fixture.utf8))
        XCTAssertFalse(settings.ignoreCertificateErrors)
        XCTAssertEqual(settings.configurationVersion, LaunchSettings.currentConfigurationVersion)
    }

    @MainActor
    func testPreviewApplicationDataMigratesToYTrayWithoutLosingManagedPaths() throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("ytray-brand-migration-test-\(UUID().uuidString)", isDirectory: true)
        let legacy = root.appendingPathComponent("legacy", isDirectory: true)
        let current = root.appendingPathComponent("YTray", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: legacy, withIntermediateDirectories: true)

        let runtime = BrowserRuntime(
            name: "Chrome for Testing",
            version: "151.0.0.0",
            architecture: "macos-arm64",
            executablePath: legacy.appendingPathComponent("Runtimes/151/chrome").path,
            source: .managed,
            browserKind: .chromeForTesting
        )
        let plugin = BrowserPlugin(
            name: "Local extension",
            version: "1.0",
            path: legacy.appendingPathComponent("Plugins/local-extension").path,
            manifestVersion: 3
        )
        let instance = BrowserInstance(
            name: "管理员身份",
            runtimeID: runtime.id,
            runtimeName: runtime.name,
            mode: .quick,
            processID: 0,
            debugPort: 9222,
            profilePath: legacy.appendingPathComponent("Profiles/admin").path,
            startURL: "https://example.com/admin",
            status: .stopped,
            lastScreenshotPath: legacy.appendingPathComponent("Screenshots/admin.png").path,
            thumbnailPath: legacy.appendingPathComponent("Thumbnails/admin.png").path
        )
        var settings = LaunchSettings()
        settings.defaultRuntimeID = runtime.id
        settings.presetProxyRemark = "Yak MITM"
        let state = PersistedState(
            runtimes: [runtime],
            plugins: [plugin],
            instances: [instance],
            settings: settings
        )
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        try encoder.encode(state).write(to: legacy.appendingPathComponent("state.json"))

        let store = InstanceStore(
            applicationDirectory: current,
            discoverSystemBrowsers: false,
            legacyApplicationDirectory: legacy
        )

        XCTAssertFalse(FileManager.default.fileExists(atPath: legacy.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: current.appendingPathComponent("state.json").path))
        XCTAssertEqual(store.runtimes.first?.executablePath,
                       current.appendingPathComponent("Runtimes/151/chrome").path)
        XCTAssertEqual(store.plugins.first?.path,
                       current.appendingPathComponent("Plugins/local-extension").path)
        XCTAssertEqual(store.instances.first?.profilePath,
                       current.appendingPathComponent("Profiles/admin").path)
        XCTAssertEqual(store.instances.first?.thumbnailPath,
                       current.appendingPathComponent("Thumbnails/admin.png").path)
        XCTAssertEqual(store.instances.first?.lastScreenshotPath,
                       current.appendingPathComponent("Screenshots/admin.png").path)
        XCTAssertEqual(store.settings.defaultRuntimeID, runtime.id)
        XCTAssertEqual(store.settings.presetProxyRemark, "Yak MITM")
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
        XCTAssertEqual(WidgetMetrics.height(runningCount: 0, historyCount: 0), 468)
        XCTAssertEqual(WidgetMetrics.height(runningCount: 1, historyCount: 0), 474)
        XCTAssertEqual(
            WidgetMetrics.height(
                runningCount: 0,
                historyCount: 0,
                proxyAdvancedExpanded: true
            ),
            544
        )
        XCTAssertLessThan(WidgetMetrics.height(runningCount: 0, historyCount: 1), 560)
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

    func testEdgeDockDefaultsAboveCapTrayAndClampsInsideScreen() {
        XCTAssertEqual(EdgeDockPreferences.defaultYPercent, 58)
        let screen = NSRect(x: 0, y: 0, width: 1_440, height: 900)
        let right = EdgeDockPositioning.frame(
            screenFrame: screen,
            onLeft: false,
            yPercent: EdgeDockPreferences.defaultYPercent
        )
        XCTAssertEqual(right.maxX, screen.maxX)
        XCTAssertEqual(right.midY, screen.height * 0.58, accuracy: 0.5)

        let left = EdgeDockPositioning.frame(
            screenFrame: screen,
            onLeft: true,
            yPercent: 100
        )
        XCTAssertEqual(left.minX, screen.minX)
        XCTAssertLessThanOrEqual(left.maxY, screen.maxY - EdgeDockPositioning.screenMargin)
    }

    func testFullWidgetOpensInsideAndBesideEitherScreenEdge() {
        let visible = NSRect(x: 0, y: 25, width: 1_440, height: 850)
        let size = NSSize(width: 390, height: 468)
        let rightTab = NSRect(x: 1_410, y: 470, width: 30, height: 112)
        let right = EdgeWidgetPositioning.frame(
            size: size,
            tabFrame: rightTab,
            onLeft: false,
            visibleFrame: visible
        )
        XCTAssertEqual(right.maxX, rightTab.minX - EdgeWidgetPositioning.gap)
        XCTAssertGreaterThanOrEqual(right.minY, visible.minY + EdgeWidgetPositioning.screenMargin)

        let leftTab = NSRect(x: 0, y: 470, width: 30, height: 112)
        let left = EdgeWidgetPositioning.frame(
            size: size,
            tabFrame: leftTab,
            onLeft: true,
            visibleFrame: visible
        )
        XCTAssertEqual(left.minX, leftTab.maxX + EdgeWidgetPositioning.gap)
        XCTAssertLessThanOrEqual(left.maxY, visible.maxY - EdgeWidgetPositioning.screenMargin)
    }

    func testEdgeLaunchButtonsOnlyAppearWhileWidgetIsClosed() {
        XCTAssertTrue(EdgeDockVisibilityPolicy.shouldShowLaunchButtons(
            widgetPresented: false,
            tabHovered: true
        ))
        XCTAssertFalse(EdgeDockVisibilityPolicy.shouldShowLaunchButtons(
            widgetPresented: true,
            tabHovered: true
        ))
        XCTAssertFalse(EdgeDockVisibilityPolicy.shouldShowLaunchButtons(
            widgetPresented: false,
            tabHovered: false
        ))
    }

    func testTransientWidgetDismissalPolicy() {
        XCTAssertTrue(WidgetDismissalPolicy.shouldHide(isPinned: false, hasAttachedSheet: false))
        XCTAssertFalse(WidgetDismissalPolicy.shouldHide(isPinned: true, hasAttachedSheet: false))
        XCTAssertFalse(WidgetDismissalPolicy.shouldHide(isPinned: false, hasAttachedSheet: true))
        XCTAssertFalse(WidgetDismissalPolicy.shouldHide(
            isPinned: false,
            hasAttachedSheet: false,
            isBusy: true
        ))
    }

    @MainActor
    func testHistoryCanBeRenamedAndDeletedWithoutTouchingRunningInstances() {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("ytray-history-test-\(UUID().uuidString)", isDirectory: true)
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
            .appendingPathComponent("ytray-history-merge-test-\(UUID().uuidString)", isDirectory: true)
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
    func testRemovingHistoryDeletesManagedThumbnailButKeepsExportedScreenshot() throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("ytray-thumbnail-cleanup-test-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let instanceID = UUID()
        let thumbnail = InstanceThumbnailStorage.thumbnailURL(
            for: instanceID,
            applicationDirectory: directory
        )
        try FileManager.default.createDirectory(
            at: thumbnail.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try Data("thumbnail".utf8).write(to: thumbnail)
        let exported = directory.appendingPathComponent("exported.png")
        try Data("exported".utf8).write(to: exported)
        let history = BrowserInstance(
            id: instanceID,
            name: "历史",
            runtimeID: UUID(),
            runtimeName: "Chrome",
            mode: .quick,
            processID: 0,
            debugPort: 9222,
            profilePath: "/tmp/history",
            startURL: "chrome://newtab",
            status: .stopped,
            lastScreenshotPath: exported.path,
            thumbnailPath: thumbnail.path,
            thumbnailUpdatedAt: Date()
        )
        let store = InstanceStore(applicationDirectory: directory, discoverSystemBrowsers: false)
        store.instances = [history]

        store.removeHistory(history)

        XCTAssertFalse(FileManager.default.fileExists(atPath: thumbnail.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: exported.path))
    }

    @MainActor
    func testLaunchFailureClearsLoadingStateAndReportsError() {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("ytray-launch-state-test-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let store = InstanceStore(applicationDirectory: directory, discoverSystemBrowsers: false)

        store.launch(mode: .quick)

        XCTAssertFalse(store.isLaunching)
        XCTAssertEqual(store.launchPhase, .idle)
        XCTAssertNil(store.launchingMode)
        XCTAssertEqual(store.launchMessage, "")
        XCTAssertNotNil(store.errorMessage)
    }

    func testHTTPProxyNormalizationOnlyAcceptsHTTPProxyAddresses() throws {
        XCTAssertEqual(try HTTPProxyAddress.normalize("127.0.0.1:8083"), "http://127.0.0.1:8083")
        XCTAssertEqual(try HTTPProxyAddress.normalize(" HTTPS://proxy.example:8443/ "), "https://proxy.example:8443")
        XCTAssertEqual(try HTTPProxyAddress.normalize("http://[::1]:8083"), "http://[::1]:8083")
        XCTAssertThrowsError(try HTTPProxyAddress.normalize(""))
        XCTAssertThrowsError(try HTTPProxyAddress.normalize("socks5://127.0.0.1:1080"))
        XCTAssertThrowsError(try HTTPProxyAddress.normalize("http://proxy.example/path"))
        XCTAssertThrowsError(try HTTPProxyAddress.normalize("http://proxy.example:99999"))
        XCTAssertThrowsError(try HTTPProxyAddress.normalize("http://proxy.example:"))
        XCTAssertThrowsError(try HTTPProxyAddress.normalize("http://user:secret@proxy.example:8080"))

        XCTAssertEqual(
            try HTTPProxyAddress.build(scheme: .https, host: "proxy.example", port: 8443),
            "https://proxy.example:8443"
        )
        let endpoint = try HTTPProxyAddress.split("http://[::1]:8083")
        XCTAssertEqual(endpoint.scheme, .http)
        XCTAssertEqual(endpoint.host, "::1")
        XCTAssertEqual(endpoint.port, 8083)
    }

    @MainActor
    func testProxyPresetHistoryKeepsFiveRecentEntriesWithRemarks() {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("ytray-proxy-history-test-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let store = InstanceStore(applicationDirectory: directory, discoverSystemBrowsers: false)
        XCTAssertEqual(store.settings.presetProxyServer, "http://127.0.0.1:8083")
        XCTAssertTrue(store.settings.ignoreCertificateErrors)

        for index in 1...6 {
            store.updatePresetProxyServer("127.0.0.1:\(8_000 + index)")
            store.updatePresetProxyRemark("代理 \(index)")
            store.updatePresetProxyUsername("user-\(index)")
            store.updatePresetProxyPassword("password-\(index)")
            XCTAssertNotNil(store.rememberPresetProxy())
        }
        XCTAssertEqual(store.settings.recentProxyPresets.count, 5)
        XCTAssertEqual(store.settings.recentProxyPresets.first?.server, "http://127.0.0.1:8006")
        XCTAssertEqual(store.settings.recentProxyPresets.first?.remark, "代理 6")
        XCTAssertEqual(store.settings.recentProxyPresets.first?.username, "user-6")
        XCTAssertEqual(store.settings.recentProxyPresets.first?.password, "password-6")
        XCTAssertFalse(store.settings.recentProxyPresets.contains { $0.server.hasSuffix(":8001") })

        store.updatePresetProxyRemark("最新备注")
        XCTAssertNotNil(store.rememberPresetProxy())
        XCTAssertEqual(store.settings.recentProxyPresets.count, 5)
        XCTAssertEqual(store.settings.recentProxyPresets.first?.remark, "最新备注")

        store.updatePresetProxyUsername("")
        store.updatePresetProxyPassword("")
        store.selectProxyPreset(store.settings.recentProxyPresets[0])
        XCTAssertEqual(store.settings.presetProxyUsername, "user-6")
        XCTAssertEqual(store.settings.presetProxyPassword, "password-6")

        XCTAssertEqual(store.quickLaunchConfiguration(usePresetProxy: true)?.proxyServer,
                       "http://127.0.0.1:8006")
        XCTAssertEqual(store.quickLaunchConfiguration(usePresetProxy: false)?.proxyServer, "")
    }

    @MainActor
    func testProxyPresetAndBrowserSelectionPersistAcrossRestart() {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("ytray-proxy-persistence-test-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let runtime = BrowserRuntime(
            name: "Chrome for Testing",
            version: "151.0.0.0",
            architecture: "macos-arm64",
            executablePath: "/not/launched/chrome",
            source: .managed,
            browserKind: .chromeForTesting
        )

        do {
            let store = InstanceStore(applicationDirectory: directory, discoverSystemBrowsers: false)
            store.runtimes = [runtime]
            store.selectDefaultRuntime(runtime)
            store.updatePresetProxyServer("127.0.0.1:8083")
            store.updatePresetProxyRemark("Yak MITM")
            store.updatePresetProxyUsername("yak-user")
            store.updatePresetProxyPassword("session-only-secret")
            store.updatePresetProxyCheckTarget("internal.example.test/login")
            XCTAssertEqual(store.rememberPresetProxy(), "http://127.0.0.1:8083")
        }

        let restored = InstanceStore(applicationDirectory: directory, discoverSystemBrowsers: false)
        XCTAssertEqual(restored.settings.defaultRuntimeID, runtime.id)
        XCTAssertEqual(restored.settings.presetProxyServer, "http://127.0.0.1:8083")
        XCTAssertEqual(restored.settings.presetProxyRemark, "Yak MITM")
        XCTAssertEqual(restored.settings.presetProxyUsername, "yak-user")
        XCTAssertEqual(restored.settings.presetProxyPassword, "session-only-secret")
        XCTAssertEqual(restored.settings.presetProxyCheckTarget, "internal.example.test/login")
        XCTAssertEqual(restored.settings.recentProxyPresets.count, 1)
        XCTAssertEqual(restored.settings.recentProxyPresets.first?.remark, "Yak MITM")
        XCTAssertEqual(restored.settings.recentProxyPresets.first?.username, "yak-user")
        XCTAssertEqual(restored.settings.recentProxyPresets.first?.password, "session-only-secret")
        XCTAssertTrue(restored.runningInstances.isEmpty)

        let stateData = try? Data(contentsOf: directory.appendingPathComponent("state.json"))
        XCTAssertTrue(String(data: stateData ?? Data(), encoding: .utf8)?.contains("session-only-secret") ?? false)
        let attributes = try? FileManager.default.attributesOfItem(
            atPath: directory.appendingPathComponent("state.json").path
        )
        let permissions = (attributes?[.posixPermissions] as? NSNumber)?.intValue
        XCTAssertEqual(permissions, 0o600)
    }

    func testLegacyProxyPresetWithoutPasswordStillDecodes() throws {
        let data = Data(#"{"server":"http://127.0.0.1:8083","remark":"旧代理","username":"yak"}"#.utf8)
        let preset = try JSONDecoder().decode(ProxyPreset.self, from: data)
        XCTAssertEqual(preset.server, "http://127.0.0.1:8083")
        XCTAssertEqual(preset.username, "yak")
        XCTAssertEqual(preset.password, "")
    }

    func testProxyProbeBuildsBasicAuthenticationAndInterpretsResponses() {
        let request = ProxyConnectivityChecker.probeRequest(username: "yak", password: "secret")
        let requestText = String(data: request, encoding: .utf8) ?? ""
        XCTAssertTrue(requestText.hasPrefix("CONNECT example.com:443 HTTP/1.1"))
        XCTAssertTrue(requestText.contains("Proxy-Authorization: Basic eWFrOnNlY3JldA=="))

        let customTarget = try? XCTUnwrap(ProxyConnectivityChecker.normalizeTarget("baidu.com/search"))
        let customRequest = customTarget.map {
            ProxyConnectivityChecker.probeRequest(target: $0, username: "", password: "")
        }
        XCTAssertTrue(String(data: customRequest ?? Data(), encoding: .utf8)?
            .hasPrefix("CONNECT baidu.com:443 HTTP/1.1") ?? false)

        XCTAssertEqual(
            ProxyConnectivityChecker.interpretResponse(
                Data("HTTP/1.1 200 Connection established\r\n\r\n".utf8),
                usedCredentials: true
            ),
            ProxyCheckResult(isSuccess: true, message: "检测成功 · 代理和认证可用")
        )
        XCTAssertEqual(
            ProxyConnectivityChecker.interpretResponse(
                Data("HTTP/1.1 407 Proxy Authentication Required\r\n\r\n".utf8),
                usedCredentials: true
            ),
            ProxyCheckResult(isSuccess: false, message: "认证失败 · 请检查用户名和密码")
        )
    }

    func testProxyTargetsNormalizeDomainsAndAnySuccessMakesReportSuccessful() throws {
        XCTAssertEqual(ProxyConnectivityChecker.defaultTimeout, 10)
        XCTAssertEqual(ProxyConnectivityChecker.defaultTargetStrings.count, 3)
        XCTAssertEqual(
            try ProxyConnectivityChecker.normalizeTarget("example.org/path?q=1").absoluteString,
            "https://example.org/path?q=1"
        )
        XCTAssertEqual(
            try ProxyConnectivityChecker.normalizeTarget("http://127.0.0.1:8080/health").absoluteString,
            "http://127.0.0.1:8080/health"
        )
        XCTAssertThrowsError(try ProxyConnectivityChecker.normalizeTarget("ftp://example.org"))

        let report = ProxyCheckReport(details: [
            ProxyCheckDetail(target: "https://example.com/", isSuccess: false,
                             message: "超时", elapsedMilliseconds: 10_000),
            ProxyCheckDetail(target: "https://www.baidu.com/", isSuccess: true,
                             message: "检测成功", elapsedMilliseconds: 120),
            ProxyCheckDetail(target: "https://www.google.com/", isSuccess: false,
                             message: "连接失败", elapsedMilliseconds: 500),
        ])
        XCTAssertTrue(report.isSuccess)
        XCTAssertEqual(report.successCount, 1)
        XCTAssertEqual(report.message, "检测成功 · 1/3 个目标可访问")
    }

    func testProxyCheckerPerformsRealConnectAndDetectsAuthenticationFailure() async throws {
        let listener = try NWListener(using: .tcp, on: .any)
        let queue = DispatchQueue(label: "ytray-tests.proxy-listener")
        let ready = expectation(description: "proxy listener ready")
        listener.stateUpdateHandler = { state in
            if case .ready = state { ready.fulfill() }
        }
        listener.newConnectionHandler = { connection in
            connection.start(queue: queue)
            connection.receive(minimumIncompleteLength: 1, maximumLength: 8_192) { data, _, _, _ in
                let request = String(data: data ?? Data(), encoding: .utf8) ?? ""
                let authorized = request.contains("Proxy-Authorization: Basic eWFrOnNlY3JldA==")
                let response = authorized
                    ? "HTTP/1.1 200 Connection established\r\n\r\n"
                    : "HTTP/1.1 407 Proxy Authentication Required\r\n\r\n"
                connection.send(content: Data(response.utf8), completion: .contentProcessed { _ in
                    connection.cancel()
                })
            }
        }
        listener.start(queue: queue)
        await fulfillment(of: [ready], timeout: 2)
        let port = try XCTUnwrap(listener.port).rawValue
        let endpoint = ProxyEndpoint(
            scheme: .http,
            host: "127.0.0.1",
            port: Int(port),
            server: "http://127.0.0.1:\(port)"
        )

        let success = await ProxyConnectivityChecker.check(
            endpoint: endpoint,
            username: "yak",
            password: "secret",
            timeout: 2
        )
        XCTAssertTrue(success.isSuccess)

        let rejected = await ProxyConnectivityChecker.check(
            endpoint: endpoint,
            username: "yak",
            password: "wrong",
            timeout: 2
        )
        XCTAssertFalse(rejected.isSuccess)
        XCTAssertTrue(rejected.message.contains("认证失败"))

        let report = await ProxyConnectivityChecker.checkDefaultTargets(
            endpoint: endpoint,
            username: "yak",
            password: "secret",
            customTarget: "internal.example.test/health",
            timeout: 2
        )
        XCTAssertTrue(report.isSuccess)
        XCTAssertEqual(report.details.count, 4)
        XCTAssertEqual(report.successCount, 4)
        XCTAssertEqual(report.details.last?.target, "https://internal.example.test/health")
        listener.cancel()
    }

    func testProxyAuthenticationExtensionUsesRestrictedFilesAndLoadsInternally() throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("ytray-proxy-auth-extension-test-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let instanceID = UUID()
        let extensionURL = try XCTUnwrap(ProxyAuthenticationExtension.write(
            instanceID: instanceID,
            username: "yak-user",
            password: "secret-value",
            applicationDirectory: directory
        ))
        let manifest = try String(contentsOf: extensionURL.appendingPathComponent("manifest.json"))
        let script = try String(contentsOf: extensionURL.appendingPathComponent("background.js"))
        XCTAssertTrue(manifest.contains("webRequestAuthProvider"))
        XCTAssertTrue(script.contains("secret-value"))
        XCTAssertTrue(script.contains("details.isProxy"))

        let scriptAttributes = try FileManager.default.attributesOfItem(
            atPath: extensionURL.appendingPathComponent("background.js").path
        )
        XCTAssertEqual((scriptAttributes[.posixPermissions] as? NSNumber)?.intValue, 0o600)

        let arguments = try BrowserLauncher.buildArguments(
            mode: .quick,
            settings: LaunchSettings(),
            profilePath: "/tmp/proxy-auth-profile",
            debugPort: 9340,
            plugins: [],
            internalExtensionPaths: [extensionURL.path]
        )
        XCTAssertTrue(arguments.contains("--load-extension=\(extensionURL.path)"))
        XCTAssertTrue(arguments.contains("--disable-extensions-except=\(extensionURL.path)"))

        ProxyAuthenticationExtension.remove(instanceID: instanceID, applicationDirectory: directory)
        XCTAssertFalse(FileManager.default.fileExists(atPath: extensionURL.path))
    }

    func testOfficialChromeAndChromeForTestingExtensionCapabilitiesAreExplicit() {
        XCTAssertFalse(BrowserLauncher.supportsCommandLineExtensions(runtimeKind: .chrome))
        XCTAssertFalse(BrowserLauncher.supportsCommandLineExtensions(runtimeKind: .chromeBeta))
        XCTAssertFalse(BrowserLauncher.supportsCommandLineExtensions(runtimeKind: .chromeCanary))
        XCTAssertTrue(BrowserLauncher.supportsCommandLineExtensions(runtimeKind: .chromeForTesting))
        XCTAssertTrue(BrowserLauncher.supportsCommandLineExtensions(runtimeKind: .chromium))
        XCTAssertTrue(BrowserLauncher.supportsCommandLineExtensions(runtimeKind: .edge))
    }

    func testRealChromeForTestingUsesGeneratedProxyAuthenticationExtensionWhenConfigured() async throws {
        guard let executablePath = ProcessInfo.processInfo.environment["YTRAY_CFT_PATH"],
              FileManager.default.isExecutableFile(atPath: executablePath) else {
            throw XCTSkip("Set YTRAY_CFT_PATH to run the real authenticated-proxy smoke test")
        }

        let listener = try NWListener(using: .tcp, on: .any)
        let queue = DispatchQueue(label: "ytray-tests.authenticated-proxy")
        let ready = expectation(description: "authenticated proxy ready")
        let authorizedProbe = expectation(description: "Chrome sent authenticated proxy request")
        let fulfillAuthorizedProbe = TestOnce { authorizedProbe.fulfill() }
        listener.stateUpdateHandler = { state in
            if case .ready = state { ready.fulfill() }
        }
        listener.newConnectionHandler = { connection in
            connection.start(queue: queue)
            connection.receive(minimumIncompleteLength: 1, maximumLength: 16_384) { data, _, _, _ in
                let request = String(data: data ?? Data(), encoding: .utf8) ?? ""
                let authorized = request.contains("Proxy-Authorization: Basic eWFrOnNlY3JldA==")
                if authorized && request.contains("ytray-auth.invalid/probe") {
                    fulfillAuthorizedProbe.run()
                }
                let body = "<html><head><title>Proxy Auth Ready</title></head><body>ok</body></html>"
                let response: String
                if authorized {
                    response = "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: \(body.utf8.count)\r\nConnection: close\r\n\r\n\(body)"
                } else {
                    response = "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"YTray\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                }
                connection.send(content: Data(response.utf8), completion: .contentProcessed { _ in
                    connection.cancel()
                })
            }
        }
        listener.start(queue: queue)
        await fulfillment(of: [ready], timeout: 2)
        defer { listener.cancel() }
        let proxyPort = try XCTUnwrap(listener.port).rawValue

        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("ytray-real-proxy-auth-test-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let instanceID = UUID()
        let extensionURL = try XCTUnwrap(ProxyAuthenticationExtension.write(
            instanceID: instanceID,
            username: "yak",
            password: "secret",
            applicationDirectory: directory
        ))
        let debugPort = BrowserLauncher.nextAvailablePort(startingAt: 18_383)
        var settings = LaunchSettings()
        settings.proxyServer = "http://127.0.0.1:\(proxyPort)"
        settings.homeURL = "data:text/html,<title>Proxy Auth Bootstrap</title>"
        // Chrome's headless mode does not reliably activate unpacked extension
        // service workers. Keep the opt-in integration test offscreen while
        // exercising the same extension path used by the desktop app.
        settings.additionalFlags = "--disable-gpu\n--window-position=-20000,-20000\n--window-size=800,600"
        let profile = directory.appendingPathComponent("Profile", isDirectory: true)
        let arguments = try BrowserLauncher.buildArguments(
            mode: .quick,
            settings: settings,
            profilePath: profile.path,
            debugPort: debugPort,
            plugins: [],
            internalExtensionPaths: [extensionURL.path]
        )
        let process = Process()
        process.executableURL = URL(fileURLWithPath: executablePath)
        process.arguments = arguments
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        try process.run()
        defer {
            if process.isRunning { process.terminate() }
            ProxyAuthenticationExtension.remove(instanceID: instanceID, applicationDirectory: directory)
        }

        let browserReady = await ScreenshotService.waitUntilReady(debugPort: debugPort)
        XCTAssertTrue(browserReady)
        try await Task.sleep(nanoseconds: 900_000_000)
        try await ScreenshotService.navigate(
            debugPort: debugPort,
            to: "http://ytray-auth.invalid/probe"
        )
        let title = await ScreenshotService.currentPageTitle(debugPort: debugPort, attempts: 60)
        await fulfillment(of: [authorizedProbe], timeout: 8)
        XCTAssertEqual(title, "Proxy Auth Ready")
    }

    @MainActor
    func testSelectingBrowserForQuickConfigurationDoesNotLaunchIt() {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("ytray-runtime-selection-test-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let store = InstanceStore(applicationDirectory: directory, discoverSystemBrowsers: false)
        let runtime = BrowserRuntime(
            name: "Chrome for Testing",
            version: "151.0.0.0",
            architecture: "macos-arm64",
            executablePath: "/not/launched/chrome",
            source: .managed,
            browserKind: .chromeForTesting
        )
        store.runtimes = [runtime]

        store.selectDefaultRuntime(runtime)

        XCTAssertEqual(store.settings.defaultRuntimeID, runtime.id)
        XCTAssertTrue(store.runningInstances.isEmpty)
        XCTAssertFalse(store.isLaunching)
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
            profilePath: "/tmp/ytray-test-profile",
            debugPort: 9333,
            plugins: []
        )
        XCTAssertTrue(arguments.contains("--user-data-dir=/tmp/ytray-test-profile"))
        XCTAssertTrue(arguments.contains("--remote-debugging-address=127.0.0.1"))
        XCTAssertTrue(arguments.contains("--remote-debugging-port=9333"))
        XCTAssertTrue(arguments.contains("--remote-allow-origins=http://127.0.0.1:9333"))
        XCTAssertTrue(arguments.contains("--no-first-run"))
        XCTAssertTrue(arguments.contains("--no-default-browser-check"))
        XCTAssertTrue(arguments.contains("--proxy-server=http://127.0.0.1:8080"))
        XCTAssertFalse(arguments.contains("--no-proxy-server"))
        XCTAssertTrue(arguments.contains("--force-webrtc-ip-handling-policy=disable_non_proxied_udp"))
        XCTAssertTrue(arguments.contains("--ignore-certificate-errors"))
    }

    func testNoProxyAndHTTPProxyLaunchesHaveExclusiveNetworkArguments() throws {
        var directSettings = LaunchSettings()
        directSettings.proxyServer = ""
        let directArguments = try BrowserLauncher.buildArguments(
            mode: .quick,
            settings: directSettings,
            profilePath: "/tmp/direct-profile",
            debugPort: 9336,
            plugins: []
        )
        XCTAssertTrue(directArguments.contains("--no-proxy-server"))
        XCTAssertFalse(directArguments.contains(where: { $0.hasPrefix("--proxy-server=") }))

        var proxySettings = LaunchSettings()
        proxySettings.proxyServer = "http://127.0.0.1:8083"
        let proxyArguments = try BrowserLauncher.buildArguments(
            mode: .quick,
            settings: proxySettings,
            profilePath: "/tmp/proxy-profile",
            debugPort: 9337,
            plugins: []
        )
        XCTAssertTrue(proxyArguments.contains("--proxy-server=http://127.0.0.1:8083"))
        XCTAssertFalse(proxyArguments.contains("--no-proxy-server"))

        for arguments in [directArguments, proxyArguments] {
            XCTAssertTrue(arguments.contains("--remote-debugging-address=127.0.0.1"))
            XCTAssertTrue(arguments.contains("--no-first-run"))
            XCTAssertTrue(arguments.contains("--no-default-browser-check"))
            XCTAssertTrue(arguments.contains("--ignore-certificate-errors"))
            XCTAssertTrue(arguments.contains("--disable-notifications"))
            XCTAssertTrue(arguments.contains("--force-webrtc-ip-handling-policy=disable_non_proxied_udp"))
        }
    }

    func testBasicSettingsCanDisableOptionalBrowserFlags() throws {
        var settings = LaunchSettings()
        settings.restrictWebRTC = false
        settings.disableNotifications = false
        settings.ignoreCertificateErrors = false
        let arguments = try BrowserLauncher.buildArguments(
            mode: .quick,
            settings: settings,
            profilePath: "/tmp/optional-flags-profile",
            debugPort: 9338,
            plugins: []
        )
        XCTAssertFalse(arguments.contains("--disable-webrtc"))
        XCTAssertFalse(arguments.contains("--force-webrtc-ip-handling-policy=disable_non_proxied_udp"))
        XCTAssertFalse(arguments.contains("--disable-notifications"))
        XCTAssertFalse(arguments.contains("--ignore-certificate-errors"))
        XCTAssertTrue(arguments.contains("--no-proxy-server"))
    }

    func testChromeForTestingBannerIsDisabledOnlyForTestingRuntime() throws {
        let settings = LaunchSettings()
        let testingArguments = try BrowserLauncher.buildArguments(
            mode: .quick,
            settings: settings,
            profilePath: "/tmp/cft-profile",
            debugPort: 9334,
            plugins: [],
            runtimeKind: .chromeForTesting
        )
        XCTAssertTrue(testingArguments.contains("--disable-infobars"))

        let regularChromeArguments = try BrowserLauncher.buildArguments(
            mode: .quick,
            settings: settings,
            profilePath: "/tmp/chrome-profile",
            debugPort: 9335,
            plugins: [],
            runtimeKind: .chrome
        )
        XCTAssertFalse(regularChromeArguments.contains("--disable-infobars"))
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
        XCTAssertTrue(arguments.contains("--ignore-certificate-errors"))
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

        for proxyFlag in [
            "--proxy-server=http://127.0.0.1:9999",
            "--no-proxy-server",
            "--proxy-pac-url=http://127.0.0.1/proxy.pac",
            "--proxy-auto-detect",
            "--proxy-bypass-list=localhost",
        ] {
            settings.additionalFlags = proxyFlag
            XCTAssertThrowsError(try BrowserLauncher.buildArguments(
                mode: .custom,
                settings: settings,
                profilePath: "/tmp/isolated",
                debugPort: 9555,
                plugins: []
            ), "代理边界不应被附加参数覆盖：\(proxyFlag)")
        }
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

private final class TestOnce: @unchecked Sendable {
    private let lock = NSLock()
    private var didRun = false
    private let action: () -> Void

    init(_ action: @escaping () -> Void) {
        self.action = action
    }

    func run() {
        lock.lock()
        guard !didRun else {
            lock.unlock()
            return
        }
        didRun = true
        lock.unlock()
        action()
    }
}
