import AppKit
import XCTest
@testable import YTray

final class BrowserExtensionServiceTests: XCTestCase {
    private let id = String(repeating: "a", count: 32)
    private let path = "/tmp/ytray-extension-protocol"
    private var unavailable: BrowserDevToolsError { .init(code: -32601, message: "Method not found") }

    func testEnabledExtensionAtTheSamePathIsNotReloaded() async throws {
        var calls: [String] = []
        try await BrowserExtensionService.ensureLoaded(paths: [path, path + "/../ytray-extension-protocol"], legacyLoadingExpected: false) { method, _ in
            calls.append(method)
            return ["extensions": [["id": self.id, "path": self.path, "enabled": true]]]
        }
        XCTAssertEqual(calls, ["Extensions.getExtensions"])
    }

    func testMissingAndDisabledExtensionsAreLoadedAndVerified() async throws {
        for initiallyDisabled in [false, true] {
            var loaded = false
            var calls: [String] = []
            try await BrowserExtensionService.ensureLoaded(paths: [path], legacyLoadingExpected: false) { method, params in
                calls.append(method)
                if method == "Extensions.loadUnpacked" {
                    XCTAssertEqual(params["path"] as? String, self.path)
                    loaded = true
                    return ["id": self.id]
                }
                return ["extensions": loaded || initiallyDisabled
                        ? [["id": self.id, "path": self.path, "enabled": loaded]] : []]
            }
            XCTAssertEqual(calls, ["Extensions.getExtensions", "Extensions.loadUnpacked", "Extensions.getExtensions"])
        }
    }

    func testVerificationRetriesUntilBrowserReportsEnabled() async throws {
        var reads = 0
        try await BrowserExtensionService.ensureLoaded(paths: [path], legacyLoadingExpected: false) { method, _ in
            if method == "Extensions.loadUnpacked" { return ["id": self.id] }
            reads += 1
            return ["extensions": [["id": self.id, "path": self.path, "enabled": reads >= 3]]]
        }
        XCTAssertEqual(reads, 3)
    }

    func testWrongIDPathAndDisabledResultsFailVerification() async {
        let results: [[String: Any]] = [
            ["id": String(repeating: "b", count: 32), "path": path, "enabled": true],
            ["id": id, "path": path + "-other", "enabled": true],
            ["id": id, "path": path, "enabled": false]
        ]
        for result in results {
            var reads = 0
            do {
                try await BrowserExtensionService.ensureLoaded(paths: [path], legacyLoadingExpected: false) { method, _ in
                    if method == "Extensions.loadUnpacked" { return ["id": self.id] }
                    reads += 1
                    return ["extensions": reads == 1 ? [] : [result]]
                }
                XCTFail("Unverified extension accepted")
            } catch { XCTAssertTrue(error.localizedDescription.contains("未确认插件已启用")) }
        }
    }

    func testInvalidLoadIDsAndMalformedListsAreRejected() async {
        for invalid in ["", "abc", String(repeating: "z", count: 32)] {
            do {
                try await BrowserExtensionService.ensureLoaded(paths: [path], legacyLoadingExpected: false) { method, _ in
                    method == "Extensions.loadUnpacked" ? ["id": invalid] : ["extensions": []]
                }
                XCTFail("Invalid extension ID accepted")
            } catch { XCTAssertTrue(error.localizedDescription.contains("有效插件 ID")) }
        }
        do {
            try await BrowserExtensionService.ensureLoaded(paths: [path], legacyLoadingExpected: true) { _, _ in [:] }
            XCTFail("Malformed inventory must not downgrade to legacy loading")
        } catch { XCTAssertTrue(error.localizedDescription.contains("无效的插件列表")) }
    }

    func testLegacyFallbackRequiresBothAPIsToBeUnavailable() async throws {
        try await BrowserExtensionService.ensureLoaded(paths: [path], legacyLoadingExpected: true) { _, _ in throw self.unavailable }
        for inventoryAvailable in [false, true] {
            do {
                try await BrowserExtensionService.ensureLoaded(paths: [path], legacyLoadingExpected: inventoryAvailable) { method, _ in
                    if inventoryAvailable && method == "Extensions.getExtensions" { return ["extensions": []] }
                    throw self.unavailable
                }
                XCTFail("Modern Chrome must not silently accept missing extensions")
            } catch { XCTAssertTrue(error is ExtensionLoadingError) }
        }
    }

    func testLoadAPIWorksEvenWhenInventoryAPIIsUnavailable() async throws {
        var loaded = false
        try await BrowserExtensionService.ensureLoaded(paths: [path], legacyLoadingExpected: false) { method, _ in
            if method == "Extensions.getExtensions" { throw self.unavailable }
            loaded = true
            return ["id": self.id]
        }
        XCTAssertTrue(loaded)
    }

