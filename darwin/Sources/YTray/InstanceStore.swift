import AppKit
import Combine
import Foundation
import Darwin

@MainActor
final class InstanceStore: NSObject, ObservableObject {
    @Published var runtimes: [BrowserRuntime] = []
    @Published var plugins: [BrowserPlugin] = []
    @Published var instances: [BrowserInstance] = []
    @Published var settings = LaunchSettings()
    @Published var availableVersions: [MirrorVersion] = []
    @Published var isInstalling = false
    @Published var activityMessage = ""
    @Published var errorMessage: String?
    @Published var extensionManifest: ExtensionManifest?
    @Published var isInstallingExtension = false
    @Published var extensionStatusMessage = ""
    @Published private(set) var launchPhase: BrowserLaunchPhase = .idle
    @Published private(set) var launchMessage = ""
    @Published private(set) var launchingMode: LaunchMode?
    @Published private(set) var launchingUsesProxy: Bool?
    @Published private(set) var restoringInstanceID: UUID?
    @Published private(set) var proxyCheckPhase: ProxyCheckPhase = .idle
    @Published private(set) var proxyCheckMessage = ""
    @Published private(set) var proxyCheckReport: ProxyCheckReport?
    @Published var isProxyAdvancedExpanded = false

    let applicationDirectory: URL
    private let stateURL: URL
    private var processes: [UUID: Process] = [:]
    private var timer: Timer?
    private var titleRefreshInFlight = false
    private var thumbnailCapturesInFlight: Set<UUID> = []
    private var lastThumbnailAttempt: [UUID: Date] = [:]
    private var launchToken: UUID?
    private var launchingInstanceID: UUID?
    private var proxyCheckToken: UUID?

    private static let thumbnailRefreshInterval: TimeInterval = 12
    private static var applicationDirectoryName: String { AppEnvironment.applicationDirectoryName }
    private static let legacyApplicationDirectoryName = ["Instance", "Dock"].joined()

    var isLaunching: Bool {
        launchPhase == .preparing || launchPhase == .waiting
    }

