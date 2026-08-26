import AppKit
import Combine
import SwiftUI

enum Brand {
    static let orange = Color(red: 242 / 255, green: 139 / 255, blue: 68 / 255)
    static let orangeNS = NSColor(calibratedRed: 242 / 255, green: 139 / 255, blue: 68 / 255, alpha: 1)
    static let pale = Color(red: 1, green: 246 / 255, blue: 238 / 255)
}

@MainActor
final class AppController: NSObject, NSApplicationDelegate, NSWindowDelegate {
    private let store = InstanceStore()
    private let launchAtLogin = LaunchAtLoginManager()
    private let appUpdater = AppUpdateManager.shared
    private lazy var edgeDock = YTrayEdgeDockController(store: store) { [weak self] anchor, onLeft in
        self?.showWidgetFromEdge(anchor: anchor, onLeft: onLeft)
    }
    private let widgetPresentation = WidgetPresentationState()
    private var statusItem: NSStatusItem!
    private var widgetPanel: NSPanel?
    private var managerWindow: NSWindow?
    private var smokeWindow: NSWindow?
    private let managerNavigation = ManagerNavigation()
    private var subscriptions: Set<AnyCancellable> = []
    private var hasPresentedWidget = false
    private var widgetOrigin = WidgetOrigin.tray
    private var suppressWidgetDismissalUntil = Date.distantPast
    private let focusSmoke = CommandLine.arguments.contains("--smoke-widget-focus")
    private let transientSmoke = CommandLine.arguments.contains("--smoke-widget-transient")
    private let edgeWidgetSmoke = CommandLine.arguments.contains("--smoke-edge-widget-focus")

    func applicationDidFinishLaunching(_ notification: Notification) {
        EdgeDockPreferences.migrateLegacyIfNeeded()
        configureStatusItem()
        edgeDock.update()
        presentFirstLaunchAtLoginNoticeIfNeeded()
        UserDefaults.standard.removeObject(forKey: "ytray.widget-position.v1")
        if edgeWidgetSmoke {
            runEdgeWidgetSmoke()
        } else if focusSmoke || transientSmoke {
            waitForStableTrayAnchor(focusOnPresentation: true)
        }
        if !edgeWidgetSmoke && !focusSmoke && !transientSmoke {
            Task { [weak self] in
                try? await Task.sleep(nanoseconds: 2_500_000_000)
                await self?.appUpdater.checkForUpdates()
            }
        }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    private func configureStatusItem() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        statusItem.autosaveName = "ytray.main"
        guard let button = statusItem.button else { return }
        button.image = trayImage()
        button.imagePosition = .imageLeft
        button.title = " \(store.runningInstances.count)"
        button.target = self
        button.action = #selector(statusClicked)
        button.sendAction(on: [.leftMouseUp, .rightMouseUp])
        button.toolTip = "YTray · 左键打开小组件 / 右键菜单"
        store.$instances.sink { [weak self] _ in
            Task { @MainActor in
                self?.refreshStatusTitle()
                self?.updateWidgetSize()
            }
        }.store(in: &subscriptions)
        store.$runtimes.sink { [weak self] _ in
            Task { @MainActor in self?.updateWidgetSize() }
        }.store(in: &subscriptions)
        store.$isProxyAdvancedExpanded.sink { [weak self] _ in
            Task { @MainActor in self?.updateWidgetSize(animated: false) }
        }.store(in: &subscriptions)
        store.$launchPhase.sink { [weak self] _ in
            Task { @MainActor in self?.refreshStatusTitle() }
        }.store(in: &subscriptions)
        appUpdater.$phase.sink { [weak self] _ in
            Task { @MainActor in self?.refreshStatusTitle() }
        }.store(in: &subscriptions)
    }

    private func refreshStatusTitle() {
        let updateSuffix = appUpdater.isUpdateAvailable ? " ↑" : ""
        statusItem?.button?.title = store.isLaunching ? " …" : " \(store.runningInstances.count)\(updateSuffix)"
        statusItem?.button?.toolTip = appUpdater.isUpdateAvailable
            ? "YTray v\(appUpdater.availableVersion ?? "最新版") 可更新"
            : "YTray · 左键打开小组件 / 右键菜单"
        statusItem?.button?.superview?.layoutSubtreeIfNeeded()
        if widgetPanel?.isVisible == true {
            DispatchQueue.main.async { [weak self] in self?.updateWidgetSize(animated: false) }
        }
    }

