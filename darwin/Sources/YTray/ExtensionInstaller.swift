import Foundation
import CryptoKit

enum ExtensionInstaller {
    static let enterpriseVariant = "chrome-enterprise"
    // Identifies the managed plugin across upgrades; the extension's manifest name
    // ("Yakit Browser Agent") stays stable, so match on it instead of tracking paths.
    static let extensionName = "Yakit Browser Agent"

    static let manifestURL = URL(string: "https://aliyun-oss.yaklang.com/chrome-extension/manifest.json")!

    private struct BundledExtensionDescriptor: Decodable {
        let version: String
        let sha256: String
        let size: Int64
        let variant: String
    }

    static func pluginsRoot(applicationDirectory: URL) -> URL {
        applicationDirectory.appendingPathComponent("Plugins", isDirectory: true)
    }

    static func pluginDirectory(applicationDirectory: URL, version: String) -> URL {
        pluginsRoot(applicationDirectory: applicationDirectory)
            .appendingPathComponent("yakit-browser-agent/\(version)", isDirectory: true)
    }

    private static func managedExtensionOptOutURL(applicationDirectory: URL) -> URL {
        pluginsRoot(applicationDirectory: applicationDirectory)
            .appendingPathComponent(".yakit-browser-agent-removed")
    }

    static func bundledVersion(resourcesURL: URL? = Bundle.main.resourceURL) -> String? {
        guard let package = try? bundledPackage(resourcesURL: resourcesURL) else { return nil }
        return package.descriptor.version
    }

    /// Installs the archive placed in YTray.app by the packaging workflow. The pinned archive
    /// is validated again at runtime before it is expanded into the user's Plugins directory.
    static func installBundled(
        into applicationDirectory: URL,
        resourcesURL: URL? = Bundle.main.resourceURL,
        ignoreOptOut: Bool = false,
        replaceExisting: Bool = false
    ) throws -> (directory: URL, version: String)? {
        if !ignoreOptOut,
           FileManager.default.fileExists(atPath: managedExtensionOptOutURL(applicationDirectory: applicationDirectory).path) {
            return nil
        }
        guard let package = try bundledPackage(resourcesURL: resourcesURL) else { return nil }
        let descriptor = package.descriptor
        let versionParts = descriptor.version.split(separator: ".", omittingEmptySubsequences: false)
        guard descriptor.variant.caseInsensitiveCompare(enterpriseVariant) == .orderedSame,
              !versionParts.isEmpty,
              versionParts.allSatisfy({ part in
                  !part.isEmpty && part.utf8.allSatisfy({ byte in byte >= 48 && byte <= 57 })
              }) else {
            throw YTrayError.extensionInstallFailed("内置插件元数据无效")
        }
        let attributes = try FileManager.default.attributesOfItem(atPath: package.archive.path)
        guard (attributes[.size] as? NSNumber)?.int64Value == descriptor.size else {
            throw YTrayError.extensionInstallFailed("内置插件大小校验失败")
        }
        guard try sha256(of: package.archive).caseInsensitiveCompare(descriptor.sha256) == .orderedSame else {
            throw YTrayError.extensionInstallFailed("内置插件 SHA-256 校验失败")
        }
        try validateArchiveEntries(package.archive)

        let destination = pluginDirectory(applicationDirectory: applicationDirectory, version: descriptor.version)
        if !replaceExisting, let existing = try? resolveExtensionRoot(destination) {
            return (existing, descriptor.version)
        }

        let temporary = destination.deletingLastPathComponent()
            .appendingPathComponent(destination.lastPathComponent + ".partial-" + UUID().uuidString, isDirectory: true)
        try? FileManager.default.removeItem(at: temporary)
        try FileManager.default.createDirectory(at: temporary, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: temporary) }