    init(
        applicationDirectory: URL? = nil,
        discoverSystemBrowsers: Bool = true,
        legacyApplicationDirectory: URL? = nil
    ) {
        let usesDefaultApplicationDirectory = applicationDirectory == nil
        let supportDirectory = FileManager.default.urls(
            for: .applicationSupportDirectory,
            in: .userDomainMask
        )[0]
        let base = applicationDirectory ?? supportDirectory
            .appendingPathComponent(Self.applicationDirectoryName, isDirectory: true)
        let legacyBase = legacyApplicationDirectory ?? (applicationDirectory == nil && !AppEnvironment.isDevelopmentBuild
            ? supportDirectory.appendingPathComponent(Self.legacyApplicationDirectoryName, isDirectory: true)
            : nil)
        if let legacyBase {
            Self.moveLegacyApplicationDirectoryIfNeeded(from: legacyBase, to: base)
        }
        self.applicationDirectory = base
        self.stateURL = base.appendingPathComponent("state.json")
        super.init()
        try? FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        load()
        if let legacyBase {
            rewriteManagedPaths(from: legacyBase, to: base)
        }
        if usesDefaultApplicationDirectory && AppEnvironment.bundledExtensionAutoInstallEnabled {
            installBundledExtensionIfNeeded()
        }
        if discoverSystemBrowsers { refreshSystemBrowsers() }
        refreshProcessStates()
        NotificationCenter.default.addObserver(self, selector: #selector(processDidTerminate(_:)),
                                               name: .ytrayProcessDidTerminate, object: nil)
        timer = Timer.scheduledTimer(timeInterval: 2, target: self,
                                     selector: #selector(refreshTimerFired), userInfo: nil, repeats: true)
    }

    private static func moveLegacyApplicationDirectoryIfNeeded(from legacy: URL, to current: URL) {
        let fileManager = FileManager.default
        guard legacy.standardizedFileURL != current.standardizedFileURL,
              !fileManager.fileExists(atPath: current.path),
              fileManager.fileExists(atPath: legacy.path) else { return }
        do {
            try fileManager.createDirectory(
                at: current.deletingLastPathComponent(),
                withIntermediateDirectories: true
            )
            try fileManager.moveItem(at: legacy, to: current)
        } catch {
            // Keep startup usable even if a preview-version directory cannot be moved.
            // The new directory will be created below and the original data remains untouched.
        }
    }

    private func rewriteManagedPaths(from legacy: URL, to current: URL) {
        let oldPrefix = legacy.standardizedFileURL.path
        let newPrefix = current.standardizedFileURL.path
        guard oldPrefix != newPrefix else { return }
        var changed = false

        func migratedPath(_ path: String) -> String {
            guard path == oldPrefix || path.hasPrefix(oldPrefix + "/") else { return path }
            changed = true
            return newPrefix + path.dropFirst(oldPrefix.count)
        }

        for index in runtimes.indices {
            runtimes[index].executablePath = migratedPath(runtimes[index].executablePath)
        }
        for index in plugins.indices {
            plugins[index].path = migratedPath(plugins[index].path)
        }
        for index in instances.indices {
            instances[index].profilePath = migratedPath(instances[index].profilePath)
            if let path = instances[index].thumbnailPath {
                instances[index].thumbnailPath = migratedPath(path)
            }
            if let path = instances[index].lastScreenshotPath {
                instances[index].lastScreenshotPath = migratedPath(path)
            }
        }
        if changed { save() }
    }

    var runningInstances: [BrowserInstance] { instances.filter { $0.status == .running } }
    var historyInstances: [BrowserInstance] { instances.filter { $0.status != .running } }
    var systemRuntimes: [BrowserRuntime] {
        runtimes.filter(\.isSystemEnvironment).sorted { $0.displayTitle < $1.displayTitle }
    }
    var managedRuntimes: [BrowserRuntime] {
        runtimes.filter { $0.source == .managed }.sorted { $0.createdAt > $1.createdAt }
    }
    var localBrowserRuntimes: [BrowserRuntime] {
        runtimes.filter { $0.source != .managed }.sorted { $0.displayTitle < $1.displayTitle }
    }
    var defaultRuntime: BrowserRuntime? {
        runtimes.first(where: { $0.id == settings.defaultRuntimeID }) ?? systemRuntimes.first ?? runtimes.first
    }

    func refreshManifest() async {
        do { availableVersions = try await RuntimeInstaller.fetchVersions() }
        catch { report(error) }
    }

    func install(version: MirrorVersion) async {
        isInstalling = true
        activityMessage = "正在下载并校验 \(version.version)…"
        defer { isInstalling = false; activityMessage = "" }
        do { _ = upsert(try await RuntimeInstaller.install(version: version, into: applicationDirectory)) }
        catch { report(error) }
    }

    /// The managed Yakit extension, if installed under the application's Plugins root.
    /// Matched by extension name + managed path so upgrades keep pointing at it.
    var managedExtension: BrowserPlugin? {
        let prefix = ExtensionInstaller.pluginsRoot(applicationDirectory: applicationDirectory).path
        return plugins.first {
            $0.name == ExtensionInstaller.extensionName && $0.path.hasPrefix(prefix)
        }
    }

    var isExtensionUpdateAvailable: Bool {
        guard let manifest = extensionManifest else { return false }
        let latest = manifest.versions.first { ExtensionInstaller.enterpriseArtifact(of: $0) != nil }
            ?? ExtensionReleaseVersion(version: manifest.latest, publishedAt: "", commit: "", artifacts: [])
        guard ExtensionInstaller.enterpriseArtifact(of: latest) != nil else { return false }
        guard let installed = managedExtension else { return true }
        return ExtensionInstaller.compareVersions(latest.version, installed.version) == .orderedDescending
    }

    func refreshExtensionManifest() async {
        extensionStatusMessage = "正在获取插件版本…"
        do {
            extensionManifest = try await ExtensionInstaller.fetchManifest()
            extensionStatusMessage = ""
        } catch {
            extensionStatusMessage = error.localizedDescription
            report(error)
        }
    }

    func installExtension(version: ExtensionReleaseVersion? = nil) async {
        guard !isInstallingExtension else { return }
        guard let target = version
            ?? extensionManifest?.versions.first(where: { ExtensionInstaller.enterpriseArtifact(of: $0) != nil }) else {
            if installBundledExtensionIfNeeded(force: true) { return }
            report(YTrayError.extensionInstallFailed("没有可安装的插件版本，请先刷新插件清单"))
            return
        }
        isInstallingExtension = true
        errorMessage = nil
        extensionStatusMessage = "正在下载并校验 Yakit 插件 \(target.version)…"
        defer { isInstallingExtension = false }
        do {
            let directory = try await ExtensionInstaller.install(version: target, into: applicationDirectory)
            registerManagedExtension(directory: directory)
            ExtensionInstaller.clearManagedExtensionRemoved(applicationDirectory: applicationDirectory)
            ExtensionInstaller.cleanupOldVersions(applicationDirectory: applicationDirectory, installedVersion: target.version)
            extensionStatusMessage = "Yakit 插件 \(target.version) 安装完成"
        } catch {
            report(error)
            extensionStatusMessage = error.localizedDescription
        }
    }

    /// Upserts the extracted managed-extension directory. Unlike addPlugin this keeps the
    /// previous plugin id on upgrades so settings.defaultPluginIDs stays valid.
    private func registerManagedExtension(directory: URL) {
        do {
            let data = try Data(contentsOf: directory.appendingPathComponent("manifest.json"))
            let manifest = try JSONDecoder().decode(PluginManifest.self, from: data)
            var previous = managedExtension
            if previous == nil, let index = plugins.firstIndex(where: { $0.path == directory.path }) {
                previous = plugins[index]
            }
            var plugin = BrowserPlugin(name: manifest.name, version: manifest.version,
                                       path: directory.path, manifestVersion: manifest.manifestVersion)
            if let previous {
                plugin.id = previous.id
                plugin.enabled = previous.enabled
                plugin.createdAt = previous.createdAt
                if let index = plugins.firstIndex(where: { $0.id == previous.id }) {
                    plugins[index] = plugin
                } else {
                    plugins.append(plugin)
                }
            } else {
                plugins.removeAll { $0.path == directory.path }
                plugins.append(plugin)
            }
            synchronizeDefaultPluginIDs()
            save()
        } catch {
            report(YTrayError.invalidPlugin(directory.path))
        }
    }

    @discardableResult
    func addLocalRuntime(selectedURL: URL) -> BrowserRuntime? {
        guard let runtime = SystemBrowserDiscovery.inspect(selectedURL: selectedURL) else {
            report(YTrayError.invalidExecutable(selectedURL.path)); return nil
        }
        return upsert(runtime)
    }

    func refreshSystemBrowsers() {
        let discovered = SystemBrowserDiscovery.discover()
        let discoveredPaths = Set(discovered.map(\.executablePath))
        runtimes.removeAll { runtime in
            runtime.source == .system && !discoveredPaths.contains(runtime.executablePath)
        }
        for runtime in discovered { _ = upsert(runtime, persist: false) }
        if settings.defaultRuntimeID == nil
            || !runtimes.contains(where: { $0.id == settings.defaultRuntimeID }) {
            settings.defaultRuntimeID = systemRuntimes.first?.id ?? runtimes.first?.id
        }
        save()
    }

    func removeRuntime(_ runtime: BrowserRuntime) {
        guard !runningInstances.contains(where: { $0.runtimeID == runtime.id }) else {
            report(YTrayError.launchFailed("该运行时仍有实例正在运行")); return
        }
        runtimes.removeAll { $0.id == runtime.id }
        if settings.defaultRuntimeID == runtime.id { settings.defaultRuntimeID = runtimes.first?.id }
        save()
    }

    func addPlugin(directory: URL) {
        do {
            let data = try Data(contentsOf: directory.appendingPathComponent("manifest.json"))
            let manifest = try JSONDecoder().decode(PluginManifest.self, from: data)
            let normalizedPath = directory.standardizedFileURL.path
            let existing = plugins.first { URL(fileURLWithPath: $0.path).standardizedFileURL.path == normalizedPath }
            var plugin = BrowserPlugin(name: manifest.name, version: manifest.version, path: normalizedPath,
                                       manifestVersion: manifest.manifestVersion)
            if let existing {
                plugin.id = existing.id
                plugin.enabled = existing.enabled
                plugin.createdAt = existing.createdAt
                if let index = plugins.firstIndex(where: { $0.id == existing.id }) {
                    plugins[index] = plugin
                }
            } else {
                plugins.append(plugin)
            }
            synchronizeDefaultPluginIDs()
            save()
        } catch { report(YTrayError.invalidPlugin(directory.path)) }
    }

    @discardableResult
    private func installBundledExtensionIfNeeded(force: Bool = false) -> Bool {
        let previous = managedExtension
        guard ExtensionInstaller.shouldInstallBundledVersion(
            ExtensionInstaller.bundledVersion(),
            installedVersion: previous?.version,
            allowSameVersion: force
        ) else { return false }
        do {
            guard let bundled = try ExtensionInstaller.installBundled(
                into: applicationDirectory,
                ignoreOptOut: force,
                replaceExisting: force
            ) else { return false }
            registerManagedExtension(directory: bundled.directory)
            ExtensionInstaller.clearManagedExtensionRemoved(applicationDirectory: applicationDirectory)
            ExtensionInstaller.cleanupOldVersions(
                applicationDirectory: applicationDirectory,
                installedVersion: bundled.version
            )
            if let previous {
                if ExtensionInstaller.compareVersions(bundled.version, previous.version) == .orderedDescending {
                    extensionStatusMessage = "已从内置包升级 Yakit 插件 \(previous.version) → \(bundled.version)"
                } else {
                    extensionStatusMessage = "已重新准备内置 Yakit 插件 \(bundled.version)"
                }
            } else {
                extensionStatusMessage = "已准备内置 Yakit 插件 \(bundled.version)"
            }
            return true
        } catch {
            report(error)
            return false
        }
    }

    func updatePlugin(_ plugin: BrowserPlugin) {
        guard let index = plugins.firstIndex(where: { $0.id == plugin.id }) else { return }
        plugins[index] = plugin
        synchronizeDefaultPluginIDs()
        save()
    }

    func removePlugin(_ plugin: BrowserPlugin) {
        // The bundled Yakit Browser Agent may be disabled, but remains managed by YTray.
        guard managedExtension?.id != plugin.id else { return }
        plugins.removeAll { $0.id == plugin.id }
        synchronizeDefaultPluginIDs()
        save()
    }

    private func synchronizeDefaultPluginIDs() {
        // The plugin-page switch is the single source of truth: an enabled local
        // extension is loaded by every newly-created non-isolated instance. The
        // custom wizard may still override this list for one launch only.
        settings.defaultPluginIDs = plugins.filter(\.enabled).map(\.id)
    }

    func launch(mode: LaunchMode, customSettings: LaunchSettings? = nil,
                customPluginIDs: [UUID]? = nil, restoring history: BrowserInstance? = nil,
                launchUsesProxy: Bool? = nil) {
        guard !isLaunching else { return }
        let token = UUID()
        launchToken = token
        launchingMode = mode
        launchingUsesProxy = launchUsesProxy
        restoringInstanceID = history?.id
        launchMessage = "正在准备浏览器…"
        launchPhase = .preparing
        errorMessage = nil
        var configuration = customSettings ?? settings
        if mode == .isolated {
            configuration = LaunchSettings(defaultRuntimeID: configuration.defaultRuntimeID ?? settings.defaultRuntimeID)
        }
        if let history {
            configuration.defaultRuntimeID = history.runtimeID
            configuration.homeURL = history.startURL
            configuration.dockBadge = history.dockBadge ?? ""
        }
        let runtimeID = configuration.defaultRuntimeID ?? runtimes.first?.id
        guard let runtimeID, let runtime = runtimes.first(where: { $0.id == runtimeID }) else {
            finishLaunchFailure(YTrayError.noRuntime, token: token)
            return
        }
        let selectedIDs = customPluginIDs ?? configuration.defaultPluginIDs
        let selectedPlugins = plugins.filter { selectedIDs.contains($0.id) && $0.enabled }
        do {
            let requestedBadge = (history?.dockBadge ?? configuration.dockBadge)
                .trimmingCharacters(in: .whitespacesAndNewlines)
            let badge = requestedBadge.isEmpty ? nextAvailableDockBadge() : try DockBadgeLabel.normalize(requestedBadge)
            if runningInstances.contains(where: { $0.dockBadge == badge }) {
                throw YTrayError.launchFailed("Dock 角标 \(badge) 已被运行中的实例使用")
            }
            let result = try BrowserLauncher.launch(
                runtime: runtime,
                mode: mode,
                settings: configuration,
                plugins: selectedPlugins,
                applicationDirectory: applicationDirectory,
                ordinal: instances.count + 1,
                dockBadge: badge,
                restoring: history
            )
            processes[result.instance.id] = result.process
            let instanceID = result.instance.id.uuidString
            result.process.terminationHandler = { _ in
                DispatchQueue.main.async {
                    NotificationCenter.default.post(name: .ytrayProcessDidTerminate,
                                                    object: instanceID)
                }
            }
            if let history {
                instances.removeAll { $0.id == history.id }
            }
            instances.insert(result.instance, at: 0)
            save()
            launchingInstanceID = result.instance.id
            launchMessage = "正在启动 \(runtime.displayTitle)…"
            launchPhase = .waiting
            Task { await waitForBrowser(instance: result.instance, token: token) }
        } catch {
            finishLaunchFailure(error, token: token)
        }
    }

    func launch(runtime: BrowserRuntime, mode: LaunchMode = .quick, rememberSelection: Bool) {
        var configuration = settings
        configuration.defaultRuntimeID = runtime.id
        if rememberSelection {
            settings.defaultRuntimeID = runtime.id
            save()
        }
        launch(mode: mode, customSettings: configuration)
    }

    func selectDefaultRuntime(_ runtime: BrowserRuntime) {
        guard runtimes.contains(where: { $0.id == runtime.id }) else { return }
        settings.defaultRuntimeID = runtime.id
        save()
    }

    func updatePresetProxyServer(_ value: String) {
        settings.presetProxyServer = value
        if let endpoint = try? HTTPProxyAddress.split(value) {
            settings.presetProxyScheme = endpoint.scheme
            settings.presetProxyHost = endpoint.host
            settings.presetProxyPort = endpoint.port
        }
        resetProxyCheck()
        save()
    }

    func updatePresetProxyScheme(_ value: ProxyScheme) {
        settings.presetProxyScheme = value
        syncPresetProxyServer()
    }

    func updatePresetProxyHost(_ value: String) {
        settings.presetProxyHost = value
        syncPresetProxyServer()
    }

    func updatePresetProxyPort(_ value: Int) {
        settings.presetProxyPort = value
        syncPresetProxyServer()
    }

    func updatePresetProxyUsername(_ value: String) {
        settings.presetProxyUsername = value
        resetProxyCheck()
        save()
    }

    func updatePresetProxyPassword(_ value: String) {
        settings.presetProxyPassword = value
        resetProxyCheck()
        save()
    }

    func updatePresetProxyRemark(_ value: String) {
        settings.presetProxyRemark = value
        save()
    }

    func updatePresetProxyCheckTarget(_ value: String) {
        settings.presetProxyCheckTarget = value
        resetProxyCheck()
        save()
    }

    func selectProxyPreset(_ preset: ProxyPreset) {
        guard let endpoint = try? HTTPProxyAddress.split(preset.server) else { return }
        settings.presetProxyServer = endpoint.server
        settings.presetProxyScheme = endpoint.scheme
        settings.presetProxyHost = endpoint.host
        settings.presetProxyPort = endpoint.port
        settings.presetProxyUsername = preset.username
        settings.presetProxyPassword = preset.password
        settings.presetProxyRemark = preset.remark
        resetProxyCheck()
        save()
    }

    @discardableResult
    func rememberPresetProxy() -> String? {
        do {
            let normalized = try HTTPProxyAddress.build(
                scheme: settings.presetProxyScheme,
                host: settings.presetProxyHost,
                port: settings.presetProxyPort
            )
            let remark = settings.presetProxyRemark.trimmingCharacters(in: .whitespacesAndNewlines)
            let username = settings.presetProxyUsername.trimmingCharacters(in: .whitespacesAndNewlines)
            let password = settings.presetProxyPassword
            settings.presetProxyServer = normalized
            settings.presetProxyHost = try HTTPProxyAddress.split(normalized).host
            settings.presetProxyUsername = username
            settings.presetProxyRemark = remark
            settings.recentProxyPresets.removeAll {
                $0.server.caseInsensitiveCompare(normalized) == .orderedSame
                    && $0.username == username
            }
            settings.recentProxyPresets.insert(
                ProxyPreset(
                    server: normalized,
                    remark: remark,
                    username: username,
                    password: password
                ),
                at: 0
            )
            settings.recentProxyPresets = Array(settings.recentProxyPresets.prefix(5))
            save()
            return normalized
        } catch {
            report(error)
            return nil
        }
    }

    func checkPresetProxy() {
        guard proxyCheckPhase != .checking else { return }
        let endpoint: ProxyEndpoint
        do {
            let server = try HTTPProxyAddress.build(
                scheme: settings.presetProxyScheme,
                host: settings.presetProxyHost,
                port: settings.presetProxyPort
            )
            endpoint = try HTTPProxyAddress.split(server)
        } catch {
            proxyCheckPhase = .failure
            proxyCheckMessage = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
            return
        }
        let token = UUID()
        proxyCheckToken = token
        proxyCheckPhase = .checking
        proxyCheckMessage = "检测中 · 最多 10 秒"
        proxyCheckReport = nil
        let username = settings.presetProxyUsername.trimmingCharacters(in: .whitespacesAndNewlines)
        let password = settings.presetProxyPassword
        let customTarget = settings.presetProxyCheckTarget
        if let normalized = try? ProxyConnectivityChecker.normalizeTarget(customTarget),
           !customTarget.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            settings.presetProxyCheckTarget = normalized.absoluteString
            save()
        }
        Task { [weak self] in
            let report = await ProxyConnectivityChecker.checkDefaultTargets(
                endpoint: endpoint,
                username: username,
                password: password,
                customTarget: customTarget,
                timeout: ProxyConnectivityChecker.defaultTimeout
            )
            guard let self, self.proxyCheckToken == token else { return }
            self.proxyCheckReport = report
            self.proxyCheckPhase = report.isSuccess ? .success : .failure
            self.proxyCheckMessage = report.message
        }
    }

