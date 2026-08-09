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
    private let widgetPresentation = WidgetPresentationState()
    private var statusItem: NSStatusItem!
    private var widgetPanel: NSPanel?
    private var managerWindow: NSWindow?
    private var smokeWindow: NSWindow?
    private let managerNavigation = ManagerNavigation()
    private var subscriptions: Set<AnyCancellable> = []
    private var hasPresentedWidget = false
    private let focusSmoke = CommandLine.arguments.contains("--smoke-widget-focus")
    private let transientSmoke = CommandLine.arguments.contains("--smoke-widget-transient")

    func applicationDidFinishLaunching(_ notification: Notification) {
        configureStatusItem()
        UserDefaults.standard.removeObject(forKey: "instance-dock.widget-position.v1")
        if focusSmoke || transientSmoke {
            waitForStableTrayAnchor(focusOnPresentation: true)
        }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    private func configureStatusItem() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        statusItem.autosaveName = "instance-dock.main"
        guard let button = statusItem.button else { return }
        button.image = trayImage()
        button.imagePosition = .imageLeft
        button.title = " \(store.runningInstances.count)"
        button.target = self
        button.action = #selector(statusClicked)
        button.sendAction(on: [.leftMouseUp, .rightMouseUp])
        button.toolTip = "Instance Dock · 左键打开小组件 / 右键菜单"
        store.$instances.sink { [weak self] _ in
            Task { @MainActor in
                self?.refreshStatusTitle()
                self?.updateWidgetSize()
            }
        }.store(in: &subscriptions)
        store.$runtimes.sink { [weak self] _ in
            Task { @MainActor in self?.updateWidgetSize() }
        }.store(in: &subscriptions)
        store.$launchPhase.sink { [weak self] _ in
            Task { @MainActor in self?.refreshStatusTitle() }
        }.store(in: &subscriptions)
    }

    private func refreshStatusTitle() {
        statusItem?.button?.title = store.isLaunching ? " …" : " \(store.runningInstances.count)"
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
        menu.addItem(.separator())
        menu.addItem(withTitle: "退出 Instance Dock", action: #selector(quit), keyEquivalent: "q").target = self
        statusItem.menu = menu
        statusItem.button?.performClick(nil)
        statusItem.menu = nil
    }

    @objc private func directLaunch() { store.launchConfigured(usePresetProxy: false) }
    @objc private func proxyLaunch() { store.launchConfigured(usePresetProxy: true) }
    @objc private func showWidgetAction() { showWidget() }
    @objc private func showManagerAction() { showManager(section: .quick) }
    @objc private func quit() { NSApp.terminate(nil) }

    private func toggleWidget() {
        if widgetPanel?.isVisible == true { hideWidget() } else { showWidget() }
    }

    private func hideWidget() {
        widgetPanel?.orderOut(nil)
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
        let panel: NSPanel
        if let existing = widgetPanel { panel = existing }
        else {
            panel = WidgetPanel(contentRect: NSRect(x: 0, y: 0, width: WidgetMetrics.width,
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
                openManager: { [weak self] section in self?.showManager(section: section) },
                closeWidget: { [weak self] in self?.hideWidget() }
            ))
            widgetPanel = panel
        }
        positionWidget(panel, anchor: anchor)
        hasPresentedWidget = true
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

    func showManager(section: ManagerSection = .quick) {
        managerNavigation.selection = section
        let window: NSWindow
        if let existing = managerWindow { window = existing }
        else {
            window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1080, height: 720),
                              styleMask: [.titled, .closable, .miniaturizable, .resizable],
                              backing: .buffered, defer: false)
            window.title = "Instance Dock"
            window.minSize = NSSize(width: 880, height: 600)
            window.isReleasedWhenClosed = false
            window.sharingType = .readOnly
            window.contentViewController = NSHostingController(rootView: ManagerView(
                store: store,
                navigation: managerNavigation
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
            historyCount: store.historyInstances.count
        )
    }

    private func updateWidgetSize(animated: Bool = true) {
        guard let panel = widgetPanel else { return }
        guard let anchor = trayAnchor() else { return }
        positionWidget(panel, anchor: anchor)
    }

    func windowWillClose(_ notification: Notification) {
        guard let window = notification.object as? NSWindow, window === managerWindow else { return }
        DispatchQueue.main.async { NSApp.setActivationPolicy(.accessory) }
    }

    func windowDidResignKey(_ notification: Notification) {
        guard let panel = notification.object as? NSPanel,
              panel === widgetPanel,
              WidgetDismissalPolicy.shouldHide(
                isPinned: widgetPresentation.isPinned,
                hasAttachedSheet: panel.attachedSheet != nil
              ) else { return }

        // Delay until the status-item action or menu tracking for the same click has completed.
        // The click still reaches the app/window underneath; only this transient panel is removed.
        DispatchQueue.main.async { [weak self, weak panel] in
            guard let self, let panel,
                  panel.isVisible,
                  !panel.isKeyWindow,
                  WidgetDismissalPolicy.shouldHide(
                    isPinned: self.widgetPresentation.isPinned,
                    hasAttachedSheet: panel.attachedSheet != nil
                  ) else { return }
            panel.orderOut(nil)
        }
    }

    private func trayImage() -> NSImage {
        TrayIconRenderer.makeImage()
    }
}

@MainActor
final class WidgetPresentationState: ObservableObject {
    @Published var isPinned = false
}

enum WidgetDismissalPolicy {
    static func shouldHide(isPinned: Bool, hasAttachedSheet: Bool) -> Bool {
        !isPinned && !hasAttachedSheet
    }
}

enum TrayIconRenderer {
    static let canvasSize = NSSize(width: 18, height: 18)
    static let contentBounds = NSRect(x: 0.35, y: 0.35, width: 17.3, height: 17.3)

    static func makeImage() -> NSImage {
        let image = NSImage(size: canvasSize, flipped: false) { _ in
            guard let context = NSGraphicsContext.current else { return false }
            context.shouldAntialias = true
            NSColor.black.setStroke()

            let chromeBody = NSRect(x: 0.35, y: 0.35, width: 15.8, height: 15.8)
            NSColor.black.setFill()
            NSBezierPath(ovalIn: chromeBody).fill()

            let center = NSPoint(x: chromeBody.midX, y: chromeBody.midY)
            context.saveGraphicsState()
            context.compositingOperation = .clear
            let separators = NSBezierPath()
            for angle in [CGFloat.pi / 2, CGFloat.pi / 2 + 2 * .pi / 3, CGFloat.pi / 2 + 4 * .pi / 3] {
                separators.move(to: NSPoint(
                    x: center.x + cos(angle) * 3.25,
                    y: center.y + sin(angle) * 3.25
                ))
                separators.line(to: NSPoint(
                    x: center.x + cos(angle) * chromeBody.width / 2,
                    y: center.y + sin(angle) * chromeBody.height / 2
                ))
            }
            separators.lineWidth = 0.9
            separators.stroke()
            NSBezierPath(ovalIn: NSRect(x: center.x - 3.25, y: center.y - 3.25,
                                       width: 6.5, height: 6.5)).fill()
            context.restoreGraphicsState()

            NSColor.black.setFill()
            NSBezierPath(ovalIn: NSRect(x: center.x - 2.15, y: center.y - 2.15,
                                       width: 4.3, height: 4.3)).fill()

            let plusBadge = NSRect(x: 11, y: 11, width: 6.65, height: 6.65)
            context.saveGraphicsState()
            context.compositingOperation = .clear
            NSBezierPath(ovalIn: plusBadge.insetBy(dx: -0.6, dy: -0.6)).fill()
            context.restoreGraphicsState()

            NSColor.black.setFill()
            NSBezierPath(ovalIn: plusBadge).fill()
            context.saveGraphicsState()
            context.compositingOperation = .clear
            let plus = NSBezierPath()
            plus.lineCapStyle = .round
            plus.lineWidth = 1.05
            plus.move(to: NSPoint(x: plusBadge.midX - 1.65, y: plusBadge.midY))
            plus.line(to: NSPoint(x: plusBadge.midX + 1.65, y: plusBadge.midY))
            plus.move(to: NSPoint(x: plusBadge.midX, y: plusBadge.midY - 1.65))
            plus.line(to: NSPoint(x: plusBadge.midX, y: plusBadge.midY + 1.65))
            plus.stroke()
            context.restoreGraphicsState()
            return true
        }
        image.isTemplate = true
        image.accessibilityDescription = "Instance Dock 浏览器实例管理"
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
