import AppKit
import CoreGraphics
import QuartzCore

struct DesktopActionCue: Sendable {
    enum Kind: String, Sendable { case click, type, key, scroll }
    let point: CGPoint // Quartz screen points: primary display top-left, y increases down.
    let kind: Kind
}

enum DesktopCursorGeometry {
    static let size = CGSize(width: 112, height: 112)
    static let hotspot = CGPoint(x: 44, y: 68)
    static func appKitPoint(_ quartz: CGPoint, primaryDisplayHeight: CGFloat) -> CGPoint {
        CGPoint(x: quartz.x, y: primaryDisplayHeight - quartz.y)
    }
    static func visibleCenter(of element: CGRect, in window: CGRect) -> CGPoint? {
        let visible = element.intersection(window)
        guard !visible.isNull, visible.width > 0, visible.height > 0,
              visible.midX.isFinite, visible.midY.isFinite else { return nil }
        return CGPoint(x: visible.midX, y: visible.midY)
    }
}

/// A visual marker, never a system mouse replacement. It cannot take focus or intercept input.
@MainActor
final class DesktopActionIndicator {
    private let panel: DesktopCursorPanel
    private let artwork: DesktopCursorView
    private var hideTask: Task<Void, Never>?
    private var moveTask: Task<Void, Never>?
    private(set) var revision: UInt64 = 0
    var isVisible: Bool { panel.isVisible }
    var windowID: CGWindowID? { panel.windowNumber > 0 ? CGWindowID(panel.windowNumber) : nil }