    func launchConfigured(usePresetProxy: Bool) {
        guard let configuration = quickLaunchConfiguration(usePresetProxy: usePresetProxy) else { return }
        launch(
            mode: .quick,
            customSettings: configuration,
            launchUsesProxy: usePresetProxy
        )
    }

    func quickLaunchConfiguration(usePresetProxy: Bool) -> LaunchSettings? {
        var configuration = settings
        if usePresetProxy {
            guard let proxy = rememberPresetProxy() else { return nil }
            configuration.proxyServer = proxy
            configuration.proxyUsername = settings.presetProxyUsername
            configuration.proxyPassword = settings.presetProxyPassword
        } else {
            configuration.proxyServer = ""
            configuration.proxyUsername = ""
            configuration.proxyPassword = ""
        }
        return configuration
    }

    func restoreHistory(_ instance: BrowserInstance) {
        guard instance.status != .running else { return }
        var configuration = instance.settingsSnapshot ?? settings
        configuration.defaultRuntimeID = instance.runtimeID
        configuration.homeURL = instance.startURL
        configuration.dockBadge = instance.dockBadge ?? ""
        if !configuration.proxyServer.isEmpty,
           configuration.proxyUsername == settings.presetProxyUsername,
           configuration.proxyPassword.isEmpty {
            configuration.proxyPassword = settings.presetProxyPassword
        }
        let restoredPluginIDs = instance.pluginIDs ?? configuration.defaultPluginIDs
        launch(
            mode: instance.mode,
            customSettings: configuration,
            customPluginIDs: restoredPluginIDs,
            restoring: instance,
            launchUsesProxy: !configuration.proxyServer.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        )
    }

