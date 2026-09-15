import AppKit
import Network
import XCTest
@testable import YTray

/// Opt in with YTRAY_CHROME_PATH and YTRAY_LAUNCHER_PATH. All profiles and traffic
/// belong to a temporary test directory/local server, never the user's browser.
@MainActor
final class BrowserExtensionIntegrationTests: XCTestCase {
    private func makeStore() throws -> InstanceStore {
        let environment = ProcessInfo.processInfo.environment
        guard let chrome = environment["YTRAY_CHROME_PATH"],
              let launcher = environment["YTRAY_LAUNCHER_PATH"],
              FileManager.default.isExecutableFile(atPath: chrome),
              FileManager.default.isExecutableFile(atPath: launcher) else {
            throw XCTSkip("Set YTRAY_CHROME_PATH and YTRAY_LAUNCHER_PATH for real Chrome integration")
        }
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("ytray-extension-integration-\(UUID().uuidString)")
        let store = InstanceStore(applicationDirectory: directory, discoverSystemBrowsers: false,
                                  browserProcessLauncher: URL(fileURLWithPath: launcher))
        let runtime = BrowserRuntime(name: "Google Chrome", version: "152.0", architecture: "arm64",
                                     executablePath: chrome, source: .local, browserKind: .chrome)
        store.runtimes = [runtime]
        store.settings.defaultRuntimeID = runtime.id
        store.settings.debugPort = BrowserLauncher.nextAvailablePort(startingAt: 19383)
        store.settings.homeURL = "data:text/html,<title>Extension Integration Ready</title><h1>ready</h1>"
        store.settings.additionalFlags = "--disable-gpu\n--window-position=-20000,-20000\n--window-size=800,600"
        store.confirmLaunchWithoutPlugins = { prompt in
            XCTFail("Unexpected plugin failure: \(prompt.message)")
            return false
        }
        addTeardownBlock { @MainActor in
            for instance in store.runningInstances { Darwin.kill(instance.processID, SIGTERM) }
            for _ in 0..<80 {
                if store.runningInstances.allSatisfy({ Darwin.kill($0.processID, 0) != 0 }) { break }
                try? await Task.sleep(nanoseconds: 100_000_000)
            }
            // The store timer can retain its scratch state; leave no test processes behind.
            for instance in store.runningInstances where Darwin.kill(instance.processID, 0) == 0 {
                Darwin.kill(instance.processID, SIGKILL)
            }
            try? FileManager.default.removeItem(at: directory)
        }
        return store
    }

    private func addPlugin(to store: InstanceStore, name: String = "Local Test", invalid: Bool = false) throws -> BrowserPlugin {
        let directory = store.applicationDirectory.appendingPathComponent(name)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let manifest: [String: Any] = ["name": name, "version": "1.0", "manifest_version": 3,
                                       "content_scripts": [["matches": ["http://*/*"], "js": ["content.js"]]]]
        try JSONSerialization.data(withJSONObject: manifest).write(to: directory.appendingPathComponent("manifest.json"))
        try Data("document.documentElement.dataset.ytrayExtension = 'injected';".utf8)
            .write(to: directory.appendingPathComponent("content.js"))
        store.addPlugin(directory: directory)
        let plugin = try XCTUnwrap(store.plugins.last)
        if invalid { try Data("{invalid".utf8).write(to: directory.appendingPathComponent("manifest.json")) }
        return plugin
    }

    private func waitForLaunch(_ store: InstanceStore) async throws {
        for _ in 0..<400 {
            if !store.isLaunching { break }
            try await Task.sleep(nanoseconds: 100_000_000)
        }
        XCTAssertFalse(store.isLaunching, "Launch timed out")
        XCTAssertNil(store.errorMessage)
    }

    private func stop(_ store: InstanceStore, instance: BrowserInstance) async throws -> BrowserInstance {
        store.stop(instance)
        for _ in 0..<160 {
            if store.historyInstances.contains(where: { $0.id == instance.id }) { break }
            try await Task.sleep(nanoseconds: 100_000_000)
        }
        return try XCTUnwrap(store.historyInstances.first(where: { $0.id == instance.id }))
    }

    private func inventory(port: Int) async throws -> [[String: Any]] {
        let socket = try await BrowserDevTools.socketURL(debugPort: port)
        let result = try await BrowserDevTools.send(socketURL: socket, method: "Extensions.getExtensions")
        return try XCTUnwrap(result["extensions"] as? [[String: Any]])
    }

