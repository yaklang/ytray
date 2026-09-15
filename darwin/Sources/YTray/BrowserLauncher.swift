import Foundation
import Darwin

enum BrowserLauncher {
    static let proxyAuthenticationBootstrapURL = "data:text/html,<title>YTray</title>"

    static func supportsCommandLineExtensions(runtimeKind: BrowserKind) -> Bool {
        switch runtimeKind {
        case .chrome, .chromeBeta, .chromeCanary:
            return false
        case .chromeForTesting, .chromium, .edge:
            return true
        }
    }

    static func resolvedIdentityColor(
        badge: String,
        settings: LaunchSettings
    ) -> BrowserIdentityColor? {
        guard settings.colorizeBrowserInstances,
              AppEnvironment.instanceColorThemesEnabled else { return nil }
        return BrowserIdentityColor.color(for: badge)
    }

    struct LaunchResult {
        let process: Process
        let instance: BrowserInstance
        var extensionPaths: [String] = []
        var legacyExtensionLoadingExpected = false
        var deferredStartupURL: String? = nil
    }

    static func buildProcessArguments(iconURL: URL, browserExecutable: URL,
                                      browserArguments: [String]) -> [String] {
        ["--browser-process", iconURL.path, browserExecutable.path] + browserArguments
    }

    static func buildArguments(mode: LaunchMode, settings: LaunchSettings, profilePath: String,
                               debugPort: Int, plugins: [BrowserPlugin],
                               runtimeKind: BrowserKind? = nil,
                               internalExtensionPaths: [String] = [],
                               identityColor: BrowserIdentityColor? = nil,
                               restoreLastSession: Bool = false,
                               managedInstanceID: UUID? = nil,
                               instanceBadge: String? = nil,
                               runtimeVersion: String? = nil,
                               deferExtensionStartup: Bool = false) throws -> [String] {
        var arguments = [
            "--user-data-dir=\(profilePath)",
            "--remote-debugging-address=127.0.0.1",
            "--remote-debugging-port=\(debugPort)",
            "--remote-allow-origins=http://127.0.0.1:\(debugPort)",
            "--no-first-run", "--no-default-browser-check",
            // YTray profiles are automation/testing profiles. Using Chromium's
            // test keychain prevents macOS Safe Storage authorization dialogs
            // from blocking startup when a managed browser build changes.
            "--use-mock-keychain",
        ]
        if let identityColor {
            arguments.append(identityColor.chromeThemeArgument)
        }
        if runtimeKind == .chromeForTesting {
            arguments.append("--disable-infobars")
        }
        if settings.ignoreCertificateErrors {
            arguments.append("--ignore-certificate-errors")
        }
        if settings.useTestType {
            arguments.append("--test-type")
        }
        if mode != .isolated {
            arguments += ["--disable-background-networking", "--disable-component-update",
                          "--disable-default-apps", "--disable-sync", "--disable-translate"]
            if settings.disableNotifications { arguments.append("--disable-notifications") }
            if settings.restrictWebRTC {
                arguments += ["--disable-webrtc",
                              "--force-webrtc-ip-handling-policy=disable_non_proxied_udp",
                              "--webrtc-ip-handling-policy=disable_non_proxied_udp"]
            }
            let proxy = settings.proxyServer.trimmingCharacters(in: .whitespacesAndNewlines)
            if proxy.isEmpty {
                // "无代理启动" must also bypass the macOS system proxy. Merely
                // omitting --proxy-server would allow Chrome to inherit it.
                arguments.append("--no-proxy-server")
            } else {
                arguments.append("--proxy-server=\(proxy)")
            }
            let paths = internalExtensionPaths + plugins.filter(\.enabled).map(\.path)
            if !paths.isEmpty {
                let joined = paths.joined(separator: ",")
                arguments.append("--load-extension=\(joined)")
                // Branded Chrome 137+ ignores CLI loading; its obsolete allowlist
                // can also disable extensions subsequently loaded through CDP.
                if runtimeKind == nil || BrowserExtensionService.legacyLoadingExpected(
                    runtimeKind: runtimeKind, version: runtimeVersion
                ) {
                    arguments.append("--disable-extensions-except=\(joined)")
                }
            }
            for line in settings.additionalFlags.components(separatedBy: .newlines) {
                let flag = line.trimmingCharacters(in: .whitespacesAndNewlines)
                guard !flag.isEmpty else { continue }
                guard flag.hasPrefix("--") else { throw YTrayError.invalidFlag(flag) }
                if LaunchSettings.blockedCustomPrefixes.contains(where: { flag.hasPrefix($0) }) {
                    throw YTrayError.invalidFlag(flag)
                }
                arguments.append(flag)
            }
        }
        let target = settings.homeURL.trimmingCharacters(in: .whitespacesAndNewlines)
        guard target.hasPrefix("chrome://") || URL(string: target)?.scheme != nil else {
            throw YTrayError.invalidURL(target)
        }
        if deferExtensionStartup && mode != .isolated {
            arguments.append("about:blank")
            return arguments
        }
        let managedExtensionID = plugins.lazy
            .filter { $0.enabled && $0.name == ExtensionInstaller.extensionName }
            .compactMap { ExtensionInstaller.chromiumExtensionID(for: $0) }
            .first
        let managedExtensionLoaded = mode != .isolated && managedInstanceID != nil
            && instanceBadge != nil && managedExtensionID != nil
        if restoreLastSession {
            arguments.append("--restore-last-session")
            if managedExtensionLoaded {
                arguments.append(try managedBrowserBootstrapURL(
                    extensionID: managedExtensionID!, instanceID: managedInstanceID!,
                    badge: instanceBadge!, target: target, restore: true, proxyServer: settings.proxyServer
                ))
            }
            return arguments
        }
        if managedExtensionLoaded {
            arguments.append(try managedBrowserBootstrapURL(
                extensionID: managedExtensionID!, instanceID: managedInstanceID!,
                badge: instanceBadge!, target: target, restore: false, proxyServer: settings.proxyServer
            ))
        } else {
            arguments.append(target)
        }
        return arguments
    }

