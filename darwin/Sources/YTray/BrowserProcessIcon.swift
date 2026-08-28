import AppKit
import Foundation

enum DockBadgeLabel {
    static func defaultLabel(for ordinal: Int) -> String {
        var value = max(1, ordinal)
        var result = ""
        repeat {
            value -= 1
            result.insert(Character(UnicodeScalar(65 + value % 26)!), at: result.startIndex)
            value /= 26
        } while value > 0
        return result
    }

    static func normalize(_ value: String) throws -> String {
        let normalized = value.trimmingCharacters(in: .whitespacesAndNewlines).uppercased()
        guard (1...2).contains(normalized.count),
              normalized.unicodeScalars.allSatisfy({ (65...90).contains(Int($0.value)) }) else {
            throw YTrayError.launchFailed("Dock 角标只能是 1–2 个英文字母")
        }
        return normalized
    }
}
enum BrowserProcessIcon {
    static func iconURL(instanceID: UUID, applicationDirectory: URL) -> URL {
        applicationDirectory
            .appendingPathComponent("ProcessIcons", isDirectory: true)
            .appendingPathComponent("\(instanceID.uuidString).png")
    }

    static func write(sourceExecutable: URL, badge: String, instanceID: UUID,
                      applicationDirectory: URL) throws -> URL {
        let label = try DockBadgeLabel.normalize(badge)
        let directory = applicationDirectory.appendingPathComponent("ProcessIcons", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let source = enclosingApplication(for: sourceExecutable) ?? sourceExecutable
        let rendered = renderIcon(baseIcon: NSWorkspace.shared.icon(forFile: source.path), badge: label)
        guard let tiff = rendered.tiffRepresentation,
              let bitmap = NSBitmapImageRep(data: tiff),
              let png = bitmap.representation(using: .png, properties: [:]) else {
            throw YTrayError.launchFailed("无法生成浏览器进程图标")
        }
        let output = iconURL(instanceID: instanceID, applicationDirectory: applicationDirectory)
        try png.write(to: output, options: .atomic)
        return output
    }

    static func remove(instanceID: UUID, applicationDirectory: URL) {
        let output = iconURL(instanceID: instanceID, applicationDirectory: applicationDirectory)
        guard FileManager.default.fileExists(atPath: output.path) else { return }
        try? FileManager.default.removeItem(at: output)
    }

    static func renderIcon(baseIcon: NSImage, badge: String, size: CGFloat = 512) -> NSImage {
        let output = NSImage(size: NSSize(width: size, height: size))
        output.lockFocus()
        NSGraphicsContext.current?.imageInterpolation = .high
        baseIcon.draw(in: NSRect(x: 0, y: 0, width: size, height: size),
                      from: .zero, operation: .sourceOver, fraction: 1)

        let diameter = size * 0.31
        let badgeRect = NSRect(x: size - diameter - size * 0.025,
                               y: size * 0.025,
                               width: diameter,
                               height: diameter)
        NSColor.white.setStroke()
        let border = NSBezierPath(ovalIn: badgeRect.insetBy(dx: -size * 0.012, dy: -size * 0.012))
        border.lineWidth = size * 0.024
        border.stroke()
        let badgeColor = AppEnvironment.instanceColorThemesEnabled
            ? BrowserIdentityColor.color(for: badge).nsColor
            : Brand.orangeNS
        badgeColor.setFill()
        NSBezierPath(ovalIn: badgeRect).fill()

        let fontSize = badge.count == 1 ? diameter * 0.59 : diameter * 0.43
        let attributes: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: fontSize, weight: .bold),
            .foregroundColor: NSColor.white,
        ]
        let text = NSAttributedString(string: badge, attributes: attributes)
        let textSize = text.size()
        text.draw(at: NSPoint(x: badgeRect.midX - textSize.width / 2,
                              y: badgeRect.midY - textSize.height / 2 + size * 0.008))
        output.unlockFocus()
        return output
    }

    private static func enclosingApplication(for executable: URL) -> URL? {
        var candidate = executable.deletingLastPathComponent()
        while candidate.path != "/" {
            if candidate.pathExtension.lowercased() == "app" { return candidate }
            candidate.deleteLastPathComponent()
        }
        return nil
    }
}
