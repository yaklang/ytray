import AppKit
import Darwin
import SwiftUI

@main
enum YTrayMain {
    @MainActor
    static func main() {
        if let index = CommandLine.arguments.firstIndex(of: "--browser-process") {
            runBrowserProcess(argumentIndex: index)
            return
        }

        let application = NSApplication.shared
        if let index = CommandLine.arguments.firstIndex(of: "--render-widget") {
            let output = index + 1 < CommandLine.arguments.count
                ? CommandLine.arguments[index + 1] : "/tmp/ytray-widget.png"
            renderWidget(application: application, output: output)
            return
        }
        if let index = CommandLine.arguments.firstIndex(of: "--render-wizard") {
            let output = index + 1 < CommandLine.arguments.count
                ? CommandLine.arguments[index + 1] : "/tmp/ytray-wizard.png"
            renderWizard(application: application, output: output)
            return
        }
        if let index = CommandLine.arguments.firstIndex(of: "--render-manager") {
            let output = index + 1 < CommandLine.arguments.count
                ? CommandLine.arguments[index + 1] : "/tmp/ytray-manager.png"
            let section = index + 2 < CommandLine.arguments.count
                ? CommandLine.arguments[index + 2] : "quick"
            renderManager(application: application, output: output, section: section)
            return
        }
        if let index = CommandLine.arguments.firstIndex(of: "--render-dock-icon") {
            let output = index + 1 < CommandLine.arguments.count
                ? CommandLine.arguments[index + 1] : "/tmp/ytray-icon.png"
            let source = index + 2 < CommandLine.arguments.count
                ? CommandLine.arguments[index + 2] : "/Applications/Google Chrome.app"
            let badge = index + 3 < CommandLine.arguments.count
                ? CommandLine.arguments[index + 3] : "A"
            renderDockIcon(application: application, output: output, sourceApp: source, badge: badge)
            return
        }
        if let index = CommandLine.arguments.firstIndex(of: "--render-tray-icon") {
            let output = index + 1 < CommandLine.arguments.count
                ? CommandLine.arguments[index + 1] : "/tmp/ytray-tray-icon.png"
            renderTrayIcon(application: application, output: output)
            return
        }
        if let index = CommandLine.arguments.firstIndex(of: "--render-edge-dock") {
            let output = index + 1 < CommandLine.arguments.count
                ? CommandLine.arguments[index + 1] : "/tmp/ytray-edge-dock.png"
            do {
                try YTrayEdgeDockController.renderPreview(to: URL(fileURLWithPath: output))
            } catch {
                fputs("edge dock render failed: \(error)\n", stderr)
                exit(1)
            }
            return
        }
        if let index = CommandLine.arguments.firstIndex(of: "--smoke-browser"),
           index + 1 < CommandLine.arguments.count {
            smokeBrowser(application: application, executablePath: CommandLine.arguments[index + 1])
            return
        }
        if let index = CommandLine.arguments.firstIndex(of: "--smoke-launch-state"),
           index + 1 < CommandLine.arguments.count {
            smokeLaunchState(application: application, executablePath: CommandLine.arguments[index + 1])
            return
        }
        let controller = AppController()
        application.delegate = controller
        application.setActivationPolicy(.accessory)
        application.run()
    }

    @MainActor
    private static func runBrowserProcess(argumentIndex index: Int) {
        guard index + 2 < CommandLine.arguments.count else {
            fputs("browser process bootstrap requires <icon> <browser> [arguments...]\n", stderr)
            exit(2)
        }
        let iconPath = CommandLine.arguments[index + 1]
        let browserPath = CommandLine.arguments[index + 2]
        let browserArguments = Array(CommandLine.arguments.dropFirst(index + 3))
        guard FileManager.default.isExecutableFile(atPath: browserPath),
              let icon = NSImage(contentsOfFile: iconPath) else {
            fputs("browser process bootstrap received an invalid browser or icon\n", stderr)
            exit(2)
        }

        let application = NSApplication.shared
        application.setActivationPolicy(.regular)
        application.applicationIconImage = icon
        application.finishLaunching()

        DispatchQueue.main.asyncAfter(deadline: .now() + 1) {
            let arguments = [browserPath] + browserArguments
            var pointers = arguments.map { strdup($0) }
            pointers.append(nil)
            execv(browserPath, &pointers)
            perror("execv")
            pointers.compactMap { $0 }.forEach { free($0) }
            exit(3)
        }
        application.run()
    }