    @objc private func statusClicked() {
        if NSApp.currentEvent?.type == .rightMouseUp { showContextMenu() }
        else { toggleWidget() }
    }

    private func showContextMenu() {
        let menu = NSMenu()
        let directItem = menu.addItem(withTitle: store.isLaunching ? "正在启动浏览器…" : "无代理启动",
                                      action: #selector(directLaunch), keyEquivalent: "n")
        directItem.target = self
        directItem.isEnabled = !store.isLaunching
        let proxyItem = menu.addItem(withTitle: "使用 HTTP 代理启动",
                                     action: #selector(proxyLaunch), keyEquivalent: "")
        proxyItem.target = self
        proxyItem.isEnabled = !store.isLaunching
        menu.addItem(withTitle: "显示小组件", action: #selector(showWidgetAction), keyEquivalent: "") .target = self
        menu.addItem(withTitle: "全部管理", action: #selector(showManagerAction), keyEquivalent: ",").target = self
        let updateTitle = appUpdater.isUpdateAvailable
            ? "安装 YTray v\(appUpdater.availableVersion ?? "最新版")…"
            : "检查 YTray 更新…"
        menu.addItem(withTitle: updateTitle, action: #selector(showUpdateAction), keyEquivalent: "").target = self
        menu.addItem(.separator())
        let edgeItem = menu.addItem(
            withTitle: EdgeDockPreferences.isEnabled ? "隐藏边缘小组件" : "显示边缘小组件",
            action: #selector(toggleEdgeDock),
            keyEquivalent: ""
        )
        edgeItem.target = self
        menu.addItem(.separator())
        menu.addItem(withTitle: "退出 YTray", action: #selector(quit), keyEquivalent: "q").target = self
        statusItem.menu = menu
        statusItem.button?.performClick(nil)
        statusItem.menu = nil
    }

    @objc private func directLaunch() { store.launchConfigured(usePresetProxy: false) }
    @objc private func proxyLaunch() { store.launchConfigured(usePresetProxy: true) }
    @objc private func showWidgetAction() { showWidget() }
    @objc private func showManagerAction() { showManager(section: .quick) }
    @objc private func showUpdateAction() {
        showManager(section: .settings)
        if !appUpdater.isUpdateAvailable && !appUpdater.isBusy {
            Task { await appUpdater.checkForUpdates() }
        }
    }
    @objc private func toggleEdgeDock() { edgeDock.toggleEnabled() }
    @objc private func quit() { NSApp.terminate(nil) }

    private func toggleWidget() {
        if widgetPanel?.isVisible == true { hideWidget() } else { showWidget() }
    }

    private func hideWidget() {
        widgetPanel?.orderOut(nil)
        edgeDock.setWidgetPresented(false)
    }

    func showWidget(focus: Bool = true) {
        guard let anchor = trayAnchor() else {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) { [weak self] in
                self?.showWidget(focus: focus)
            }
            return
        }
        showWidget(anchor: anchor, focus: focus)
    }

    private func showWidget(anchor: NSRect, focus: Bool) {
        let panel = preparedWidgetPanel()
        widgetOrigin = .tray
        positionWidget(panel, anchor: anchor)
        presentWidget(panel, focus: focus)
    }

    private func showWidgetFromEdge(anchor: NSRect, onLeft: Bool) {
        let panel = preparedWidgetPanel()
        edgeDock.setWidgetPresented(true)
        widgetOrigin = .edge(anchor: anchor, onLeft: onLeft)
        positionWidgetBesideEdge(panel, anchor: anchor, onLeft: onLeft)
        hasPresentedWidget = true
        suppressWidgetDismissalUntil = Date().addingTimeInterval(0.25)
        panel.orderFrontRegardless()

        // A non-activating edge panel delivers its mouse-up while another app is
        // still active. Defer activation until that event has fully unwound;
        // otherwise AppKit can immediately return focus to the previous app and
        // the transient widget dismisses itself on the same click.
        DispatchQueue.main.async { [weak self, weak panel] in
            guard let self, let panel, panel.isVisible else { return }
            self.focusWidget(panel)
        }
    }

    private func preparedWidgetPanel() -> NSPanel {
        if let widgetPanel { return widgetPanel }
        let panel = WidgetPanel(contentRect: NSRect(x: 0, y: 0, width: WidgetMetrics.width,
                                                    height: currentWidgetHeight),
                                styleMask: [.borderless], backing: .buffered, defer: false)
        panel.isFloatingPanel = true
        panel.level = .floating
        panel.backgroundColor = .clear
        panel.isOpaque = false
        panel.hasShadow = true
        panel.sharingType = .readOnly
        panel.hidesOnDeactivate = false
        panel.isMovableByWindowBackground = false
        panel.isReleasedWhenClosed = false
        panel.animationBehavior = .utilityWindow
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.delegate = self
        panel.contentViewController = NSHostingController(rootView: WidgetView(
            store: store,
            presentation: widgetPresentation,
            launchAtLogin: launchAtLogin,
            openManager: { [weak self] section in self?.showManager(section: section) },
            closeWidget: { [weak self] in self?.hideWidget() }
        ))
        widgetPanel = panel
        return panel
    }

    private func presentWidget(_ panel: NSPanel, focus: Bool) {
        hasPresentedWidget = true
        edgeDock.setWidgetPresented(true)
        if focus {
            focusWidget(panel)
        } else {
            panel.orderFrontRegardless()
        }
    }

    private func focusWidget(_ panel: NSPanel) {
        NSApp.activate(ignoringOtherApps: true)
        panel.makeKeyAndOrderFront(nil)
        panel.makeFirstResponder(panel.contentView)
        DispatchQueue.main.async { [weak panel] in
            guard let panel, panel.isVisible else { return }
            NSApp.activate(ignoringOtherApps: true)
            panel.makeKey()
            panel.makeFirstResponder(panel.contentView)
        }
    }

    private func waitForStableTrayAnchor(
        previous: NSRect? = nil,
        stableSamples: Int = 0,
        focusOnPresentation: Bool = false
    ) {
        guard !hasPresentedWidget else { return }
        guard let anchor = trayAnchor() else {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) { [weak self] in
                self?.waitForStableTrayAnchor(
                    previous: previous,
                    stableSamples: 0,
                    focusOnPresentation: focusOnPresentation
                )
            }
            return
        }
        let isStable = previous.map { WidgetPositioning.nearlyEqual($0, anchor) } ?? false
        let samples = isStable ? stableSamples + 1 : 0
        guard samples >= 2 else {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) { [weak self] in
                self?.waitForStableTrayAnchor(
                    previous: anchor,
                    stableSamples: samples,
                    focusOnPresentation: focusOnPresentation
                )
            }
            return
        }
        showWidget(anchor: anchor, focus: focusOnPresentation)
        if transientSmoke {
            runTransientWidgetSmoke(panel: widgetPanel)
        } else if focusSmoke {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { [weak self] in
                guard let self, let panel = self.widgetPanel else { return }
                let passed = NSApp.isActive && panel.isKeyWindow
                print("widget focus smoke \(passed ? "passed" : "failed"): active=\(NSApp.isActive) key=\(panel.isKeyWindow)")
                NSApp.terminate(nil)
            }
        }
    }

    private func runTransientWidgetSmoke(panel: NSPanel?) {
        guard let panel else { return }
        let probe = NSWindow(
            contentRect: NSRect(x: -2_000, y: -2_000, width: 20, height: 20),
            styleMask: [.titled], backing: .buffered, defer: false
        )
        probe.isReleasedWhenClosed = false
        smokeWindow = probe

        DispatchQueue.main.asyncAfter(deadline: .now() + 0.20) { [weak self, weak panel] in
            guard let self, let panel else { return }
            probe.makeKeyAndOrderFront(nil)
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.20) { [weak self, weak panel] in
                guard let self, let panel else { return }
                let dismissed = !panel.isVisible
                self.widgetPresentation.isPinned = true
                self.showWidget()
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.20) { [weak self, weak panel] in
                    guard let self, let panel else { return }
                    probe.makeKeyAndOrderFront(nil)
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.20) {
                        let pinnedStayedVisible = panel.isVisible
                        let passed = dismissed && pinnedStayedVisible
                        print("widget transient smoke \(passed ? "passed" : "failed"): dismissed=\(dismissed) pinnedVisible=\(pinnedStayedVisible)")
                        self.smokeWindow?.orderOut(nil)
                        NSApp.terminate(nil)
                    }
                }
            }
        }
    }

