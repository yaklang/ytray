import Foundation
import CryptoKit

enum RuntimeInstaller {
    static let manifestURL = URL(string: "https://aliyun-oss.yaklang.com/browsers/chrome/manifest.json")!

    static var architecture: String {
        #if arch(arm64)
        return "arm64"
        #else
        return "x64"
        #endif
    }

    static var platform: String { "macos-\(architecture)" }

    static func fetchVersions() async throws -> [MirrorVersion] {
        let (data, response) = try await URLSession.shared.data(from: manifestURL)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else {
            throw YTrayError.downloadFailed("镜像清单返回异常")
        }
        return try JSONDecoder().decode(MirrorManifest.self, from: data).versions
    }

    static func install(version: MirrorVersion, into applicationDirectory: URL) async throws -> BrowserRuntime {
        guard let artifact = version.artifacts.first(where: {
            $0.os == "macos" && $0.arch == architecture && $0.format == "zip"
        }), let url = URL(string: artifact.url) else {
            throw YTrayError.downloadFailed("版本 \(version.version) 没有 \(platform) ZIP")
        }
        let (temporary, response) = try await URLSession.shared.download(from: url)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else {
            throw YTrayError.downloadFailed("ZIP 下载返回异常")
        }
        let actualHash = try sha256(of: temporary)
        guard actualHash.caseInsensitiveCompare(artifact.sha256) == .orderedSame else {
            throw YTrayError.downloadFailed("SHA-256 校验失败")
        }

        let destination = applicationDirectory.appendingPathComponent("Runtimes/\(version.version)/\(platform)", isDirectory: true)
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
            throw YTrayError.downloadFailed(String(data: data, encoding: .utf8) ?? "ZIP 解压失败")
        }
        guard let executable = locateChrome(in: destination) else {
            throw YTrayError.downloadFailed("ZIP 内未找到 Google Chrome for Testing 可执行文件")
        }
        return BrowserRuntime(name: "Chrome for Testing \(version.version)", version: version.version,
                              architecture: platform, executablePath: executable.path, source: .managed,
                              browserKind: .chromeForTesting)
    }

    static func normalizeExecutable(_ selected: URL) -> URL? {
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: selected.path, isDirectory: &isDirectory) else { return nil }
        if selected.pathExtension.lowercased() == "app" {
            let macOS = selected.appendingPathComponent("Contents/MacOS", isDirectory: true)
            if let items = try? FileManager.default.contentsOfDirectory(at: macOS, includingPropertiesForKeys: nil),
               let executable = items.first(where: { FileManager.default.isExecutableFile(atPath: $0.path) }) {
                return executable
            }
        }
        if isDirectory.boolValue { return locateChrome(in: selected) }
        return FileManager.default.isExecutableFile(atPath: selected.path) ? selected : nil
    }

    static func locateChrome(in root: URL) -> URL? {
        guard let enumerator = FileManager.default.enumerator(
            at: root, includingPropertiesForKeys: [.isRegularFileKey], options: [.skipsHiddenFiles]
        ) else { return nil }
        for case let candidate as URL in enumerator {
            if candidate.path.contains(".app/Contents/MacOS/") &&
                candidate.lastPathComponent.localizedCaseInsensitiveContains("Chrome") &&
                FileManager.default.isExecutableFile(atPath: candidate.path) { return candidate }
        }
        return nil
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