        let unzip = Process()
        unzip.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
        unzip.arguments = ["-x", "-k", package.archive.path, temporary.path]
        let errorPipe = Pipe()
        unzip.standardError = errorPipe
        try unzip.run()
        unzip.waitUntilExit()
        guard unzip.terminationStatus == 0 else {
            let data = errorPipe.fileHandleForReading.readDataToEndOfFile()
            throw YTrayError.extensionInstallFailed(String(data: data, encoding: .utf8) ?? "内置插件解压失败")
        }
        _ = try resolveExtensionRoot(temporary)
        try? FileManager.default.removeItem(at: destination)
        try FileManager.default.createDirectory(at: destination.deletingLastPathComponent(), withIntermediateDirectories: true)
        try FileManager.default.moveItem(at: temporary, to: destination)
        return (try resolveExtensionRoot(destination), descriptor.version)
    }

    static func markManagedExtensionRemoved(applicationDirectory: URL) {
        let marker = managedExtensionOptOutURL(applicationDirectory: applicationDirectory)
        try? FileManager.default.createDirectory(at: marker.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? Data(Date().ISO8601Format().utf8).write(to: marker, options: .atomic)
    }

    static func clearManagedExtensionRemoved(applicationDirectory: URL) {
        try? FileManager.default.removeItem(at: managedExtensionOptOutURL(applicationDirectory: applicationDirectory))
    }

    private static func bundledPackage(resourcesURL: URL?) throws
        -> (descriptor: BundledExtensionDescriptor, archive: URL)? {
        guard let root = resourcesURL?.appendingPathComponent("BundledExtension", isDirectory: true) else { return nil }
        let descriptorURL = root.appendingPathComponent("bundled-extension.json")
        let archiveURL = root.appendingPathComponent("yakit-browser-agent.zip")
        guard FileManager.default.fileExists(atPath: descriptorURL.path),
              FileManager.default.fileExists(atPath: archiveURL.path) else { return nil }
        return (try JSONDecoder().decode(BundledExtensionDescriptor.self, from: Data(contentsOf: descriptorURL)), archiveURL)
    }

    private static func validateArchiveEntries(_ archive: URL) throws {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/unzip")
        process.arguments = ["-Z1", archive.path]
        let output = Pipe()
        process.standardOutput = output
        process.standardError = Pipe()
        try process.run()
        let listingData = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        guard process.terminationStatus == 0,
              let listing = String(data: listingData, encoding: .utf8) else {
            throw YTrayError.extensionInstallFailed("无法检查内置插件压缩包")
        }
        for entry in listing.split(separator: "\n").map(String.init) {
            let components = entry.replacingOccurrences(of: "\\", with: "/").split(separator: "/")
            if entry.hasPrefix("/") || components.contains(where: { $0 == ".." }) {
                throw YTrayError.extensionInstallFailed("内置插件包含不安全路径")
            }
        }
    }

    static func fetchManifest() async throws -> ExtensionManifest {
        let (data, response) = try await URLSession.shared.data(from: manifestURL)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else {
            throw YTrayError.extensionInstallFailed("插件清单返回异常")
        }
        do {
            let manifest = try JSONDecoder().decode(ExtensionManifest.self, from: data)
            guard !manifest.latest.isEmpty else {
                throw YTrayError.extensionInstallFailed("插件清单格式无效")
            }
            return manifest
        } catch let error as YTrayError {
            throw error
        } catch {
            throw YTrayError.extensionInstallFailed("插件清单格式无效")
        }
    }

    static func enterpriseArtifact(of version: ExtensionReleaseVersion) -> ExtensionArtifact? {
        version.artifacts.first { $0.variant.caseInsensitiveCompare(enterpriseVariant) == .orderedSame && !$0.url.isEmpty }
    }

    /// Compares dotted numeric versions ("0.2.10" > "0.2.2"). Non-numeric segments
    /// fall back to ordinal comparison; a parse failure never throws.
    static func compareVersions(_ left: String?, _ right: String?) -> ComparisonResult {
        func segments(_ raw: String?) -> [String] {
            (raw ?? "")
                .trimmingCharacters(in: .whitespaces)
                .drop { $0 == "v" || $0 == "V" }
                .split(separator: ".", omittingEmptySubsequences: false)
                .map(String.init)
        }
        let leftParts = segments(left)
        let rightParts = segments(right)
        for index in 0 ..< max(leftParts.count, rightParts.count) {
            let lp = index < leftParts.count ? leftParts[index] : ""
            let rp = index < rightParts.count ? rightParts[index] : ""
            if let ln = Int64(lp), let rn = Int64(rp) {
                if ln != rn { return ln < rn ? .orderedAscending : .orderedDescending }
            } else {
                let ordinal = lp.compare(rp, options: [.literal])
                if ordinal != .orderedSame { return ordinal }
            }
        }
        return (left ?? "").compare(right ?? "", options: [.literal])
    }

    /// Decides whether the embedded plugin should be installed automatically. A missing
    /// local plugin is initialized from the bundle; an existing plugin is upgraded only
    /// when the embedded version is strictly newer. This prevents equal-version rewrites
    /// and downgrades after a user has independently updated the plugin.
    static func shouldInstallBundledVersion(
        _ bundledVersion: String?,
        installedVersion: String?,
        allowSameVersion: Bool = false
    ) -> Bool {
        guard let bundledVersion,
              !bundledVersion.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return false }
        guard let installedVersion,
              !installedVersion.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return true }
        let comparison = compareVersions(bundledVersion, installedVersion)
        return comparison == .orderedDescending || (allowSameVersion && comparison == .orderedSame)
    }

    /// Installs the given release into Plugins/yakit-browser-agent/{version} and returns
    /// the extracted extension directory (the one containing manifest.json). Existing
    /// installs of the same version are replaced, which doubles as a repair path.
    static func install(version: ExtensionReleaseVersion, into applicationDirectory: URL) async throws -> URL {
        guard let artifact = enterpriseArtifact(of: version), let url = URL(string: artifact.url) else {
            throw YTrayError.extensionInstallFailed("版本 \(version.version) 没有 \(enterpriseVariant) 产物")
        }
        let (temporary, response) = try await URLSession.shared.download(from: url)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else {
            throw YTrayError.extensionInstallFailed("ZIP 下载返回异常")
        }
        if !artifact.sha256.isEmpty {
            let actualHash = try sha256(of: temporary)
            guard actualHash.caseInsensitiveCompare(artifact.sha256) == .orderedSame else {
                throw YTrayError.extensionInstallFailed("SHA-256 校验失败")
            }
        }

        let destination = pluginDirectory(applicationDirectory: applicationDirectory, version: version.version)
        try? FileManager.default.removeItem(at: destination)
        try FileManager.default.createDirectory(at: destination, withIntermediateDirectories: true)
        let unzip = Process()
        unzip.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
        unzip.arguments = ["-x", "-k", temporary.path, destination.path]
        let errorPipe = Pipe()
        unzip.standardError = errorPipe
        try unzip.run()
        unzip.waitUntilExit()
        guard unzip.terminationStatus == 0 else {
            let data = errorPipe.fileHandleForReading.readDataToEndOfFile()
            throw YTrayError.extensionInstallFailed(String(data: data, encoding: .utf8) ?? "ZIP 解压失败")
        }
        return try resolveExtensionRoot(destination)
    }

    /// Registers the zip root directly; a single nested directory containing the manifest
    /// (defensive against future packaging changes) is also accepted.
    private static func resolveExtensionRoot(_ directory: URL) throws -> URL {
        if FileManager.default.fileExists(atPath: directory.appendingPathComponent("manifest.json").path) {
            return directory
        }
        let children = (try? FileManager.default.contentsOfDirectory(
            at: directory, includingPropertiesForKeys: nil, options: [.skipsHiddenFiles])) ?? []
        if children.count == 1,
           FileManager.default.fileExists(atPath: children[0].appendingPathComponent("manifest.json").path) {
            return children[0]
        }
        throw YTrayError.extensionInstallFailed("解压后未找到 manifest.json")
    }

    /// Drops version directories of the managed plugin other than the installed one so
    /// upgrades do not accumulate old copies. Best-effort; locked files are ignored.
    static func cleanupOldVersions(applicationDirectory: URL, installedVersion: String) {
        let root = pluginsRoot(applicationDirectory: applicationDirectory)
            .appendingPathComponent("yakit-browser-agent", isDirectory: true)
        let children = (try? FileManager.default.contentsOfDirectory(
            at: root, includingPropertiesForKeys: nil, options: [.skipsHiddenFiles])) ?? []
        for child in children where child.lastPathComponent != installedVersion {
            try? FileManager.default.removeItem(at: child)
        }
    }

    private static func sha256(of url: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var hasher = SHA256()
        while true {
            guard let chunk = try handle.read(upToCount: 1024 * 1024), !chunk.isEmpty else { break }
            hasher.update(data: chunk)
        }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }
}