    private func runEdgeWidgetSmoke() {
        DispatchQueue.main.async { [weak self] in
            guard let self, let anchor = self.edgeDock.openWidgetForSmokeTest() else {
                print("edge widget focus smoke failed: missing edge anchor")
                NSApp.terminate(nil)
                return
            }
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.35) { [weak self] in
                guard let self, let panel = self.widgetPanel else {
                    print("edge widget focus smoke failed: missing widget panel")
                    NSApp.terminate(nil)
                    return
                }
                let besideEdge = EdgeDockPreferences.isOnLeft
                    ? panel.frame.minX >= anchor.maxX
                    : panel.frame.maxX <= anchor.minX
                let passed = panel.isVisible && panel.isKeyWindow && besideEdge
                print("edge widget focus smoke \(passed ? "passed" : "failed"): visible=\(panel.isVisible) key=\(panel.isKeyWindow) besideEdge=\(besideEdge)")
                NSApp.terminate(nil)
            }
        }
    }

    private func trayAnchor() -> NSRect? {
        guard let button = statusItem?.button, let statusWindow = button.window else { return nil }
        button.superview?.layoutSubtreeIfNeeded()
        if let actualFrame = actualWindowFrame(windowNumber: statusWindow.windowNumber) {
            return actualFrame
        }
        let buttonFrameInWindow = button.convert(button.bounds, to: nil)
        return statusWindow.convertToScreen(buttonFrameInWindow)
    }

    private func actualWindowFrame(windowNumber: Int) -> NSRect? {
        guard windowNumber > 0,
              let rows = CGWindowListCopyWindowInfo(
                [.optionIncludingWindow, .excludeDesktopElements],
                CGWindowID(windowNumber)
              ) as? [[String: Any]],
              let row = rows.first,
              let bounds = row[kCGWindowBounds as String] as? [String: Any],
              let x = (bounds["X"] as? NSNumber)?.doubleValue,
              let y = (bounds["Y"] as? NSNumber)?.doubleValue,
              let width = (bounds["Width"] as? NSNumber)?.doubleValue,
              let height = (bounds["Height"] as? NSNumber)?.doubleValue else { return nil }
        let quartzFrame = CGRect(x: x, y: y, width: width, height: height)
        let primaryTop = NSScreen.screens.first(where: { $0.frame.origin == .zero })?.frame.maxY
            ?? NSScreen.main?.frame.maxY
            ?? 0
        return WidgetPositioning.appKitFrame(fromQuartz: quartzFrame, primaryScreenTop: primaryTop)
    }

    private func positionWidget(_ panel: NSPanel, anchor: NSRect) {
        let screen = NSScreen.screens.first(where: { $0.frame.intersects(anchor) })
            ?? statusItem?.button?.window?.screen
            ?? NSScreen.main
            ?? NSScreen.screens.first
        guard let screen else { return }
        let frame = WidgetPositioning.frame(
            size: NSSize(width: WidgetMetrics.width, height: currentWidgetHeight),
            trayAnchor: anchor,
            visibleFrame: screen.visibleFrame
        )
        panel.setFrame(frame, display: true, animate: false)
    }

    private func positionWidgetBesideEdge(_ panel: NSPanel, anchor: NSRect, onLeft: Bool) {
        let screen = NSScreen.screens.first(where: { $0.frame.intersects(anchor) })
            ?? NSScreen.main
            ?? NSScreen.screens.first
        guard let screen else { return }
        let frame = EdgeWidgetPositioning.frame(
            size: NSSize(width: WidgetMetrics.width, height: currentWidgetHeight),
            tabFrame: anchor,
            onLeft: onLeft,
            visibleFrame: screen.visibleFrame
        )
        panel.setFrame(frame, display: true, animate: false)
    }

    func showManager(section: ManagerSection = .quick) {
        managerNavigation.selection = section
        let window: NSWindow
        if let existing = managerWindow { window = existing }
        else {
            window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1080, height: 720),
                              styleMask: [.titled, .closable, .miniaturizable, .resizable],
                              backing: .buffered, defer: false)
            window.title = "YTray"
            window.minSize = NSSize(width: 880, height: 600)
            window.isReleasedWhenClosed = false
            window.sharingType = .readOnly
            window.contentViewController = NSHostingController(rootView: ManagerView(
                store: store,
                navigation: managerNavigation,
                launchAtLogin: launchAtLogin
            ))
            window.delegate = self
            window.center()
            managerWindow = window
        }
        NSApp.setActivationPolicy(.regular)
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    private var currentWidgetHeight: CGFloat {
        WidgetMetrics.height(
            runningCount: store.runningInstances.count,
            historyCount: store.historyInstances.count,
            proxyAdvancedExpanded: store.isProxyAdvancedExpanded
        )
    }

    private func updateWidgetSize(animated: Bool = true) {
        guard let panel = widgetPanel else { return }
        switch widgetOrigin {
        case .tray:
            guard let anchor = trayAnchor() else { return }
            positionWidget(panel, anchor: anchor)
        case .edge(let anchor, let onLeft):
            positionWidgetBesideEdge(panel, anchor: anchor, onLeft: onLeft)
        }
    }

    func windowWillClose(_ notification: Notification) {
        guard let window = notification.object as? NSWindow, window === managerWindow else { return }
        DispatchQueue.main.async { NSApp.setActivationPolicy(.accessory) }
    }

    func windowDidResignKey(_ notification: Notification) {
        guard let panel = notification.object as? NSPanel,
              panel === widgetPanel,
              Date() >= suppressWidgetDismissalUntil,
              WidgetDismissalPolicy.shouldHide(
                isPinned: widgetPresentation.isPinned,
                hasAttachedSheet: panel.attachedSheet != nil,
                isBusy: store.proxyCheckPhase == .checking
              ) else { return }

        // Delay until the status-item action or menu tracking for the same click has completed.
        // The click still reaches the app/window underneath; only this transient panel is removed.
        DispatchQueue.main.async { [weak self, weak panel] in
            guard let self, let panel,
                  panel.isVisible,
                  !panel.isKeyWindow,
                  Date() >= self.suppressWidgetDismissalUntil,
                  WidgetDismissalPolicy.shouldHide(
                    isPinned: self.widgetPresentation.isPinned,
                    hasAttachedSheet: panel.attachedSheet != nil,
                    isBusy: self.store.proxyCheckPhase == .checking
                  ) else { return }
            panel.orderOut(nil)
            self.edgeDock.setWidgetPresented(false)
        }
    }

    private func trayImage() -> NSImage {
        TrayIconRenderer.makeImage()
    }

    private func presentFirstLaunchAtLoginNoticeIfNeeded() {
        guard let result = launchAtLogin.enableOnFirstLaunchIfNeeded() else { return }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.35) { [weak self] in
            guard let self else { return }
            self.showManager(section: .launchAtLogin)
            let alert = NSAlert()
            alert.messageText = "YTray 开机启动"
            switch result {
            case .enabled:
                alert.informativeText = "已默认开启开机启动。以后登录 macOS 时，YTray 会自动进入菜单栏；不会自动打开浏览器。你可以在“开机启动”页面随时管理。"
                alert.alertStyle = .informational
            case .requiresApproval:
                alert.informativeText = "YTray 已请求开机启动。请在“系统设置 → 通用 → 登录项”中允许；也可以稍后在 YTray 的“开机启动”页面处理。"
                alert.alertStyle = .informational
            case .failed(let message):
                alert.informativeText = "暂时无法自动开启（\(message)）。这不影响其他功能；你可以稍后在 YTray 的“开机启动”页面重试。"
                alert.alertStyle = .warning
            }
            alert.addButton(withTitle: "知道了")
            if let window = self.managerWindow {
                alert.beginSheetModal(for: window)
            } else {
                alert.runModal()
            }
        }
    }
}