    init() {
        let frame = CGRect(origin: .zero, size: DesktopCursorGeometry.size)
        panel = DesktopCursorPanel(contentRect: frame, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        artwork = DesktopCursorView(frame: frame)
        panel.contentView = artwork
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.ignoresMouseEvents = true
        panel.hidesOnDeactivate = false
        panel.isReleasedWhenClosed = false
        panel.level = .screenSaver
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        panel.isExcludedFromWindowsMenu = true
        panel.setAccessibilityElement(false)
        artwork.setAccessibilityElement(false)
        panel.animationBehavior = .none
    }

    func show(_ cue: DesktopActionCue) {
        guard cue.point.x.isFinite, cue.point.y.isFinite else { return }
        hideTask?.cancel(); hideTask = nil
        moveTask?.cancel(); moveTask = nil
        revision &+= 1
        artwork.layer?.removeAnimation(forKey: "fade")
        let primary = NSScreen.screens.first { ($0.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value == CGMainDisplayID() }
        let point = DesktopCursorGeometry.appKitPoint(cue.point, primaryDisplayHeight: primary?.frame.maxY ?? NSScreen.screens.first?.frame.maxY ?? 0)
        let origin = CGPoint(x: point.x - DesktopCursorGeometry.hotspot.x, y: point.y - DesktopCursorGeometry.hotspot.y)
        let moving = panel.isVisible && !NSWorkspace.shared.accessibilityDisplayShouldReduceMotion
        panel.alphaValue = 1
        artwork.layer?.opacity = 1
        if moving {
            let start = panel.frame.origin
            let current = revision
            moveTask = Task { @MainActor [weak self] in
                for step in 1...12 {
                    do { try await Task.sleep(nanoseconds: 10_000_000) } catch { return }
                    guard let self, self.revision == current else { return }
                    let t = CGFloat(step) / 12
                    let progress = 1 - pow(1 - t, 3)
                    self.panel.setFrameOrigin(CGPoint(x: start.x + (origin.x - start.x) * progress,
                                                      y: start.y + (origin.y - start.y) * progress))
                }
            }
        } else { panel.setFrameOrigin(origin) }
        panel.orderFrontRegardless()
        artwork.animate(cue.kind, reduceMotion: NSWorkspace.shared.accessibilityDisplayShouldReduceMotion)
    }

    func finish() {
        guard panel.isVisible else { return }
        let current = revision
        hideTask?.cancel()
        artwork.finish()
        hideTask = Task { @MainActor [weak self] in
            do { try await Task.sleep(nanoseconds: 1_800_000_000) } catch { return }
            guard let self, self.revision == current else { return }
            let fade = CABasicAnimation(keyPath: "opacity")
            fade.fromValue = 1; fade.toValue = 0; fade.duration = 0.2
            fade.fillMode = .forwards; fade.isRemovedOnCompletion = false
            self.artwork.layer?.add(fade, forKey: "fade")
            do { try await Task.sleep(nanoseconds: 220_000_000) } catch { return }
            guard self.revision == current else { return }
            self.panel.orderOut(nil)
        }
    }

    func hide() {
        revision &+= 1
        hideTask?.cancel(); hideTask = nil
        moveTask?.cancel(); moveTask = nil
        panel.contentView?.layer?.removeAllAnimations()
        artwork.finish()
        panel.orderOut(nil)
        panel.alphaValue = 1
    }
}

private final class DesktopCursorPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

/// Hollow arrow, red glow, and an action ripple centered on the arrow's actual hotspot.
@MainActor
final class DesktopCursorView: NSView {
    private let arrow = CAShapeLayer()
    private let glowLayer = CAShapeLayer()
    private let ambientGlow = CALayer()
    private let ripple = CAShapeLayer()
    override var isOpaque: Bool { false }
    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
        layer?.masksToBounds = false
        let tip = DesktopCursorGeometry.hotspot
        let path = CGMutablePath()
        path.move(to: tip)
        for p in [CGPoint(x: 0, y: -24), CGPoint(x: 6.5, y: -18), CGPoint(x: 11, y: -29),
                  CGPoint(x: 16, y: -27), CGPoint(x: 11, y: -17), CGPoint(x: 20, y: -17)] {
            path.addLine(to: CGPoint(x: tip.x + p.x, y: tip.y + p.y))
        }
        path.closeSubpath()
        arrow.frame = bounds
        arrow.path = path
        arrow.fillColor = NSColor.clear.cgColor
        arrow.strokeColor = Brand.controlCursorNS.cgColor
        arrow.lineWidth = 2.2
        arrow.lineJoin = .round
        arrow.lineCap = .round
        // Clip the glow out of the arrow interior: a blurred outline alone can
        // otherwise make a small hollow cursor look like a solid red pointer.
        glowLayer.frame = bounds
        glowLayer.path = path
        glowLayer.fillColor = NSColor.clear.cgColor
        glowLayer.strokeColor = Brand.controlCursorNS.cgColor
        glowLayer.lineWidth = 2.2
        glowLayer.lineJoin = .round
        glowLayer.shadowPath = path.copy(strokingWithWidth: 2.2, lineCap: .round, lineJoin: .round, miterLimit: 10)
        glowLayer.shadowColor = Brand.controlCursorNS.cgColor
        glowLayer.shadowOpacity = 0.9
        glowLayer.shadowRadius = 12
        glowLayer.shadowOffset = .zero
        let outside = CGMutablePath()
        outside.addRect(bounds)
        outside.addPath(path)
        let glowMask = CAShapeLayer()
        glowMask.frame = bounds
        glowMask.path = outside
        glowMask.fillRule = .evenOdd
        glowLayer.mask = glowMask
        // A broad, soft terracotta aura complements the crisp hollow outline.
        // Its radial gradient is fully transparent at the edge and masked out of the arrow.
        ambientGlow.frame = bounds
        let scale: CGFloat = 2
        if let context = CGContext(data: nil, width: Int(bounds.width * scale), height: Int(bounds.height * scale),
                                   bitsPerComponent: 8, bytesPerRow: 0, space: CGColorSpaceCreateDeviceRGB(),
                                   bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue),
           let gradient = CGGradient(colorsSpace: CGColorSpaceCreateDeviceRGB(), colors: [
                Brand.controlCursorNS.withAlphaComponent(0.32).cgColor,
                Brand.controlCursorNS.withAlphaComponent(0.18).cgColor,
                Brand.controlCursorNS.withAlphaComponent(0).cgColor
           ] as CFArray, locations: [0, 0.4, 1]) {
            let center = CGPoint(x: (tip.x + 7) * scale, y: (tip.y - 12) * scale)
            context.drawRadialGradient(gradient, startCenter: center, startRadius: 0,
                                       endCenter: center, endRadius: 50 * scale, options: [])
            ambientGlow.contents = context.makeImage()
            ambientGlow.contentsScale = scale
        }
        let ambientMask = CAShapeLayer()
        ambientMask.frame = bounds
        ambientMask.path = outside
        ambientMask.fillRule = .evenOdd
        ambientGlow.mask = ambientMask
        let radius: CGFloat = 13
        ripple.bounds = CGRect(x: -radius, y: -radius, width: radius * 2, height: radius * 2)
        ripple.position = tip
        ripple.path = CGPath(ellipseIn: ripple.bounds, transform: nil)
        ripple.fillColor = NSColor.clear.cgColor
        ripple.strokeColor = Brand.controlCursorNS.cgColor
        ripple.lineWidth = 1.8
        ripple.opacity = 0
        ripple.shadowColor = Brand.controlCursorNS.cgColor
        ripple.shadowOpacity = 0.8
        ripple.shadowRadius = 8
        ripple.shadowOffset = .zero
        layer?.addSublayer(ripple)
        layer?.addSublayer(ambientGlow)
        layer?.addSublayer(glowLayer)
        layer?.addSublayer(arrow)
    }
    required init?(coder: NSCoder) { nil }
    func animate(_ kind: DesktopActionCue.Kind, reduceMotion: Bool) {
        arrow.removeAllAnimations(); glowLayer.removeAllAnimations(); ripple.removeAllAnimations()
        guard !reduceMotion else { return }
        let glow = CABasicAnimation(keyPath: "shadowOpacity")
        glow.fromValue = 0.45; glow.toValue = 1
        glow.duration = 0.6; glow.autoreverses = true; glow.repeatCount = .infinity
        glowLayer.add(glow, forKey: "working")
        let expand = CABasicAnimation(keyPath: "transform.scale")
        expand.fromValue = 0.55
        expand.toValue = kind == .click ? 2 : kind == .scroll ? 1.65 : 1.35
        let opacity = CAKeyframeAnimation(keyPath: "opacity")
        opacity.values = [0, 0.9, 0]; opacity.keyTimes = [0, 0.15, 1]
        let group = CAAnimationGroup()
        group.animations = [expand, opacity]; group.duration = 0.75
        group.repeatCount = .infinity
        ripple.add(group, forKey: "action")
    }
    func finish() { arrow.removeAllAnimations(); glowLayer.removeAllAnimations(); ripple.removeAllAnimations() }
}