    private struct Target: Decodable {
        let type: String
        let url: String?
        let webSocketDebuggerUrl: String?
    }

    private func targets(debugPort: Int) async throws -> [Target] {
        let endpoint = try XCTUnwrap(URL(string: "http://127.0.0.1:\(debugPort)/json/list"))
        var request = URLRequest(url: endpoint)
        request.timeoutInterval = 3
        let (data, response) = try await URLSession.shared.data(for: request)
        XCTAssertEqual((response as? HTTPURLResponse)?.statusCode, 200)
        return try JSONDecoder().decode([Target].self, from: data)
    }

    private func evaluateJavaScript(debugPort: Int, expression: String) async throws -> [String: Any] {
        let pages = try await targets(debugPort: debugPort)
        let socket = try XCTUnwrap(pages.first(where: { $0.type == "page" })?.webSocketDebuggerUrl.flatMap(URL.init(string:)))
        return try await BrowserDevTools.send(socketURL: socket, method: "Runtime.evaluate",
            parameters: ["expression": expression, "awaitPromise": true, "returnByValue": true])
    }

    func testRealChromeLoadsPluginAndRestoresIdentityAndTheme() async throws {
        let store = try makeStore()
        let plugin = try addPlugin(to: store)
        store.settings.dockBadge = "B"
        store.launch(mode: .custom)
        try await waitForLaunch(store)
        let initial = try XCTUnwrap(store.runningInstances.first)
        let loaded = try await inventory(port: initial.debugPort)
        let loadedPlugin = try XCTUnwrap(loaded.first { $0["enabled"] as? Bool == true && ($0["path"] as? String).map(BrowserExtensionService.normalizedPath) == BrowserExtensionService.normalizedPath(plugin.path) })
        XCTAssertEqual(loadedPlugin["id"] as? String, ExtensionInstaller.chromiumExtensionID(for: plugin),
                       "configured=\(plugin.path); canonical=\(BrowserExtensionService.normalizedPath(plugin.path)); loaded=\(loadedPlugin)")
        let title = await ScreenshotService.currentPageTitle(debugPort: initial.debugPort, attempts: 20)
        XCTAssertEqual(title, "Extension Integration Ready")
        let history = try await stop(store, instance: initial)
        let preferencesURL = URL(fileURLWithPath: history.profilePath).appendingPathComponent("Default/Preferences")
        var preferences = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: preferencesURL)) as? [String: Any])
        var autogenerated = try XCTUnwrap(preferences["autogenerated"] as? [String: Any])
        var theme = try XCTUnwrap(autogenerated["theme"] as? [String: Any])
        XCTAssertEqual((theme["color"] as? NSNumber)?.uint32Value, 0xFF2563EB)
        // Simulate a user-selected appearance and ensure restoration never overwrites it.
        theme["color"] = Int32(bitPattern: 0xFF00FF00)
        autogenerated["theme"] = theme
        preferences["autogenerated"] = autogenerated
        try JSONSerialization.data(withJSONObject: preferences).write(to: preferencesURL)
        store.restoreHistory(history)
        try await waitForLaunch(store)
        let restored = try XCTUnwrap(store.runningInstances.first)
        XCTAssertEqual(restored.id, initial.id)
        XCTAssertEqual(restored.profilePath, initial.profilePath)
        XCTAssertEqual(restored.dockBadge, "B")
        XCTAssertEqual(restored.pluginIDs, [plugin.id])
        _ = try await stop(store, instance: restored)
        let after = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: preferencesURL)) as? [String: Any])
        let afterTheme = (after["autogenerated"] as? [String: Any])?["theme"] as? [String: Any]
        XCTAssertEqual((afterTheme?["color"] as? NSNumber)?.uint32Value, 0xFF00FF00)
    }

    func testRealPartialLoadCancellationRetainsPreviousHistoryAndDefaults() async throws {
        let store = try makeStore()
        store.settings.dockBadge = "A"
        store.launch(mode: .quick)
        try await waitForLaunch(store)
        let history = try await stop(store, instance: XCTUnwrap(store.runningInstances.first))
        _ = try addPlugin(to: store)
        _ = try addPlugin(to: store, name: "Broken Plugin", invalid: true)
        let defaults = store.settings.defaultPluginIDs
        var prompted = false
        store.confirmLaunchWithoutPlugins = { prompt in
            prompted = true
            XCTAssertTrue(store.isLaunching)
            XCTAssertTrue(store.runningInstances.isEmpty)
            XCTAssertTrue(prompt.canSkipPlugins)
            XCTAssertTrue(prompt.message.contains("Broken Plugin"))
            return false
        }
        store.launch(mode: .quick)
        try await waitForLaunch(store)
        XCTAssertTrue(prompted)
        XCTAssertTrue(store.runningInstances.isEmpty)
        XCTAssertEqual(store.historyInstances.map(\.id), [history.id])
        XCTAssertEqual(store.settings.defaultPluginIDs, defaults)
        let profiles = try FileManager.default.contentsOfDirectory(atPath: store.applicationDirectory.appendingPathComponent("Profiles").path)
        XCTAssertEqual(profiles, [history.id.uuidString])
    }

    func testRealNewProfileCanRetryWithoutPlugins() async throws {
        let store = try makeStore()
        _ = try addPlugin(to: store)
        _ = try addPlugin(to: store, name: "Broken Plugin", invalid: true)
        store.settings.dockBadge = "D"
        let defaults = store.settings.defaultPluginIDs
        var promptCount = 0
        store.confirmLaunchWithoutPlugins = { prompt in
            promptCount += 1
            XCTAssertTrue(prompt.canSkipPlugins)
            XCTAssertTrue(store.runningInstances.isEmpty)
            return true
        }
        store.launch(mode: .custom)
        try await waitForLaunch(store)
        let retry = try XCTUnwrap(store.runningInstances.first)
        XCTAssertEqual(promptCount, 1)
        XCTAssertEqual(retry.dockBadge, "D")
        XCTAssertEqual(retry.pluginIDs, [])
        XCTAssertEqual(store.settings.defaultPluginIDs, defaults)
        let profiles = try FileManager.default.contentsOfDirectory(atPath: store.applicationDirectory.appendingPathComponent("Profiles").path)
        XCTAssertEqual(profiles, [retry.id.uuidString])
        let title = await ScreenshotService.currentPageTitle(debugPort: retry.debugPort, attempts: 20)
        XCTAssertEqual(title, "Extension Integration Ready")
    }

    func testRealExistingProfileRetryDisablesPreviouslyLoadedPlugins() async throws {
        let store = try makeStore()
        let plugin = try addPlugin(to: store)
        store.launch(mode: .quick)
        try await waitForLaunch(store)
        let initial = try XCTUnwrap(store.runningInstances.first)
        var retiredProcess: Process?
        let exitName = Notification.Name("YTrayProcessDidTerminate")
        let observer = NotificationCenter.default.addObserver(forName: exitName, object: nil, queue: .main) { notification in
            if let process = notification.object as? Process, process.processIdentifier == initial.processID {
                retiredProcess = process
            }
        }
        defer { NotificationCenter.default.removeObserver(observer) }
        var history = try await stop(store, instance: initial)
        let broken = try addPlugin(to: store, name: "Broken Plugin", invalid: true)
        history.pluginIDs = [plugin.id, broken.id]
        history.lastPageURL = "data:text/html,<title>Restored Without Plugins</title>"
        let defaults = store.settings.defaultPluginIDs
        store.confirmLaunchWithoutPlugins = { _ in false }
        store.restoreHistory(history)
        try await waitForLaunch(store)
        XCTAssertTrue(store.runningInstances.isEmpty)
        let cancelled = try XCTUnwrap(store.historyInstances.first)
        XCTAssertEqual(cancelled.id, history.id)
        XCTAssertEqual(cancelled.lastPageURL, history.lastPageURL)
        XCTAssertEqual(cancelled.pluginIDs, history.pluginIDs)
        XCTAssertEqual(cancelled.thumbnailPath, history.thumbnailPath)
        XCTAssertTrue(FileManager.default.fileExists(atPath: history.profilePath))
        var promptCount = 0
        store.confirmLaunchWithoutPlugins = { _ in promptCount += 1; return true }
        store.restoreHistory(history)
        try await waitForLaunch(store)
        let retry = try XCTUnwrap(store.runningInstances.first)
        XCTAssertEqual(promptCount, 1)
        XCTAssertEqual(retry.id, history.id)
        XCTAssertEqual(retry.profilePath, history.profilePath)
        XCTAssertEqual(retry.pluginIDs, [])
        XCTAssertEqual(store.settings.defaultPluginIDs, defaults)
        XCTAssertTrue(store.plugins.allSatisfy(\.enabled))
        let extensions = try await inventory(port: retry.debugPort)
        XCTAssertFalse(extensions.contains { $0["enabled"] as? Bool == true })
        let title = await ScreenshotService.currentPageTitle(debugPort: retry.debugPort, attempts: 20)
        XCTAssertEqual(title, "Restored Without Plugins")
        // A queued exit notification from an earlier Process must not stop this replacement.
        NotificationCenter.default.post(name: exitName, object: try XCTUnwrap(retiredProcess))
        XCTAssertEqual(store.runningInstances.first?.processID, retry.processID)
        XCTAssertTrue(FileManager.default.fileExists(atPath: BrowserProcessIcon.iconURL(instanceID: retry.id, applicationDirectory: store.applicationDirectory).path))
    }

    func testRealProxyAuthenticationAndContentScriptRunBeforeTargetPage() async throws {
        let store = try makeStore()
        _ = try addPlugin(to: store)
        let proxy = try LocalExtensionProxy()
        defer { proxy.stop() }
        try await proxy.start()
        store.settings.proxyServer = "http://127.0.0.1:\(proxy.port)"
        store.settings.proxyUsername = "yak"
        store.settings.proxyPassword = "secret"
        store.settings.homeURL = "http://ytray-extension.invalid/probe"
        store.launch(mode: .custom)
        try await waitForLaunch(store)
        let instance = try XCTUnwrap(store.runningInstances.first)
        var injected = false
        for _ in 0..<30 {
            let result = try await evaluateJavaScript(debugPort: instance.debugPort, expression: "document.documentElement.dataset.ytrayExtension")
            injected = (result["result"] as? [String: Any])?["value"] as? String == "injected"
            if injected { break }
            try await Task.sleep(nanoseconds: 100_000_000)
        }
        XCTAssertTrue(injected)
        XCTAssertGreaterThan(proxy.authorizedRequests, 0)
        XCTAssertGreaterThan(proxy.challenges, 0)
        let title = await ScreenshotService.currentPageTitle(debugPort: instance.debugPort)
        XCTAssertEqual(title, "Proxy Extension Ready")
    }

    func testRealProxyPluginFailureNeverNavigatesOrAllowsSkip() async throws {
        let store = try makeStore()
        _ = try addPlugin(to: store, name: "Broken Plugin", invalid: true)
        let proxy = try LocalExtensionProxy()
        defer { proxy.stop() }
        try await proxy.start()
        store.settings.proxyServer = "http://127.0.0.1:\(proxy.port)"
        store.settings.proxyUsername = "yak"
        store.settings.proxyPassword = "secret"
        store.settings.homeURL = "http://ytray-extension.invalid/probe"
        var prompted = false
        store.confirmLaunchWithoutPlugins = { prompt in
            prompted = true
            XCTAssertFalse(prompt.canSkipPlugins)
            return true // Even an erroneous confirmation cannot bypass authentication.
        }
        store.launch(mode: .quick)
        try await waitForLaunch(store)
        XCTAssertTrue(prompted)
        XCTAssertTrue(store.runningInstances.isEmpty)
        XCTAssertEqual(proxy.probeRequests, 0)
    }

    func testRealBundledAgentReceivesManagedIdentityAfterCDPLoad() async throws {
        let store = try makeStore()
        guard let path = ProcessInfo.processInfo.environment["YTRAY_TEST_EXTENSION_PATH"] else {
            throw XCTSkip("Set YTRAY_TEST_EXTENSION_PATH to an unpacked Yakit Browser Agent")
        }
        // Copy into the test-owned tree before loading so extensions cannot change installation data.
        let copied = store.applicationDirectory.appendingPathComponent("Yakit Browser Agent")
        try FileManager.default.copyItem(at: URL(fileURLWithPath: path), to: copied)
        store.addPlugin(directory: copied)
        let server = try LocalExtensionProxy(requiresAuthentication: false)
        defer { server.stop() }
        try await server.start()
        store.settings.homeURL = "http://127.0.0.1:\(server.port)/probe"
        let plugin = try XCTUnwrap(store.plugins.first)
        store.launch(mode: .quick)
        try await waitForLaunch(store)
        let instance = try XCTUnwrap(store.runningInstances.first)
        let extensionID = try XCTUnwrap(ExtensionInstaller.chromiumExtensionID(for: plugin))
        let installed = try await inventory(port: instance.debugPort)
        XCTAssertTrue(installed.contains { $0["id"] as? String == extensionID && $0["enabled"] as? Bool == true })
        var storage: [String: Any] = [:]
        for _ in 0..<40 {
            let targets = try await targets(debugPort: instance.debugPort)
            if let worker = targets.first(where: { $0.type == "service_worker" && $0.url?.hasPrefix("chrome-extension://\(extensionID)/") == true }),
               let socket = worker.webSocketDebuggerUrl.flatMap(URL.init(string:)) {
                storage = try await BrowserDevTools.send(socketURL: socket, method: "Runtime.evaluate",
                    parameters: ["expression": "chrome.storage.local.get(null)", "awaitPromise": true, "returnByValue": true])
            }
            let json = String(decoding: try JSONSerialization.data(withJSONObject: storage), as: UTF8.self)
            if json.lowercased().contains(instance.id.uuidString.lowercased()) { break }
            try await Task.sleep(nanoseconds: 100_000_000)
        }
        let json = String(decoding: try JSONSerialization.data(withJSONObject: storage), as: UTF8.self)
        XCTAssertTrue(json.lowercased().contains(instance.id.uuidString.lowercased()), json)
        let title = await ScreenshotService.currentPageTitle(debugPort: instance.debugPort, attempts: 30)
        XCTAssertEqual(title, "Proxy Extension Ready")
        let screenshot = try await ScreenshotService.capture(debugPort: instance.debugPort, instanceID: instance.id,
                                                             outputDirectory: store.applicationDirectory)
        XCTAssertGreaterThan(try Data(contentsOf: screenshot).count, 1000)
        let history = try await stop(store, instance: instance)
        store.restoreHistory(history)
        try await waitForLaunch(store)
        let restored = try XCTUnwrap(store.runningInstances.first)
        XCTAssertEqual(restored.id, history.id)
        var restoredTitle: String?
        for _ in 0..<40 {
            restoredTitle = await ScreenshotService.currentPageTitle(debugPort: restored.debugPort)
            if restoredTitle == "Proxy Extension Ready" { break }
            try await Task.sleep(nanoseconds: 100_000_000)
        }
        XCTAssertEqual(restoredTitle, "Proxy Extension Ready")
    }
}