    func testRealAPIErrorsAreNotTreatedAsUnavailable() async {
        for failingMethod in ["Extensions.getExtensions", "Extensions.loadUnpacked"] {
            do {
                try await BrowserExtensionService.ensureLoaded(paths: [path], legacyLoadingExpected: true) { method, _ in
                    if method == failingMethod { throw BrowserDevToolsError(code: -32000, message: "Policy denied") }
                    return ["extensions": []]
                }
                XCTFail("Policy failure ignored")
            } catch { XCTAssertTrue(error.localizedDescription.contains("Policy denied")) }
        }
    }

    func testCancellationIsNotAnExtensionFailure() async {
        do {
            try await BrowserExtensionService.ensureLoaded(paths: [path], legacyLoadingExpected: true) { method, _ in
                if method == "Extensions.getExtensions" { return ["extensions": []] }
                throw CancellationError()
            }
            XCTFail("Cancellation ignored")
        } catch { XCTAssertTrue(error is CancellationError) }
    }

    func testSymlinkedPluginsKeepTheSameChromeIDAndAreLoadedOnce() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("ytray-plugin-symlink-\(UUID().uuidString)")
        let real = root.appendingPathComponent("plugin")
        let alias = root.appendingPathComponent("alias")
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: real, withIntermediateDirectories: true)
        try Data(#"{"name":"Local","version":"1","manifest_version":3}"#.utf8).write(to: real.appendingPathComponent("manifest.json"))
        try FileManager.default.createSymbolicLink(at: alias, withDestinationURL: real)
        let plugin = BrowserPlugin(name: "Local", version: "1", path: alias.path, manifestVersion: 3)
        let realPath = real.path.hasPrefix("/var/") ? "/private" + real.path : real.path
        let expectedID = ExtensionInstaller.chromiumExtensionID(extensionPath: realPath, manifestKey: nil)
        XCTAssertEqual(ExtensionInstaller.chromiumExtensionID(for: plugin), expectedID)
        var loads = 0
        try await BrowserExtensionService.ensureLoaded(paths: [alias.path, real.path], legacyLoadingExpected: false) { method, _ in
            if method == "Extensions.loadUnpacked" { loads += 1; return ["id": self.id] }
            return ["extensions": loads == 0 ? [] : [["id": self.id, "path": real.path, "enabled": true]]]
        }
        XCTAssertEqual(loads, 1)
    }

    func testEmptySelectionDoesNotConnectToTheBrowser() async throws {
        try await BrowserExtensionService.ensureLoaded(paths: [], legacyLoadingExpected: false) { _, _ in
            XCTFail("Unexpected CDP command")
            return [:]
        }
    }

    func testBrowserTargetMustBeLoopbackOnTheRequestedPort() throws {
        XCTAssertEqual(try BrowserDevTools.validatedSocketURL("ws://127.0.0.1:9222/devtools/browser/id", debugPort: 9222).port, 9222)
        for url in ["ws://example.com:9222/devtools/browser/id", "ws://127.0.0.1:9223/devtools/browser/id",
                    "ws://127.0.0.1:9222/devtools/page/id", "wss://127.0.0.1:9222/devtools/browser/id",
                    "ws://user@127.0.0.1:9222/devtools/browser/id", "ws://127.0.0.1:9222/devtools/browser/"] {
            XCTAssertThrowsError(try BrowserDevTools.validatedSocketURL(url, debugPort: 9222))
        }
    }

    func testChromeVersionBoundaryAndCompatibleBrowserAllowlist() throws {
        for kind in [BrowserKind.chrome, .chromeBeta, .chromeCanary] {
            for version in ["136.0", "137.0", "152.0", "", "invalid"] {
                let expected = version == "136.0"
                XCTAssertEqual(BrowserExtensionService.legacyLoadingExpected(runtimeKind: kind, version: version), expected)
                let args = try BrowserLauncher.buildArguments(
                    mode: .quick, settings: LaunchSettings(), profilePath: "/tmp/profile", debugPort: 9222,
                    plugins: [], runtimeKind: kind, internalExtensionPaths: [path], runtimeVersion: version
                )
                XCTAssertEqual(args.contains(where: { $0.hasPrefix("--disable-extensions-except=") }), expected)
            }
        }
        for kind in [BrowserKind.chromeForTesting, .chromium, .edge] {
            XCTAssertTrue(BrowserExtensionService.legacyLoadingExpected(runtimeKind: kind, version: "152.0"))
        }
    }

    func testDeferredStartupDoesNotNavigateOrRestoreTabsBeforeVerification() throws {
        var settings = LaunchSettings()
        settings.homeURL = "https://example.com/private"
        settings.proxyUsername = "yak"
        settings.proxyPassword = "secret"
        let args = try BrowserLauncher.buildArguments(
            mode: .custom, settings: settings, profilePath: "/tmp/profile", debugPort: 9222,
            plugins: [], internalExtensionPaths: [path], restoreLastSession: true, deferExtensionStartup: true
        )
        XCTAssertEqual(args.last, "about:blank")
        XCTAssertFalse(args.contains(settings.homeURL))
        XCTAssertFalse(args.contains("--restore-last-session"))
        XCTAssertFalse(args.joined().contains("secret"))
    }

    func testDeferredManagedBootstrapPreservesStartupProxy() throws {
        let plugin = BrowserPlugin(name: ExtensionInstaller.extensionName, version: "1", path: "/tmp/ytray-managed-bootstrap", manifestVersion: 3)
        var settings = LaunchSettings()
        for proxy in ["", "http://127.0.0.1:8083"] {
            settings.proxyServer = proxy
            let bootstrap = try BrowserLauncher.deferredStartupURL(settings: settings, plugins: [plugin],
                instanceID: UUID(), badge: "A", history: nil)
            let query = try XCTUnwrap(URLComponents(string: bootstrap)?.queryItems)
            XCTAssertEqual(query.first(where: { $0.name == "startupProxy" })?.value, proxy.isEmpty ? "direct" : proxy)
        }
    }

    func testSkipPluginsPreservesOtherConfigurationAndCannotSkipProxyAuthentication() throws {
        let runtime = BrowserRuntime(name: "Chrome", version: "152", architecture: "arm64", executablePath: "/tmp/chrome", source: .system)
        var settings = LaunchSettings()
        settings.defaultPluginIDs = [UUID()]
        settings.proxyServer = "http://127.0.0.1:8080"
        settings.homeURL = "https://example.com/"
        settings.dockBadge = "B"
        settings.additionalFlags = "--disable-gpu"
        let prompt = ExtensionLaunchPrompt(runtime: runtime, settings: settings, pluginCount: 1, failure: "Invalid manifest")
        let retry = try XCTUnwrap(prompt.retrySettings(from: settings))
        XCTAssertTrue(prompt.canSkipPlugins)
        XCTAssertTrue(prompt.message.contains("Invalid manifest"))
        XCTAssertTrue(retry.defaultPluginIDs.isEmpty)
        XCTAssertEqual(retry.proxyServer, settings.proxyServer)
        XCTAssertEqual(retry.homeURL, settings.homeURL)
        XCTAssertEqual(retry.dockBadge, "B")
        XCTAssertEqual(retry.additionalFlags, "--disable-gpu\n--disable-extensions")
        XCTAssertEqual(settings.defaultPluginIDs.count, 1)
        for username in ["yak", ""] {
            settings.proxyUsername = username
            settings.proxyPassword = username.isEmpty ? "secret" : ""
            let required = ExtensionLaunchPrompt(runtime: runtime, settings: settings, pluginCount: 1, failure: "failed")
            XCTAssertFalse(required.canSkipPlugins)
            XCTAssertNil(required.retrySettings(from: settings))
        }
    }
    @MainActor
    func testFailureDialogKeepsButtonsVisibleWithLongErrorsInBothAppearances() async throws {
        _ = NSApplication.shared
        let runtime = BrowserRuntime(name: "Chrome", version: "152", architecture: "arm64", executablePath: "/tmp/chrome", source: .system)
        for appearance in [NSAppearance.Name.aqua, .darkAqua] {
            let prompt = ExtensionLaunchPrompt(runtime: runtime, settings: LaunchSettings(), pluginCount: 3,
                                               failure: String(repeating: "插件 manifest.json 加载失败，缺少必要字段。\n", count: 100))
            let alert = prompt.makeAlert()
            alert.window.appearance = NSAppearance(named: appearance)
            alert.layout()
            alert.window.orderFrontRegardless()
            defer { alert.window.orderOut(nil) }
            try await Task.sleep(nanoseconds: 300_000_000)
            let content = try XCTUnwrap(alert.window.contentView)
            content.layoutSubtreeIfNeeded()
            let scroll = try XCTUnwrap(alert.accessoryView as? NSScrollView)
            let text = try XCTUnwrap(scroll.documentView as? NSTextView)
            XCTAssertGreaterThan(text.frame.height, scroll.contentSize.height)
            XCTAssertEqual(alert.buttons.map(\.title), ["取消启动", "不加载插件并启动"])
            XCTAssertLessThan(alert.window.frame.height, NSScreen.main?.visibleFrame.height ?? 800)
            for button in alert.buttons {
                XCTAssertTrue(content.bounds.contains(button.convert(button.bounds, to: content)))
                XCTAssertFalse(button.isHidden)
            }

        }
        var authentication = LaunchSettings()
        authentication.proxyUsername = "yak"
        let alert = ExtensionLaunchPrompt(runtime: runtime, settings: authentication, pluginCount: 1, failure: "failed").makeAlert()
        XCTAssertEqual(alert.buttons.map(\.title), ["取消启动"])
    }

}
