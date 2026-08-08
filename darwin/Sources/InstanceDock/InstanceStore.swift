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
    @Published private(set) var launchPhase: BrowserLaunchPhase = .idle
    @Published private(set) var launchMessage = ""
    @Published private(set) var launchingMode: LaunchMode?
    @Published private(set) var restoringInstanceID: UUID?

    let applicationDirectory: URL
    private let stateURL: URL
    private var processes: [UUID: Process] = [:]
    private var timer: Timer?
    private var titleRefreshInFlight = false
    private var launchToken: UUID?
    private var launchingInstanceID: UUID?

    var isLaunching: Bool {
        launchPhase == .preparing || launchPhase == .waiting
    }

    init(applicationDirectory: URL? = nil, discoverSystemBrowsers: Bool = true) {
        let base = applicationDirectory ?? FileManager.default.urls(
            for: .applicationSupportDirectory, in: .userDomainMask
        )[0].appendingPathComponent("InstanceDock", isDirectory: true)
        self.applicationDirectory = base
        self.stateURL = base.appendingPathComponent("state.json")
        super.init()
        try? FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        load()
        if discoverSystemBrowsers { refreshSystemBrowsers() }
        refreshProcessStates()
        NotificationCenter.default.addObserver(self, selector: #selector(processDidTerminate(_:)),
                                               name: .instanceDockProcessDidTerminate, object: nil)
        timer = Timer.scheduledTimer(timeInterval: 2, target: self,
                                     selector: #selector(refreshTimerFired), userInfo: nil, repeats: true)
    }

    var runningInstances: [BrowserInstance] { instances.filter { $0.status == .running } }
    var historyInstances: [BrowserInstance] { instances.filter { $0.status != .running } }
    var systemRuntimes: [BrowserRuntime] {
        runtimes.filter(\.isSystemEnvironment).sorted { $0.displayTitle < $1.displayTitle }
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

    @discardableResult
    func addLocalRuntime(selectedURL: URL) -> BrowserRuntime? {
        guard let runtime = SystemBrowserDiscovery.inspect(selectedURL: selectedURL) else {
            report(InstanceDockError.invalidExecutable(selectedURL.path)); return nil
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
            report(InstanceDockError.launchFailed("该运行时仍有实例正在运行")); return
        }
        runtimes.removeAll { $0.id == runtime.id }
        if settings.defaultRuntimeID == runtime.id { settings.defaultRuntimeID = runtimes.first?.id }
        save()
    }

    func addPlugin(directory: URL) {
        do {
            let data = try Data(contentsOf: directory.appendingPathComponent("manifest.json"))
            let manifest = try JSONDecoder().decode(PluginManifest.self, from: data)
            plugins.removeAll { $0.path == directory.path }
            plugins.append(BrowserPlugin(name: manifest.name, version: manifest.version, path: directory.path,
                                          manifestVersion: manifest.manifestVersion))
            save()
        } catch { report(InstanceDockError.invalidPlugin(directory.path)) }
    }

    func updatePlugin(_ plugin: BrowserPlugin) {
        guard let index = plugins.firstIndex(where: { $0.id == plugin.id }) else { return }
        plugins[index] = plugin
        save()
    }

    func removePlugin(_ plugin: BrowserPlugin) {
        plugins.removeAll { $0.id == plugin.id }
        settings.defaultPluginIDs.removeAll { $0 == plugin.id }
        save()
    }

    func launch(mode: LaunchMode, customSettings: LaunchSettings? = nil,
                customPluginIDs: [UUID]? = nil, restoring history: BrowserInstance? = nil) {
        guard !isLaunching else { return }
        let token = UUID()
        launchToken = token
        launchingMode = mode
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
            finishLaunchFailure(InstanceDockError.noRuntime, token: token)
            return
        }
        let selectedIDs = customPluginIDs ?? configuration.defaultPluginIDs
        let selectedPlugins = plugins.filter { selectedIDs.contains($0.id) && $0.enabled }
        do {
            let requestedBadge = (history?.dockBadge ?? configuration.dockBadge)
                .trimmingCharacters(in: .whitespacesAndNewlines)
            let badge = requestedBadge.isEmpty ? nextAvailableDockBadge() : try DockBadgeLabel.normalize(requestedBadge)
            if runningInstances.contains(where: { $0.dockBadge == badge }) {
                throw InstanceDockError.launchFailed("Dock 角标 \(badge) 已被运行中的实例使用")
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
                    NotificationCenter.default.post(name: .instanceDockProcessDidTerminate,
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

    func restoreHistory(_ instance: BrowserInstance) {
        guard instance.status != .running else { return }
        var configuration = instance.settingsSnapshot ?? settings
        configuration.defaultRuntimeID = instance.runtimeID
        configuration.homeURL = instance.startURL
        configuration.dockBadge = instance.dockBadge ?? ""
        let restoredPluginIDs = instance.pluginIDs ?? configuration.defaultPluginIDs
        launch(
            mode: instance.mode,
            customSettings: configuration,
            customPluginIDs: restoredPluginIDs,
            restoring: instance
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
        save()
    }

    func removeAllHistory() {
        let history = instances.filter { $0.status != .running }
        for instance in history {
            BrowserProcessIcon.remove(instanceID: instance.id, applicationDirectory: applicationDirectory)
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
                .appendingPathComponent("InstanceDock", isDirectory: true)
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
        consolidateHistoryBadges()
        save()
    }

    private func save() {
        let state = PersistedState(runtimes: runtimes, plugins: plugins, instances: instances, settings: settings)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        encoder.dateEncodingStrategy = .iso8601
        guard let data = try? encoder.encode(state) else { return }
        try? data.write(to: stateURL, options: .atomic)
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
    }

    @objc private func processDidTerminate(_ notification: Notification) {
        guard let value = notification.object as? String, let id = UUID(uuidString: value) else { return }
        markStopped(id)
    }

    private func markStopped(_ id: UUID) {
        processes[id] = nil
        BrowserProcessIcon.remove(instanceID: id, applicationDirectory: applicationDirectory)
        if launchingInstanceID == id, let token = launchToken, isLaunching {
            finishLaunchFailure(
                InstanceDockError.launchFailed("浏览器进程在完成启动前退出"),
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
        }
    }

    private func archiveAndStop(_ instance: BrowserInstance) async {
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
            finishLaunchFailure(InstanceDockError.launchFailed(detail), token: token)
            return
        }
        if restoringInstanceID == instance.id,
           let restoreURL = instance.lastPageURL?.trimmingCharacters(in: .whitespacesAndNewlines),
           !restoreURL.isEmpty,
           !restoreURL.hasPrefix("chrome://") {
            do {
                try await ScreenshotService.navigate(debugPort: instance.debugPort, to: restoreURL)
            } catch {
                finishLaunchFailure(error, token: token)
                return
            }
        }
        launchMessage = "\(instance.runtimeName) 已启动"
        launchPhase = .succeeded
        launchingMode = nil
        launchingInstanceID = nil
        restoringInstanceID = nil
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

private extension Notification.Name {
    static let instanceDockProcessDidTerminate = Notification.Name("InstanceDockProcessDidTerminate")
}
