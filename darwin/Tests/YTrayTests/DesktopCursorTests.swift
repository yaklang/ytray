import AppKit
import QuartzCore
import XCTest
@testable import YTray

final class DesktopCursorTests: XCTestCase {
    func testScreenCoordinatesSupportDisplaysAboveBelowAndLeftOfPrimary() {
        XCTAssertEqual(DesktopCursorGeometry.appKitPoint(CGPoint(x: 120, y: 80), primaryDisplayHeight: 1440), CGPoint(x: 120, y: 1360))
        XCTAssertEqual(DesktopCursorGeometry.appKitPoint(CGPoint(x: -900, y: -300), primaryDisplayHeight: 1440), CGPoint(x: -900, y: 1740))
        XCTAssertEqual(DesktopCursorGeometry.appKitPoint(CGPoint(x: 400, y: 1800), primaryDisplayHeight: 1440), CGPoint(x: 400, y: -360))
        let window = CGRect(x: 100, y: 200, width: 600, height: 400)
        XCTAssertEqual(DesktopCursorGeometry.visibleCenter(of: CGRect(x: 110, y: 500, width: 200, height: 900), in: window), CGPoint(x: 210, y: 550))
        XCTAssertNil(DesktopCursorGeometry.visibleCenter(of: CGRect(x: 800, y: 200, width: 20, height: 20), in: window))
        XCTAssertNil(DesktopCursorGeometry.visibleCenter(of: .zero, in: window))
    }

    @MainActor
    func testOverlayNeverTakesFocusAndStopCancelsPendingMovementAndFade() async throws {
        _ = NSApplication.shared
        let frontmost = NSWorkspace.shared.frontmostApplication?.processIdentifier
        let keyWindow = NSApp.keyWindow
        let indicator = DesktopActionIndicator()
        indicator.show(.init(point: CGPoint(x: -10000, y: -10000), kind: .click))
        let window = try XCTUnwrap(NSApp.windows.first { $0.windowNumber == indicator.windowID.map(Int.init) })
        XCTAssertTrue(indicator.isVisible)
        XCTAssertTrue(window.ignoresMouseEvents)
        XCTAssertFalse(window.canBecomeKey)
        XCTAssertFalse(window.canBecomeMain)
        indicator.finish()
        indicator.show(.init(point: CGPoint(x: -9000, y: -9000), kind: .scroll))
        indicator.hide()
        try await Task.sleep(nanoseconds: 2_100_000_000)
        XCTAssertFalse(indicator.isVisible)
        XCTAssertTrue(NSApp.keyWindow === keyWindow)
        XCTAssertEqual(NSWorkspace.shared.frontmostApplication?.processIdentifier, frontmost)
    }

    @MainActor
    func testCursorArtworkHasTransparentBackgroundAndHollowCenter() throws {
        let view = DesktopCursorView(frame: CGRect(origin: .zero, size: DesktopCursorGeometry.size))
        let context = try XCTUnwrap(CGContext(data: nil, width: 224, height: 224, bitsPerComponent: 8, bytesPerRow: 224 * 4,
            space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        context.scaleBy(x: 2, y: 2)
        view.layer?.render(in: context)
        let image = try XCTUnwrap(context.makeImage())
        let pixels = try XCTUnwrap(context.data).assumingMemoryBound(to: UInt8.self)
        XCTAssertEqual(pixels[3], 0, "The overlay background must remain transparent")
        var redPixels = 0
        for i in stride(from: 0, to: 224 * 224 * 4, by: 4) where pixels[i + 3] > 100 {
            if Int(pixels[i]) > Int(pixels[i + 1]) * 3 / 2 && pixels[i + 1] > pixels[i + 2] { redPixels += 1 }
        }
        XCTAssertGreaterThan(redPixels, 50)
        let center = (112 * 224 + 96) * 4
        XCTAssertLessThan(pixels[center + 3], 20, "The arrow must have a hollow center")
        if let path = ProcessInfo.processInfo.environment["YTRAY_CURSOR_PREVIEW_DIR"] {
            let directory = URL(fileURLWithPath: path)
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            let png = try XCTUnwrap(NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:]))
            try png.write(to: directory.appendingPathComponent("cursor-artwork.png"))
            let preview = try XCTUnwrap(CGContext(data: nil, width: 224, height: 224, bitsPerComponent: 8, bytesPerRow: 224 * 4,
                space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
            preview.setFillColor(NSColor(srgbRed: 0.12, green: 0.13, blue: 0.16, alpha: 1).cgColor)
            preview.fill(CGRect(x: 0, y: 0, width: 224, height: 224))
            preview.draw(image, in: CGRect(x: 0, y: 0, width: 224, height: 224))
            let composed = try XCTUnwrap(preview.makeImage())
            let previewPNG = try XCTUnwrap(NSBitmapImageRep(cgImage: composed).representation(using: .png, properties: [:]))
            try previewPNG.write(to: directory.appendingPathComponent("cursor-artwork-dark.png"))
        }
    }
}