    static func managedBrowserBootstrapURL(
        extensionID: String, instanceID: UUID, badge: String, target: String, restore: Bool, proxyServer: String = ""
    ) throws -> String {
        var components = URLComponents()
        components.scheme = "chrome-extension"
        components.host = extensionID
        components.path = "/ytray-bootstrap.html"
        components.queryItems = [
            URLQueryItem(name: "manager", value: "ytray"),
            URLQueryItem(name: "instanceId", value: instanceID.uuidString),
            URLQueryItem(name: "badge", value: try DockBadgeLabel.normalize(badge)),
            URLQueryItem(name: "target", value: target),
            URLQueryItem(name: "restore", value: restore ? "1" : "0"),
            URLQueryItem(name: "startupProxy", value: proxyServer.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? "direct" : try HTTPProxyAddress.normalize(proxyServer)),
        ]
        guard let value = components.string else { throw YTrayError.invalidURL(target) }
        return value
    }

    static func preparePinnedExtensions(
        profile: URL, loadedPlugins: [BrowserPlugin], configuredPlugins: [BrowserPlugin]? = nil
    ) throws {
        let loaded = loadedPlugins.compactMap { plugin in
            ExtensionInstaller.chromiumExtensionID(for: plugin).map { (plugin: plugin, id: $0) }
        }
        let controlledIDs = Set((configuredPlugins ?? loadedPlugins).compactMap {
            ExtensionInstaller.chromiumExtensionID(for: $0)
        })
        guard !controlledIDs.isEmpty else { return }

        let defaultProfile = profile.appendingPathComponent("Default", isDirectory: true)
        let preferencesURL = defaultProfile.appendingPathComponent("Preferences")
        try FileManager.default.createDirectory(at: defaultProfile, withIntermediateDirectories: true)
        var preferences: [String: Any] = [:]
        if FileManager.default.fileExists(atPath: preferencesURL.path) {
            let object = try JSONSerialization.jsonObject(with: Data(contentsOf: preferencesURL))
            guard let decoded = object as? [String: Any] else {
                throw YTrayError.launchFailed("浏览器 Preferences 格式无效")
            }
            preferences = decoded
        }
        var extensions = preferences["extensions"] as? [String: Any] ?? [:]
        var pinned = (extensions["pinned_extensions"] as? [String] ?? [])
            .filter { !controlledIDs.contains($0) }
        for item in loaded where item.plugin.pinToToolbar == true && !pinned.contains(item.id) {
            pinned.append(item.id)
        }
        extensions["pinned_extensions"] = pinned
        preferences["extensions"] = extensions
        let data = try JSONSerialization.data(withJSONObject: preferences)
        try data.write(to: preferencesURL, options: .atomic)
    }