    func runtime(for instance: BrowserInstance) -> BrowserRuntime? {
        runtimes.first(where: { $0.id == instance.runtimeID })
    }

    func stop(_ instance: BrowserInstance) {
        Task { await archiveAndStop(instance) }
    }

    func removeHistory(_ instance: BrowserInstance) {
        guard instance.status != .running else { return }
        instances.removeAll { $0.id == instance.id }
        BrowserProcessIcon.remove(instanceID: instance.id, applicationDirectory: applicationDirectory)
        InstanceThumbnailStorage.removeThumbnail(for: instance, applicationDirectory: applicationDirectory)
        save()
    }

    func removeAllHistory() {
        let history = instances.filter { $0.status != .running }
        for instance in history {
            BrowserProcessIcon.remove(instanceID: instance.id, applicationDirectory: applicationDirectory)
            InstanceThumbnailStorage.removeThumbnail(for: instance, applicationDirectory: applicationDirectory)
        }
        instances.removeAll { $0.status != .running }
        save()
    }

    func renameHistory(_ instance: BrowserInstance, name: String) {
        guard instance.status != .running,
              let index = instances.firstIndex(where: { $0.id == instance.id }) else { return }
        let value = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !value.isEmpty else { return }
        instances[index].name = value
        save()
    }

    func capture(_ instance: BrowserInstance) async {
        guard instance.status == .running else { return }
        do {
            let pictures = FileManager.default.urls(for: .picturesDirectory, in: .userDomainMask)[0]
                .appendingPathComponent("YTray", isDirectory: true)
            let output = try await ScreenshotService.capture(debugPort: instance.debugPort,
                                                             instanceID: instance.id,
                                                             outputDirectory: pictures)
            if let index = instances.firstIndex(where: { $0.id == instance.id }) {
                instances[index].lastScreenshotPath = output.path
            }
            save()
            NSWorkspace.shared.activateFileViewerSelecting([output])
        } catch { report(error) }
    }

