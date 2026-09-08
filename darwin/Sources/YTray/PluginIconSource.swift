import AppKit
import Foundation

enum PluginIconSource {
    private struct IconManifest: Decodable {
        let icons: [String: String]?
    }

    private static let cache = NSCache<NSString, NSImage>()

    static func resolveIconPath(pluginDirectory: String) -> String? {
        let root = URL(fileURLWithPath: pluginDirectory, isDirectory: true)
            .standardizedFileURL.resolvingSymlinksInPath()
        guard let data = try? Data(contentsOf: root.appendingPathComponent("manifest.json")),
              let manifest = try? JSONDecoder().decode(IconManifest.self, from: data) else { return nil }

        let rootPrefix = root.path.hasSuffix("/") ? root.path : root.path + "/"
        for relativePath in (manifest.icons ?? [:]).sorted(by: { iconSize($0.key) > iconSize($1.key) }).map(\.value) {
            let candidate = root.appendingPathComponent(relativePath)
                .standardizedFileURL.resolvingSymlinksInPath()
            guard candidate.path.hasPrefix(rootPrefix),
                  FileManager.default.fileExists(atPath: candidate.path) else { continue }
            return candidate.path
        }
        return nil
    }

    static func image(for plugin: BrowserPlugin) -> NSImage? {
        let key = "\(plugin.path)|\(plugin.version)" as NSString
        if let cached = cache.object(forKey: key) { return cached }
        guard let path = resolveIconPath(pluginDirectory: plugin.path),
              let image = NSImage(contentsOfFile: path) else { return nil }
        cache.setObject(image, forKey: key)
        return image
    }

    private static func iconSize(_ value: String) -> Int { Int(value) ?? 0 }
}