    static func launch(runtime: BrowserRuntime, mode: LaunchMode, settings: LaunchSettings,
                       plugins: [BrowserPlugin], applicationDirectory: URL, ordinal: Int,
                       dockBadge: String, restoring history: BrowserInstance? = nil,
                       configuredPlugins: [BrowserPlugin]? = nil,
                       launcherExecutable: URL? = nil) throws -> LaunchResult {
        let executable = URL(fileURLWithPath: runtime.executablePath)
        guard FileManager.default.isExecutableFile(atPath: executable.path) else {
            throw YTrayError.invalidExecutable(executable.path)
        }
        let id = history?.id ?? UUID()
        let normalizedBadge = try DockBadgeLabel.normalize(dockBadge)
        let profile = history.map { URL(fileURLWithPath: $0.profilePath, isDirectory: true) }
            ?? applicationDirectory.appendingPathComponent("Profiles/\(id.uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: profile, withIntermediateDirectories: true)
        let port = nextAvailablePort(startingAt: max(1024, settings.debugPort))
        let usesProxyAuthentication = mode != .isolated
            && (!settings.proxyUsername.isEmpty || !settings.proxyPassword.isEmpty)
        let proxyAuthExtension = usesProxyAuthentication ? try ProxyAuthenticationExtension.write(
            instanceID: id,
            username: settings.proxyUsername,
            password: settings.proxyPassword,
            proxyServer: settings.proxyServer,
            applicationDirectory: applicationDirectory
        ) : nil
        let extensionPaths = mode == .isolated ? []
            : (proxyAuthExtension.map { [$0.path] } ?? []) + plugins.filter(\.enabled).map(\.path)
        let startupURL = try deferredStartupURL(settings: settings, plugins: plugins,
                                               instanceID: id, badge: normalizedBadge, history: history)
        let arguments: [String]
        let dockIdentityColor = AppEnvironment.instanceColorThemesEnabled
            ? BrowserIdentityColor.color(for: normalizedBadge)
            : nil
        let themeIdentityColor = resolvedIdentityColor(badge: normalizedBadge, settings: settings)
        do {
            // A generated theme is installed only for a brand-new profile.
            // Restored profiles keep their persisted or user-selected
            // theme, so a normal restart never overwrites appearance choices.
            arguments = try buildArguments(
                mode: mode,
                settings: settings,
                profilePath: profile.path,
                debugPort: port,
                plugins: plugins,
                runtimeKind: runtime.kind,
                internalExtensionPaths: proxyAuthExtension.map { [$0.path] } ?? [],
                identityColor: history == nil ? themeIdentityColor : nil,
                restoreLastSession: history != nil && !usesProxyAuthentication,
                managedInstanceID: id,
                instanceBadge: normalizedBadge,
                runtimeVersion: runtime.version,
                deferExtensionStartup: !extensionPaths.isEmpty
            )
            if mode != .isolated {
                try preparePinnedExtensions(
                    profile: profile, loadedPlugins: plugins, configuredPlugins: configuredPlugins
                )
            }
        } catch {
            ProxyAuthenticationExtension.remove(instanceID: id, applicationDirectory: applicationDirectory)
            throw error
        }
        let logs = applicationDirectory.appendingPathComponent("Logs", isDirectory: true)
        let logURL = logs.appendingPathComponent("\(id.uuidString).log")
        let log: FileHandle
        do {
            try FileManager.default.createDirectory(at: logs, withIntermediateDirectories: true)
            FileManager.default.createFile(atPath: logURL.path, contents: nil)
            log = try FileHandle(forWritingTo: logURL)
        } catch {
            ProxyAuthenticationExtension.remove(instanceID: id, applicationDirectory: applicationDirectory)
            throw error
        }
        let iconURL: URL
        do {
            iconURL = try BrowserProcessIcon.write(
                sourceExecutable: executable,
                badge: normalizedBadge,
                instanceID: id,
                applicationDirectory: applicationDirectory,
                identityColor: dockIdentityColor
            )
        } catch {
            try? log.close()
            ProxyAuthenticationExtension.remove(instanceID: id, applicationDirectory: applicationDirectory)
            throw error
        }
        let process = Process()
        guard let launcher = launcherExecutable ?? Bundle.main.executableURL else {
            try? log.close()
            BrowserProcessIcon.remove(instanceID: id, applicationDirectory: applicationDirectory)
            ProxyAuthenticationExtension.remove(instanceID: id, applicationDirectory: applicationDirectory)
            throw YTrayError.launchFailed("找不到 YTray 启动器")
        }
        process.executableURL = launcher
        process.arguments = buildProcessArguments(
            iconURL: iconURL,
            browserExecutable: executable,
            browserArguments: arguments
        )
        process.standardOutput = log
        process.standardError = log
        do {
            try process.run()
            try? log.close()
        } catch {
            try? log.close()
            BrowserProcessIcon.remove(instanceID: id, applicationDirectory: applicationDirectory)
            ProxyAuthenticationExtension.remove(instanceID: id, applicationDirectory: applicationDirectory)
            throw YTrayError.launchFailed(error.localizedDescription)
        }
        let instance = BrowserInstance(
            id: id, name: history?.name ?? "浏览器实例 \(ordinal)", runtimeID: runtime.id,
            runtimeName: runtime.displayTitle, runtimeVersion: runtime.version,
            runtimeKind: runtime.kind, runtimeSource: runtime.source,
            mode: mode, processID: process.processIdentifier,
            debugPort: port, profilePath: profile.path,
            startURL: history?.startURL ?? settings.homeURL,
            startedAt: Date(), status: .running,
            lastScreenshotPath: history?.lastScreenshotPath,
            thumbnailPath: history?.thumbnailPath,
            thumbnailUpdatedAt: history?.thumbnailUpdatedAt,
            lastPageTitle: history?.lastPageTitle,
            lastPageURL: history?.lastPageURL,
            dockBadge: normalizedBadge,
            settingsSnapshot: settings,
            pluginIDs: plugins.map(\.id)
        )
        return LaunchResult(
            process: process, instance: instance,
            extensionPaths: extensionPaths,
            legacyExtensionLoadingExpected: BrowserExtensionService.legacyLoadingExpected(
                runtimeKind: runtime.kind, version: runtime.version
            ),
            deferredStartupURL: extensionPaths.isEmpty ? nil : startupURL
        )
    }

    static func deferredStartupURL(settings: LaunchSettings, plugins: [BrowserPlugin],
                                   instanceID: UUID, badge: String, history: BrowserInstance?) throws -> String {
        let restoredURL = history?.lastPageURL?.trimmingCharacters(in: .whitespacesAndNewlines)
        let target = restoredURL.flatMap { $0.isEmpty ? nil : $0 } ?? settings.homeURL
        if let managed = plugins.first(where: { $0.enabled && $0.name == ExtensionInstaller.extensionName }),
           let extensionID = ExtensionInstaller.chromiumExtensionID(for: managed) {
            return try managedBrowserBootstrapURL(extensionID: extensionID, instanceID: instanceID,
                                                  badge: badge, target: target, restore: history != nil,
                                                  proxyServer: settings.proxyServer)
        }
        return target
    }

    static func nextAvailablePort(startingAt requested: Int) -> Int {
        for port in requested...min(requested + 200, 65_535) where canBind(port) { return port }
        return requested
    }

    private static func canBind(_ port: Int) -> Bool {
        let descriptor = socket(AF_INET, SOCK_STREAM, 0)
        guard descriptor >= 0 else { return false }
        defer { close(descriptor) }
        var value: Int32 = 1
        setsockopt(descriptor, SOL_SOCKET, SO_REUSEADDR, &value, socklen_t(MemoryLayout.size(ofValue: value)))
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = in_port_t(port).bigEndian
        address.sin_addr = in_addr(s_addr: inet_addr("127.0.0.1"))
        return withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                bind(descriptor, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) == 0
            }
        }
    }
}
