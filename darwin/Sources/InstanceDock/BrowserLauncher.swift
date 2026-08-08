import Foundation
import Darwin

enum BrowserLauncher {
    struct LaunchResult {
        let process: Process
        let instance: BrowserInstance
    }

    static func buildProcessArguments(iconURL: URL, browserExecutable: URL,
                                      browserArguments: [String]) -> [String] {
        ["--browser-process", iconURL.path, browserExecutable.path] + browserArguments
    }

    static func buildArguments(mode: LaunchMode, settings: LaunchSettings, profilePath: String,
                               debugPort: Int, plugins: [BrowserPlugin],
                               restoreLastSession: Bool = false) throws -> [String] {
        var arguments = [
            "--user-data-dir=\(profilePath)",
            "--remote-debugging-address=127.0.0.1",
            "--remote-debugging-port=\(debugPort)",
            "--remote-allow-origins=http://127.0.0.1:\(debugPort)",
            "--no-first-run", "--no-default-browser-check",
        ]
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
            if !proxy.isEmpty { arguments.append("--proxy-server=\(proxy)") }
            if settings.ignoreCertificateErrors { arguments.append("--ignore-certificate-errors") }
            let paths = plugins.filter(\.enabled).map(\.path)
            if !paths.isEmpty {
                let joined = paths.joined(separator: ",")
                arguments += ["--disable-extensions-except=\(joined)", "--load-extension=\(joined)"]
            }
            for line in settings.additionalFlags.components(separatedBy: .newlines) {
                let flag = line.trimmingCharacters(in: .whitespacesAndNewlines)
                guard !flag.isEmpty else { continue }
                guard flag.hasPrefix("--") else { throw InstanceDockError.invalidFlag(flag) }
                if LaunchSettings.blockedCustomPrefixes.contains(where: { flag.hasPrefix($0) }) {
                    throw InstanceDockError.invalidFlag(flag)
                }
                arguments.append(flag)
            }
        }
        if restoreLastSession {
            arguments.append("--restore-last-session")
            return arguments
        }
        let target = settings.homeURL.trimmingCharacters(in: .whitespacesAndNewlines)
        guard target.hasPrefix("chrome://") || URL(string: target)?.scheme != nil else {
            throw InstanceDockError.invalidURL(target)
        }
        arguments.append(target)
        return arguments
    }

    static func launch(runtime: BrowserRuntime, mode: LaunchMode, settings: LaunchSettings,
                       plugins: [BrowserPlugin], applicationDirectory: URL, ordinal: Int,
                       dockBadge: String, restoring history: BrowserInstance? = nil) throws -> LaunchResult {
        let executable = URL(fileURLWithPath: runtime.executablePath)
        guard FileManager.default.isExecutableFile(atPath: executable.path) else {
            throw InstanceDockError.invalidExecutable(executable.path)
        }
        let id = history?.id ?? UUID()
        let normalizedBadge = try DockBadgeLabel.normalize(dockBadge)
        let profile = history.map { URL(fileURLWithPath: $0.profilePath, isDirectory: true) }
            ?? applicationDirectory.appendingPathComponent("Profiles/\(id.uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: profile, withIntermediateDirectories: true)
        let port = nextAvailablePort(startingAt: max(1024, settings.debugPort))
        let arguments = try buildArguments(mode: mode, settings: settings, profilePath: profile.path,
                                           debugPort: port, plugins: plugins,
                                           restoreLastSession: history != nil)
        let logs = applicationDirectory.appendingPathComponent("Logs", isDirectory: true)
        try FileManager.default.createDirectory(at: logs, withIntermediateDirectories: true)
        let logURL = logs.appendingPathComponent("\(id.uuidString).log")
        FileManager.default.createFile(atPath: logURL.path, contents: nil)
        let log = try FileHandle(forWritingTo: logURL)
        let iconURL: URL
        do {
            iconURL = try BrowserProcessIcon.write(
                sourceExecutable: executable,
                badge: normalizedBadge,
                instanceID: id,
                applicationDirectory: applicationDirectory
            )
        } catch {
            try? log.close()
            throw error
        }
        let process = Process()
        guard let launcher = Bundle.main.executableURL else {
            try? log.close()
            BrowserProcessIcon.remove(instanceID: id, applicationDirectory: applicationDirectory)
            throw InstanceDockError.launchFailed("找不到 Instance Dock 启动器")
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
            throw InstanceDockError.launchFailed(error.localizedDescription)
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
            lastPageTitle: history?.lastPageTitle,
            lastPageURL: history?.lastPageURL,
            dockBadge: normalizedBadge,
            settingsSnapshot: settings,
            pluginIDs: plugins.map(\.id)
        )
        return LaunchResult(process: process, instance: instance)
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