private enum WidgetOrigin {
    case tray
    case edge(anchor: NSRect, onLeft: Bool)
}

@MainActor
final class WidgetPresentationState: ObservableObject {
    @Published var isPinned = false
}

enum WidgetDismissalPolicy {
    static func shouldHide(
        isPinned: Bool,
        hasAttachedSheet: Bool,
        isBusy: Bool = false
    ) -> Bool {
        !isPinned && !hasAttachedSheet && !isBusy
    }
}

enum TrayIconRenderer {
    static let canvasSize = NSSize(width: 18, height: 18)
    static let contentBounds = NSRect(x: 0.55, y: 1.1, width: 16.9, height: 15.8)

    static func makeImage() -> NSImage {
        let image = NSImage(size: canvasSize, flipped: false) { _ in
            guard let context = NSGraphicsContext.current else { return false }
            context.shouldAntialias = true
            NSColor.black.setFill()

            // A compact template rendering of the canonical icon's front WWW panel
            // and three receding browser cards. Keeping it vector-based preserves the
            // macOS menu-bar tinting and remains legible at the native 18pt size.
            let frontPanel = NSBezierPath()
            frontPanel.move(to: NSPoint(x: 0.55, y: 4.2))
            frontPanel.curve(to: NSPoint(x: 2.05, y: 2.45),
                             controlPoint1: NSPoint(x: 0.55, y: 3.35),
                             controlPoint2: NSPoint(x: 1.05, y: 2.7))
            frontPanel.line(to: NSPoint(x: 7.7, y: 1.1))
            frontPanel.curve(to: NSPoint(x: 9.15, y: 2.45),
                             controlPoint1: NSPoint(x: 8.55, y: 0.9),
                             controlPoint2: NSPoint(x: 9.15, y: 1.55))
            frontPanel.line(to: NSPoint(x: 9.15, y: 15.35))
            frontPanel.curve(to: NSPoint(x: 7.7, y: 16.9),
                             controlPoint1: NSPoint(x: 9.15, y: 16.25),
                             controlPoint2: NSPoint(x: 8.55, y: 17.1))
            frontPanel.line(to: NSPoint(x: 2.05, y: 15.55))
            frontPanel.curve(to: NSPoint(x: 0.55, y: 13.8),
                             controlPoint1: NSPoint(x: 1.05, y: 15.3),
                             controlPoint2: NSPoint(x: 0.55, y: 14.65))
            frontPanel.close()
            frontPanel.fill()

            NSBezierPath(roundedRect: NSRect(x: 10.35, y: 2.55, width: 2.45, height: 12.9),
                         xRadius: 1.2, yRadius: 1.2).fill()
            NSBezierPath(roundedRect: NSRect(x: 13.55, y: 3.85, width: 1.9, height: 10.3),
                         xRadius: 0.95, yRadius: 0.95).fill()
            NSBezierPath(roundedRect: NSRect(x: 16.05, y: 5.05, width: 1.4, height: 7.9),
                         xRadius: 0.7, yRadius: 0.7).fill()

            context.saveGraphicsState()
            context.compositingOperation = .clear
            NSColor.black.setFill()
            for x in [2.1, 3.35, 4.6] {
                NSBezierPath(ovalIn: NSRect(x: x, y: 12.85, width: 0.75, height: 0.75)).fill()
            }
            NSColor.black.setStroke()
            let webGlyph = NSBezierPath()
            webGlyph.lineCapStyle = .round
            webGlyph.lineJoinStyle = .round
            webGlyph.lineWidth = 0.85
            webGlyph.move(to: NSPoint(x: 2.0, y: 8.9))
            webGlyph.line(to: NSPoint(x: 2.8, y: 6.7))
            webGlyph.line(to: NSPoint(x: 3.6, y: 8.9))
            webGlyph.line(to: NSPoint(x: 4.4, y: 6.7))
            webGlyph.line(to: NSPoint(x: 5.2, y: 8.9))
            webGlyph.line(to: NSPoint(x: 6.0, y: 6.7))
            webGlyph.line(to: NSPoint(x: 6.8, y: 8.9))
            webGlyph.line(to: NSPoint(x: 7.6, y: 6.7))
            webGlyph.stroke()
            context.restoreGraphicsState()
            return true
        }
        image.isTemplate = true
        image.accessibilityDescription = "YTray 浏览器实例管理"
        return image
    }
}

