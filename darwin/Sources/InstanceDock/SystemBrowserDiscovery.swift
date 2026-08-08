import Foundation

enum SystemBrowserDiscovery {
    private struct Candidate {
        let appName: String
        let executableName: String
        let kind: BrowserKind
    }

    private static let candidates = [
        Candidate(appName: "Google Chrome.app", executableName: "Google Chrome", kind: .chrome),
        Candidate(appName: "Google Chrome Beta.app", executableName: "Google Chrome Beta", kind: .chromeBeta),
        Candidate(appName: "Google Chrome Canary.app", executableName: "Google Chrome Canary", kind: .chromeCanary),
        Candidate(appName: "Google Chrome for Testing.app", executableName: "Google Chrome for Testing", kind: .chromeForTesting),
        Candidate(appName: "Chromium.app", executableName: "Chromium", kind: .chromium),
        Candidate(appName: "Microsoft Edge.app", executableName: "Microsoft Edge", kind: .edge),
        Candidate(appName: "Microsoft Edge Beta.app", executableName: "Microsoft Edge Beta", kind: .edge),
        Candidate(appName: "Microsoft Edge Canary.app", executableName: "Microsoft Edge Canary", kind: .edge),
    ]

    static func discover() -> [BrowserRuntime] {
        let roots = [
            URL(fileURLWithPath: "/Applications", isDirectory: true),
            FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Applications", isDirectory: true),
        ]
        var found: [BrowserRuntime] = []
        for root in roots {
            for candidate in candidates {
                let app = root.appendingPathComponent(candidate.appName, isDirectory: true)
                let executable = app.appendingPathComponent("Contents/MacOS/\(candidate.executableName)")
                guard FileManager.default.isExecutableFile(atPath: executable.path) else { continue }
                found.append(BrowserRuntime(
                    name: candidate.kind.title,
                    version: readVersion(executable) ?? "版本未知",
                    architecture: RuntimeInstaller.platform,
                    executablePath: executable.path,
                    source: .system,
                    browserKind: candidate.kind
                ))
            }
        }
        return found
    }

    static func inspect(selectedURL: URL) -> BrowserRuntime? {
        guard let executable = RuntimeInstaller.normalizeExecutable(selectedURL) else { return nil }
        let rawName = selectedURL.pathExtension.lowercased() == "app"
            ? selectedURL.deletingPathExtension().lastPathComponent : executable.lastPathComponent
        let kind = BrowserKind.infer(name: rawName, path: executable.path)
        let standardPath = executable.path.hasPrefix("/Applications/")
            || executable.path.hasPrefix(FileManager.default.homeDirectoryForCurrentUser
                .appendingPathComponent("Applications").path + "/")
        return BrowserRuntime(
            name: kind.title,
            version: readVersion(executable) ?? "版本未知",
            architecture: RuntimeInstaller.platform,
            executablePath: executable.path,
            source: standardPath ? .system : .local,
            browserKind: kind
        )
    }

    private static func readVersion(_ executable: URL) -> String? {
        let process = Process()
        process.executableURL = executable
        process.arguments = ["--version"]
        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = pipe
        do {
            try process.run()
            process.waitUntilExit()
            let value = String(data: pipe.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8)?
                .trimmingCharacters(in: .whitespacesAndNewlines)
            return value?.split(separator: " ").last.map(String.init)
        } catch {
            return nil
        }
    }
}
