import Foundation
import CryptoKit

enum ExtensionInstaller {
    static let enterpriseVariant = "chrome-enterprise"
    // Identifies the managed plugin across upgrades; the extension's manifest name
    // ("Yakit Browser Agent") stays stable, so match on it instead of tracking paths.
    static let extensionName = "Yakit Browser Agent"

    static let manifestURL = URL(string: "https://aliyun-oss.yaklang.com/chrome-extension/manifest.json")!

    static func pluginsRoot(applicationDirectory: URL) -> URL {
        applicationDirectory.appendingPathComponent("Plugins", isDirectory: true)
    }

    static func pluginDirectory(applicationDirectory: URL, version: String) -> URL {
        pluginsRoot(applicationDirectory: applicationDirectory)
            .appendingPathComponent("yakit-browser-agent/\(version)", isDirectory: true)
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