enum WidgetPositioning {
    static let margin: CGFloat = 8
    static let trayGap: CGFloat = 6

    static func frame(size: NSSize, trayAnchor: NSRect, visibleFrame: NSRect) -> NSRect {
        let preferredX = trayAnchor.midX - size.width / 2
        let x = min(max(preferredX, visibleFrame.minX + margin),
                    visibleFrame.maxX - size.width - margin)
        let preferredY = trayAnchor.minY - size.height - trayGap
        let y = min(max(preferredY, visibleFrame.minY + margin),
                    visibleFrame.maxY - size.height)
        return NSRect(x: x.rounded(), y: y.rounded(), width: size.width, height: size.height)
    }

    static func nearlyEqual(_ lhs: NSRect, _ rhs: NSRect) -> Bool {
        abs(lhs.minX - rhs.minX) < 0.5
            && abs(lhs.minY - rhs.minY) < 0.5
            && abs(lhs.width - rhs.width) < 0.5
            && abs(lhs.height - rhs.height) < 0.5
    }

    static func appKitFrame(fromQuartz frame: CGRect, primaryScreenTop: CGFloat) -> NSRect {
        NSRect(
            x: frame.minX,
            y: primaryScreenTop - frame.maxY,
            width: frame.width,
            height: frame.height
        )
    }
}

private final class WidgetPanel: NSPanel {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }
}