private final class LocalExtensionProxy: @unchecked Sendable {
    private let listener: NWListener
    private let requiresAuthentication: Bool
    private let queue = DispatchQueue(label: "ytray-tests.extension-proxy")
    private let lock = NSLock()
    private var counts = (authorized: 0, challenges: 0, probes: 0)
    var port: UInt16 { listener.port!.rawValue }
    var authorizedRequests: Int { lock.withLock { counts.authorized } }
    var challenges: Int { lock.withLock { counts.challenges } }
    var probeRequests: Int { lock.withLock { counts.probes } }
    init(requiresAuthentication: Bool = true) throws {
        self.requiresAuthentication = requiresAuthentication
        listener = try NWListener(using: .tcp, on: .any)
    }
    func start() async throws {
        listener.newConnectionHandler = { [weak self] connection in
            guard let self else { connection.cancel(); return }
            connection.start(queue: self.queue)
            connection.receive(minimumIncompleteLength: 1, maximumLength: 16_384) { data, _, _, _ in
                let request = String(decoding: data ?? Data(), as: UTF8.self)
                let authorized = !self.requiresAuthentication || request.lowercased().contains("proxy-authorization: basic eWFrOnNlY3JldA==".lowercased())
                let isProbe = request.contains("ytray-extension.invalid/probe")
                self.lock.withLock {
                    if authorized { self.counts.authorized += 1 } else { self.counts.challenges += 1 }
                    if isProbe { self.counts.probes += 1 }
                }
                let body = "<html><title>Proxy Extension Ready</title><body>local proxy</body></html>"
                let response = authorized
                    ? "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: \(body.utf8.count)\r\nConnection: close\r\n\r\n\(body)"
                    : "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"YTray\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                connection.send(content: Data(response.utf8), completion: .contentProcessed { _ in connection.cancel() })
            }
        }
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            listener.stateUpdateHandler = { state in
                if case .ready = state { continuation.resume() }
                if case .failed(let error) = state { continuation.resume(throwing: error) }
            }
            listener.start(queue: queue)
        }
    }
    func stop() { listener.cancel() }
}