    func revealProfile(_ instance: BrowserInstance) {
        NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: instance.profilePath)])
    }

    func focus(_ instance: BrowserInstance) {
        NSRunningApplication(processIdentifier: instance.processID)?.activate(options: [.activateAllWindows])
    }

    func saveSettings() { save() }

    private func syncPresetProxyServer() {
        if let server = try? HTTPProxyAddress.build(
            scheme: settings.presetProxyScheme,
            host: settings.presetProxyHost,
            port: settings.presetProxyPort
        ) {
            settings.presetProxyServer = server
        }
        resetProxyCheck()
        save()
    }

    private func resetProxyCheck() {
        proxyCheckToken = nil
        proxyCheckPhase = .idle
        proxyCheckMessage = ""
        proxyCheckReport = nil
    }

    @discardableResult
    private func upsert(_ runtime: BrowserRuntime, persist: Bool = true) -> BrowserRuntime {
        var stored = runtime
        if let index = runtimes.firstIndex(where: { $0.executablePath == runtime.executablePath }) {
            stored.id = runtimes[index].id
            stored.createdAt = runtimes[index].createdAt
            runtimes[index] = stored
        } else {
            runtimes.append(stored)
        }
        if settings.defaultRuntimeID == nil { settings.defaultRuntimeID = stored.id }
        if persist { save() }
        return stored
    }

    private func load() {
        guard let data = try? Data(contentsOf: stateURL) else { return }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        guard let state = try? decoder.decode(PersistedState.self, from: data) else { return }
        runtimes = state.runtimes
        plugins = state.plugins
        instances = state.instances
        settings = state.settings
        synchronizeDefaultPluginIDs()
        consolidateHistoryBadges()
        save()
    }

    private func save() {
        let state = PersistedState(runtimes: runtimes, plugins: plugins, instances: instances, settings: settings)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        encoder.dateEncodingStrategy = .iso8601
        guard let data = try? encoder.encode(state) else { return }
        do {
            try data.write(to: stateURL, options: .atomic)
            try FileManager.default.setAttributes(
                [.posixPermissions: NSNumber(value: Int16(0o600))],
                ofItemAtPath: stateURL.path
            )
        } catch {
            return
        }
    }

    private func refreshProcessStates() {
        var changed = false
        for index in instances.indices where instances[index].status == .running {
            let pid = instances[index].processID
            if pid <= 0 || Darwin.kill(pid, 0) != 0 {
                instances[index].status = .stopped
                BrowserProcessIcon.remove(
                    instanceID: instances[index].id,
                    applicationDirectory: applicationDirectory
                )
                ProxyAuthenticationExtension.remove(
                    instanceID: instances[index].id,
                    applicationDirectory: applicationDirectory
                )
                changed = true
            }
        }
        if changed {
            consolidateHistoryBadges()
            save()
        }
    }

    @objc private func refreshTimerFired() {
        refreshProcessStates()
        Task { await refreshRunningPageTitles() }
        scheduleAutomaticThumbnailRefresh()
    }

    @objc private func processDidTerminate(_ notification: Notification) {
        guard let value = notification.object as? String, let id = UUID(uuidString: value) else { return }
        markStopped(id)
    }

    private func markStopped(_ id: UUID) {
        processes[id] = nil
        BrowserProcessIcon.remove(instanceID: id, applicationDirectory: applicationDirectory)
        ProxyAuthenticationExtension.remove(instanceID: id, applicationDirectory: applicationDirectory)
        if launchingInstanceID == id, let token = launchToken, isLaunching {
            finishLaunchFailure(
                YTrayError.launchFailed("浏览器进程在完成启动前退出"),
                token: token
            )
        }
        guard let index = instances.firstIndex(where: { $0.id == id }) else { return }
        instances[index].status = .stopped
        consolidateHistoryBadges()
        save()
    }

    private func consolidateHistoryBadges() {
        let history = instances.filter { $0.status != .running }
        var newestByBadge: [String: BrowserInstance] = [:]
        for instance in history {
            guard let badge = instance.dockBadge, !badge.isEmpty else { continue }
            if let current = newestByBadge[badge], current.startedAt >= instance.startedAt { continue }
            newestByBadge[badge] = instance
        }

        let removed = instances.filter { instance in
            guard instance.status != .running,
                  let badge = instance.dockBadge,
                  let newest = newestByBadge[badge] else { return false }
            return instance.id != newest.id
        }
        guard !removed.isEmpty else { return }
        let removedIDs = Set(removed.map(\.id))
        instances.removeAll { removedIDs.contains($0.id) }
        for instance in removed {
            BrowserProcessIcon.remove(instanceID: instance.id, applicationDirectory: applicationDirectory)
            InstanceThumbnailStorage.removeThumbnail(for: instance, applicationDirectory: applicationDirectory)
        }
    }

    private func archiveAndStop(_ instance: BrowserInstance) async {
        await captureAndStoreThumbnail(instance)
        await refreshPageTitle(for: instance)
        if let process = processes[instance.id], process.isRunning { process.terminate() }
        else if let application = NSRunningApplication(processIdentifier: instance.processID) {
            _ = application.terminate()
        } else if instance.processID > 0 { Darwin.kill(instance.processID, SIGTERM) }
    }

    private func refreshRunningPageTitles() async {
        guard !titleRefreshInFlight else { return }
        let targets = runningInstances.map { ($0.id, $0.debugPort) }
        guard !targets.isEmpty else { return }
        titleRefreshInFlight = true
        defer { titleRefreshInFlight = false }

        var updates: [(UUID, ScreenshotService.PageState)] = []
        await withTaskGroup(of: (UUID, ScreenshotService.PageState?).self) { group in
            for (id, port) in targets {
                group.addTask {
                    (id, await ScreenshotService.currentPageState(debugPort: port))
                }
            }
            for await (id, state) in group {
                if let state { updates.append((id, state)) }
            }
        }

        var changed = false
        for (id, state) in updates {
            guard let index = instances.firstIndex(where: { $0.id == id }) else { continue }
            if !state.title.isEmpty, instances[index].lastPageTitle != state.title {
                instances[index].lastPageTitle = state.title
                changed = true
            }
            if !state.url.isEmpty, instances[index].lastPageURL != state.url {
                instances[index].lastPageURL = state.url
                changed = true
            }
        }
        if changed { save() }
    }

    private func refreshPageTitle(for instance: BrowserInstance) async {
        guard let state = await ScreenshotService.currentPageState(debugPort: instance.debugPort, attempts: 5),
              let index = instances.firstIndex(where: { $0.id == instance.id }) else { return }
        var changed = false
        if !state.title.isEmpty, instances[index].lastPageTitle != state.title {
            instances[index].lastPageTitle = state.title
            changed = true
        }
        if !state.url.isEmpty, instances[index].lastPageURL != state.url {
            instances[index].lastPageURL = state.url
            changed = true
        }
        if changed { save() }
    }

    private func scheduleAutomaticThumbnailRefresh(force instanceID: UUID? = nil) {
        let now = Date()
        for instance in runningInstances {
            let isForced = instance.id == instanceID
            let lastAttempt = lastThumbnailAttempt[instance.id]
                ?? instance.thumbnailUpdatedAt
                ?? .distantPast
            guard isForced || now.timeIntervalSince(lastAttempt) >= Self.thumbnailRefreshInterval else {
                continue
            }
            guard !thumbnailCapturesInFlight.contains(instance.id) else { continue }
            thumbnailCapturesInFlight.insert(instance.id)
            lastThumbnailAttempt[instance.id] = now
            Task { [weak self] in
                guard let self else { return }
                if instance.thumbnailPath == nil {
                    try? await Task.sleep(nanoseconds: 450_000_000)
                }
                await self.captureAndStoreThumbnail(instance)
                self.thumbnailCapturesInFlight.remove(instance.id)
            }
        }
    }

    private func captureAndStoreThumbnail(_ instance: BrowserInstance) async {
        guard instances.contains(where: { $0.id == instance.id && $0.status == .running }) else { return }
        let output = InstanceThumbnailStorage.thumbnailURL(
            for: instance.id,
            applicationDirectory: applicationDirectory
        )
        guard let captured = try? await ScreenshotService.captureThumbnail(
            debugPort: instance.debugPort,
            instanceID: instance.id,
            outputURL: output
        ), let index = instances.firstIndex(where: { $0.id == instance.id }) else { return }
        instances[index].thumbnailPath = captured.path
        instances[index].thumbnailUpdatedAt = Date()
        save()
    }

    private func report(_ error: Error) {
        errorMessage = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
    }

    private func waitForBrowser(instance: BrowserInstance, token: UUID) async {
        let ready = await ScreenshotService.waitUntilReady(debugPort: instance.debugPort)
        guard launchToken == token else { return }
        guard ready else {
            let stillRunning = instance.processID > 0 && Darwin.kill(instance.processID, 0) == 0
            let detail = stillRunning
                ? "浏览器进程已经创建，但调试端口未在 15 秒内就绪"
                : "浏览器进程在完成启动前退出"
            finishLaunchFailure(YTrayError.launchFailed(detail), token: token)
            return
        }
        let usesProxyAuthentication = !(instance.settingsSnapshot?.proxyUsername ?? "").isEmpty
            || !(instance.settingsSnapshot?.proxyPassword ?? "").isEmpty
        let restoreURL = instance.lastPageURL?.trimmingCharacters(in: .whitespacesAndNewlines)
        let navigationTarget: String? = restoringInstanceID == instance.id
            ? ((restoreURL?.isEmpty == false) ? restoreURL : instance.startURL)
            : (usesProxyAuthentication ? instance.startURL : nil)
        if let navigationTarget, !navigationTarget.isEmpty {
            do {
                if usesProxyAuthentication {
                    try? await Task.sleep(nanoseconds: 900_000_000)
                }
                try await ScreenshotService.navigate(debugPort: instance.debugPort, to: navigationTarget)
            } catch {
                finishLaunchFailure(error, token: token)
                return
            }
        }
        launchMessage = "\(instance.runtimeName) 已启动"
        launchPhase = .succeeded
        launchingMode = nil
        launchingUsesProxy = nil
        launchingInstanceID = nil
        restoringInstanceID = nil
        scheduleAutomaticThumbnailRefresh(force: instance.id)
        try? await Task.sleep(nanoseconds: 1_200_000_000)
        guard launchToken == token, launchPhase == .succeeded else { return }
        launchMessage = ""
        launchPhase = .idle
        launchToken = nil
    }

    private func finishLaunchFailure(_ error: Error, token: UUID) {
        guard launchToken == token else { return }
        launchPhase = .idle
        launchMessage = ""
        launchingMode = nil
        launchingUsesProxy = nil
        launchingInstanceID = nil
        restoringInstanceID = nil
        launchToken = nil
        report(error)
    }

    private func nextAvailableDockBadge() -> String {
        let used = Set(runningInstances.compactMap(\.dockBadge))
        for ordinal in 1...702 {
            let candidate = DockBadgeLabel.defaultLabel(for: ordinal)
            if !used.contains(candidate) { return candidate }
        }
        return "ZZ"
    }
}

enum InstanceThumbnailStorage {
    static func thumbnailURL(for instanceID: UUID, applicationDirectory: URL) -> URL {
        applicationDirectory
            .appendingPathComponent("Thumbnails", isDirectory: true)
            .appendingPathComponent("\(instanceID.uuidString).jpg")
    }

    static func removeThumbnail(for instance: BrowserInstance, applicationDirectory: URL) {
        let managedURL = thumbnailURL(for: instance.id, applicationDirectory: applicationDirectory)
        try? FileManager.default.removeItem(at: managedURL)
    }
}

private extension Notification.Name {
    static let ytrayProcessDidTerminate = Notification.Name("YTrayProcessDidTerminate")
}