    @MainActor
    private static func renderDockIcon(application: NSApplication, output: String,
                                       sourceApp: String, badge: String) {
        application.setActivationPolicy(.prohibited)
        do {
            let normalized = try DockBadgeLabel.normalize(badge)
            let base = NSWorkspace.shared.icon(forFile: sourceApp)
            let icon = BrowserProcessIcon.renderIcon(baseIcon: base, badge: normalized)
            guard let tiff = icon.tiffRepresentation,
                  let bitmap = NSBitmapImageRep(data: tiff),
                  let png = bitmap.representation(using: .png, properties: [:]) else {
                throw YTrayError.launchFailed("无法生成 Dock 图标 PNG")
            }
            try png.write(to: URL(fileURLWithPath: output), options: .atomic)
            print("dock icon rendered: \(output) badge=\(normalized)")
        } catch {
            fputs("dock icon render failed: \(error.localizedDescription)\n", stderr)
        }
    }

    @MainActor
    private static func renderTrayIcon(application: NSApplication, output: String) {
        let pixels = 220
        guard let bitmap = NSBitmapImageRep(
            bitmapDataPlanes: nil, pixelsWide: pixels, pixelsHigh: pixels,
            bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true,
            isPlanar: false, colorSpaceName: .deviceRGB,
            bytesPerRow: 0, bitsPerPixel: 0
        ) else { return }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: bitmap)
        NSColor(calibratedWhite: 0.94, alpha: 1).setFill()
        NSRect(x: 0, y: 0, width: pixels, height: pixels).fill()
        NSGraphicsContext.current?.imageInterpolation = .high
        TrayIconRenderer.makeImage().draw(
            in: NSRect(x: 20, y: 20, width: 180, height: 180),
            from: NSRect(origin: .zero, size: TrayIconRenderer.canvasSize),
            operation: .sourceOver,
            fraction: 1
        )
        NSGraphicsContext.restoreGraphicsState()
        if let data = bitmap.representation(using: .png, properties: [:]) {
            try? data.write(to: URL(fileURLWithPath: output), options: .atomic)
        }
        application.terminate(nil)
    }

    @MainActor
    private static func renderWidget(application: NSApplication, output: String) {
        let scratch = FileManager.default.temporaryDirectory
            .appendingPathComponent("ytray-render-\(UUID().uuidString)", isDirectory: true)
        let store = InstanceStore(applicationDirectory: scratch)
        if let runtime = store.defaultRuntime {
            var samples: [BrowserInstance] = []
            if CommandLine.arguments.contains("--running") {
                for index in 1...2 {
                    let thumbnail = renderSampleThumbnail(
                        in: scratch,
                        name: "running-\(index)",
                        title: index == 1 ? "YTray 使用说明" : "本地调试控制台",
                        accent: index == 1 ? .systemOrange : .systemBlue
                    )
                    samples.append(BrowserInstance(
                        name: "运行任务 \(index)",
                        runtimeID: runtime.id,
                        runtimeName: runtime.displayTitle,
                        runtimeVersion: runtime.versionLabel,
                        runtimeKind: runtime.kind,
                        runtimeSource: runtime.source,
                        mode: .quick,
                        processID: Int32(9526 + index),
                        debugPort: 9221 + index,
                        profilePath: scratch.appendingPathComponent("Profiles/running-\(index)").path,
                        startURL: "chrome://newtab",
                        thumbnailPath: thumbnail?.path,
                        thumbnailUpdatedAt: Date(),
                        lastPageTitle: index == 1 ? "YTray 使用说明" : "本地调试控制台",
                        dockBadge: DockBadgeLabel.defaultLabel(for: index)
                    ))
                }
            }
            if CommandLine.arguments.contains("--history") {
                for index in 1...3 {
                    let title = ["登录页调试", "项目文档", "代理设置"][index - 1]
                    let thumbnail = renderSampleThumbnail(
                        in: scratch,
                        name: "history-\(index)",
                        title: title,
                        accent: [.systemGreen, .systemPurple, .systemTeal][index - 1]
                    )
                    samples.append(BrowserInstance(
                        name: "历史任务 \(index)",
                        runtimeID: runtime.id,
                        runtimeName: runtime.displayTitle,
                        runtimeVersion: runtime.versionLabel,
                        runtimeKind: runtime.kind,
                        runtimeSource: runtime.source,
                        mode: .quick,
                        processID: 0,
                        debugPort: 9222 + index,
                        profilePath: scratch.appendingPathComponent("Profiles/history-\(index)").path,
                        startURL: "chrome://newtab",
                        status: .stopped,
                        thumbnailPath: thumbnail?.path,
                        thumbnailUpdatedAt: Date(),
                        lastPageTitle: title,
                        dockBadge: DockBadgeLabel.defaultLabel(for: index)
                    ))
                }
            }
            store.instances = samples
            store.isProxyAdvancedExpanded = CommandLine.arguments.contains("--proxy-advanced")
        }
        let height = WidgetMetrics.height(
            runningCount: store.runningInstances.count,
            historyCount: store.historyInstances.count,
            proxyAdvancedExpanded: store.isProxyAdvancedExpanded
        )
        let hosting = NSHostingView(rootView: WidgetView(
            store: store,
            presentation: WidgetPresentationState(),
            launchAtLogin: LaunchAtLoginManager(
                backend: PreviewLaunchAtLoginBackend(),
                packagedApplication: true
            ),
            openManager: { _ in },
            closeWidget: {}
        ))
        hosting.frame = NSRect(x: 0, y: 0, width: WidgetMetrics.width, height: height)
        let window = NSWindow(contentRect: hosting.frame, styleMask: [.borderless], backing: .buffered, defer: false)
        window.contentView = hosting
        window.backgroundColor = NSColor.clear
        window.isOpaque = false
        window.orderFrontRegardless()
        application.setActivationPolicy(.prohibited)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
            hosting.layoutSubtreeIfNeeded()
            guard let bitmap = hosting.bitmapImageRepForCachingDisplay(in: hosting.bounds) else {
                fputs("cannot allocate widget bitmap\n", stderr)
                application.terminate(nil)
                return
            }
            hosting.cacheDisplay(in: hosting.bounds, to: bitmap)
            if let data = bitmap.representation(using: .png, properties: [:]) {
                try? data.write(to: URL(fileURLWithPath: output), options: .atomic)
            }
            try? FileManager.default.removeItem(at: scratch)
            application.terminate(nil)
        }
        application.run()
        withExtendedLifetime(window) {}
    }

    @MainActor
    private static func renderWizard(application: NSApplication, output: String) {
        let scratch = FileManager.default.temporaryDirectory
            .appendingPathComponent("ytray-wizard-render-\(UUID().uuidString)", isDirectory: true)
        let store = InstanceStore(applicationDirectory: scratch)
        if store.settings.defaultRuntimeID == nil {
            store.settings.defaultRuntimeID = store.runtimes.first?.id
        }
        let size = NSSize(width: 720, height: 570)
        let hosting = NSHostingView(rootView: CustomLaunchWizard(store: store, isPresented: .constant(true)))
        hosting.frame = NSRect(origin: .zero, size: size)
        hosting.appearance = NSAppearance(named: .darkAqua)
        let window = NSWindow(contentRect: hosting.frame, styleMask: [.borderless], backing: .buffered, defer: false)
        window.contentView = hosting
        window.appearance = NSAppearance(named: .darkAqua)
        window.backgroundColor = .windowBackgroundColor
        window.isOpaque = true
        window.orderFrontRegardless()
        application.setActivationPolicy(.prohibited)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
            hosting.layoutSubtreeIfNeeded()
            guard let bitmap = hosting.bitmapImageRepForCachingDisplay(in: hosting.bounds) else {
                fputs("cannot allocate wizard bitmap\n", stderr)
                application.terminate(nil)
                return
            }
            hosting.cacheDisplay(in: hosting.bounds, to: bitmap)
            if let data = bitmap.representation(using: .png, properties: [:]) {
                try? data.write(to: URL(fileURLWithPath: output), options: .atomic)
            }
            try? FileManager.default.removeItem(at: scratch)
            application.terminate(nil)
        }
        application.run()
        withExtendedLifetime(window) {}
    }

    @MainActor
    private static func renderManager(application: NSApplication, output: String, section: String) {
        let scratch = FileManager.default.temporaryDirectory
            .appendingPathComponent("ytray-manager-render-\(UUID().uuidString)", isDirectory: true)
        let store = InstanceStore(applicationDirectory: scratch)
        let navigation = ManagerNavigation()
        navigation.selection = ManagerSection.allCases.first(where: {
            $0.rawValue == section || String(describing: $0) == section
        }) ?? .quick
        let launchAtLogin = LaunchAtLoginManager(
            backend: PreviewLaunchAtLoginBackend(),
            packagedApplication: true
        )
        let size = NSSize(width: 1080, height: 720)
        let hosting = NSHostingView(rootView: ManagerView(
            store: store,
            navigation: navigation,
            launchAtLogin: launchAtLogin
        ))
        hosting.frame = NSRect(origin: .zero, size: size)
        hosting.appearance = NSAppearance(named: .darkAqua)
        let window = NSWindow(
            contentRect: hosting.frame,
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )
        window.title = "YTray"
        window.contentView = hosting
        window.appearance = NSAppearance(named: .darkAqua)
        window.orderFrontRegardless()
        application.setActivationPolicy(.prohibited)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.6) {
            hosting.layoutSubtreeIfNeeded()
            guard let bitmap = hosting.bitmapImageRepForCachingDisplay(in: hosting.bounds) else {
                fputs("cannot allocate manager bitmap\n", stderr)
                application.terminate(nil)
                return
            }
            hosting.cacheDisplay(in: hosting.bounds, to: bitmap)
            if let data = bitmap.representation(using: .png, properties: [:]) {
                try? data.write(to: URL(fileURLWithPath: output), options: .atomic)
            }
            try? FileManager.default.removeItem(at: scratch)
            application.terminate(nil)
        }
        application.run()
        withExtendedLifetime(window) {}
    }

    private static func renderSampleThumbnail(
        in directory: URL,
        name: String,
        title: String,
        accent: NSColor
    ) -> URL? {
        let size = NSSize(width: 640, height: 360)
        let image = NSImage(size: size)
        image.lockFocus()
        NSColor(calibratedWhite: 0.10, alpha: 1).setFill()
        NSRect(origin: .zero, size: size).fill()
        NSColor(calibratedWhite: 0.17, alpha: 1).setFill()
        NSRect(x: 0, y: 312, width: 640, height: 48).fill()
        accent.setFill()
        NSBezierPath(roundedRect: NSRect(x: 28, y: 54, width: 584, height: 218), xRadius: 18, yRadius: 18).fill()
        NSColor.white.withAlphaComponent(0.92).setFill()
        NSBezierPath(roundedRect: NSRect(x: 52, y: 82, width: 536, height: 160), xRadius: 12, yRadius: 12).fill()
        NSString(string: title).draw(
            at: NSPoint(x: 52, y: 322),
            withAttributes: [
                .font: NSFont.systemFont(ofSize: 17, weight: .semibold),
                .foregroundColor: NSColor.white,
            ]
        )
        NSString(string: "页面内容自动缩略图").draw(
            at: NSPoint(x: 178, y: 148),
            withAttributes: [
                .font: NSFont.systemFont(ofSize: 22, weight: .bold),
                .foregroundColor: NSColor.darkGray,
            ]
        )
        image.unlockFocus()
        guard let tiff = image.tiffRepresentation,
              let bitmap = NSBitmapImageRep(data: tiff),
              let data = bitmap.representation(using: .jpeg, properties: [.compressionFactor: 0.72]) else {
            return nil
        }
        let output = directory.appendingPathComponent("\(name).jpg")
        do {
            try data.write(to: output, options: .atomic)
            return output
        } catch {
            return nil
        }
    }

    @MainActor
    private static func smokeBrowser(application: NSApplication, executablePath: String) {
        let scratch = FileManager.default.temporaryDirectory
            .appendingPathComponent("ytray-browser-smoke-\(UUID().uuidString)", isDirectory: true)
        let runtime = BrowserRuntime(name: "Smoke Chrome", version: "local",
                                     architecture: RuntimeInstaller.platform,
                                     executablePath: executablePath, source: .local)
        var settings = LaunchSettings()
        settings.homeURL = "data:text/html,<title>YTray Smoke</title><h1>YTray</h1>"
        settings.debugPort = 17_777
        application.setActivationPolicy(.prohibited)
        Task { @MainActor in
            do {
                let launched = try BrowserLauncher.launch(
                    runtime: runtime,
                    mode: .isolated,
                    settings: settings,
                    plugins: [],
                    applicationDirectory: scratch,
                    ordinal: 1,
                    dockBadge: "A"
                )
                do {
                    var runningApp: NSRunningApplication?
                    for _ in 0..<50 {
                        if let app = NSRunningApplication(processIdentifier: launched.instance.processID),
                           app.bundleURL?.pathExtension.lowercased() == "app",
                           app.bundleIdentifier?.hasPrefix("com.yaklang.YTray") != true {
                            runningApp = app
                            break
                        }
                        try? await Task.sleep(nanoseconds: 100_000_000)
                    }
                    guard let runningApp else {
                        throw YTrayError.launchFailed("同 PID 没有切换为原始浏览器应用身份")
                    }
                    let title = await ScreenshotService.currentPageTitle(
                        debugPort: launched.instance.debugPort,
                        attempts: 30
                    )
                    let image = try await ScreenshotService.capture(
                        debugPort: launched.instance.debugPort,
                        instanceID: launched.instance.id,
                        outputDirectory: scratch.appendingPathComponent("Screenshots", isDirectory: true)
                    )
                    let attributes = try? FileManager.default.attributesOfItem(atPath: image.path)
                    let size = (attributes?[.size] as? NSNumber)?.intValue ?? 0
                    let thumbnail = try await ScreenshotService.captureThumbnail(
                        debugPort: launched.instance.debugPort,
                        instanceID: launched.instance.id,
                        outputURL: InstanceThumbnailStorage.thumbnailURL(
                            for: launched.instance.id,
                            applicationDirectory: scratch
                        )
                    )
                    let thumbnailAttributes = try? FileManager.default.attributesOfItem(atPath: thumbnail.path)
                    let thumbnailSize = (thumbnailAttributes?[.size] as? NSNumber)?.intValue ?? 0
                    guard title == "YTray Smoke", size > 0, thumbnailSize > 0 else {
                        throw YTrayError.launchFailed("浏览器调试或截图验证未通过")
                    }
                    print("browser smoke passed: pid=\(launched.instance.processID) port=\(launched.instance.debugPort) badge=A bundle=\(runningApp.bundleIdentifier ?? "missing") name=\(runningApp.localizedName ?? "missing") title=\(title ?? "missing") screenshot=\(size) bytes thumbnail=\(thumbnailSize) bytes")
                } catch {
                    fputs("browser smoke failed: \(error.localizedDescription)\n", stderr)
                    let log = scratch.appendingPathComponent("Logs/\(launched.instance.id.uuidString).log")
                    if let data = try? Data(contentsOf: log), let text = String(data: data, encoding: .utf8) {
                        fputs(text, stderr)
                    }
                }
                launched.process.terminate()
                for _ in 0..<30 {
                    guard Darwin.kill(launched.instance.processID, 0) == 0 else { break }
                    try? await Task.sleep(nanoseconds: 100_000_000)
                }
                try? FileManager.default.removeItem(at: scratch)
                application.terminate(nil)
            } catch {
                fputs("browser smoke failed: \(error.localizedDescription)\n", stderr)
                try? FileManager.default.removeItem(at: scratch)
                application.terminate(nil)
            }
        }
        application.run()
    }

    @MainActor
    private static func smokeLaunchState(application: NSApplication, executablePath: String) {
        let scratch = FileManager.default.temporaryDirectory
            .appendingPathComponent("ytray-launch-state-smoke-\(UUID().uuidString)", isDirectory: true)
        let runtime = BrowserRuntime(name: "Smoke Chrome", version: "local",
                                     architecture: RuntimeInstaller.platform,
                                     executablePath: executablePath, source: .local)
        let store = InstanceStore(applicationDirectory: scratch, discoverSystemBrowsers: false)
        store.runtimes = [runtime]
        store.settings.defaultRuntimeID = runtime.id
        store.settings.homeURL = "data:text/html,<title>Launch State Ready</title><h1>Ready</h1>"
        store.settings.debugPort = 17_781
        application.setActivationPolicy(.prohibited)

        store.launch(mode: .quick)
        Task { @MainActor in
            guard store.isLaunching, store.launchPhase == .waiting else {
                fputs("launch state smoke failed: loading state was not visible immediately\n", stderr)
                await cleanupLaunchStateSmoke(store: store, scratch: scratch, application: application)
                return
            }
            var initialLaunchReady = false
            for _ in 0..<180 {
                if store.launchPhase == .succeeded {
                    initialLaunchReady = true
                    break
                }
                if store.errorMessage != nil { break }
                try? await Task.sleep(nanoseconds: 100_000_000)
            }
            guard initialLaunchReady, let original = store.runningInstances.first else {
                fputs("launch state smoke failed: \(store.errorMessage ?? "browser did not become ready")\n", stderr)
                await cleanupLaunchStateSmoke(store: store, scratch: scratch, application: application)
                return
            }

            var capturedThumbnail: String?
            for _ in 0..<80 {
                capturedThumbnail = store.runningInstances.first(where: { $0.id == original.id })?.thumbnailPath
                if let capturedThumbnail,
                   FileManager.default.fileExists(atPath: capturedThumbnail) { break }
                try? await Task.sleep(nanoseconds: 100_000_000)
            }
            guard let capturedThumbnail,
                  FileManager.default.fileExists(atPath: capturedThumbnail) else {
                fputs("launch state smoke failed: automatic thumbnail was not captured\n", stderr)
                await cleanupLaunchStateSmoke(store: store, scratch: scratch, application: application)
                return
            }

            let originalIdentity = (original.id, original.name, original.profilePath, original.dockBadge)
            store.stop(original)
            var history: BrowserInstance?
            for _ in 0..<60 {
                history = store.historyInstances.first(where: { $0.id == original.id })
                if history != nil { break }
                try? await Task.sleep(nanoseconds: 100_000_000)
            }
            guard let history else {
                fputs("launch state smoke failed: stopped instance did not enter history\n", stderr)
                await cleanupLaunchStateSmoke(store: store, scratch: scratch, application: application)
                return
            }
            guard history.thumbnailPath == capturedThumbnail,
                  FileManager.default.fileExists(atPath: capturedThumbnail) else {
                fputs("launch state smoke failed: history did not retain its thumbnail\n", stderr)
                await cleanupLaunchStateSmoke(store: store, scratch: scratch, application: application)
                return
            }

            store.restoreHistory(history)
            guard store.isLaunching, store.restoringInstanceID == history.id else {
                fputs("launch state smoke failed: restore did not enter loading state\n", stderr)
                await cleanupLaunchStateSmoke(store: store, scratch: scratch, application: application)
                return
            }
            var restoredReady = false
            for _ in 0..<180 {
                if store.launchPhase == .succeeded {
                    restoredReady = true
                    break
                }
                if store.errorMessage != nil { break }
                try? await Task.sleep(nanoseconds: 100_000_000)
            }
            guard restoredReady, let restored = store.runningInstances.first,
                  restored.id == originalIdentity.0,
                  restored.name == originalIdentity.1,
                  restored.profilePath == originalIdentity.2,
                  restored.dockBadge == originalIdentity.3 else {
                fputs("launch state smoke failed: restored instance did not inherit identity/profile/badge\n", stderr)
                await cleanupLaunchStateSmoke(store: store, scratch: scratch, application: application)
                return
            }
            let restoredTitle = await ScreenshotService.currentPageTitle(
                debugPort: restored.debugPort,
                attempts: 30
            )
            guard restoredTitle == "Launch State Ready" else {
                fputs("launch state smoke failed: last tab was not restored (title=\(restoredTitle ?? "missing"), savedURL=\(restored.lastPageURL ?? "missing"))\n", stderr)
                await cleanupLaunchStateSmoke(store: store, scratch: scratch, application: application)
                return
            }
            print("launch+restore state smoke passed: loading -> succeeded -> history -> restored id=\(restored.id) badge=\(restored.dockBadge ?? "missing") title=\(restoredTitle ?? "missing")")
            await cleanupLaunchStateSmoke(store: store, scratch: scratch, application: application)
        }
        application.run()
    }

    @MainActor
    private static func cleanupLaunchStateSmoke(
        store: InstanceStore,
        scratch: URL,
        application: NSApplication
    ) async {
        let running = store.runningInstances
        let processIDs = running.map(\.processID).filter { $0 > 0 }
        for instance in running { store.stop(instance) }

        for _ in 0..<50 {
            if processIDs.allSatisfy({ Darwin.kill($0, 0) != 0 }) { break }
            try? await Task.sleep(nanoseconds: 100_000_000)
        }
        for processID in processIDs where Darwin.kill(processID, 0) == 0 {
            Darwin.kill(processID, SIGTERM)
        }
        for _ in 0..<20 {
            if processIDs.allSatisfy({ Darwin.kill($0, 0) != 0 }) { break }
            try? await Task.sleep(nanoseconds: 100_000_000)
        }
        for processID in processIDs where Darwin.kill(processID, 0) == 0 {
            Darwin.kill(processID, SIGKILL)
        }
        for _ in 0..<20 {
            if processIDs.allSatisfy({ Darwin.kill($0, 0) != 0 }) { break }
            try? await Task.sleep(nanoseconds: 100_000_000)
        }

        try? FileManager.default.removeItem(at: scratch)
        application.terminate(nil)
    }
}
