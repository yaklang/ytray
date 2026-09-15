import Foundation

struct ExtensionLoadingError: LocalizedError {
    let message: String
    var errorDescription: String? { message }
}

struct BrowserDevToolsError: LocalizedError {
    let code: Int
    let message: String
    var errorDescription: String? { message }
    var isUnavailable: Bool {
        code == -32601 || message.lowercased().hasPrefix("method not available")
    }
}

/// Extension commands must go to the browser target, not a page WebSocket.
enum BrowserDevTools {
    static func socketURL(debugPort: Int) async throws -> URL {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.connectionProxyDictionary = [:]
        configuration.timeoutIntervalForRequest = 3
        let session = URLSession(configuration: configuration)
        defer { session.invalidateAndCancel() }
        guard (1...65_535).contains(debugPort),
              let endpoint = URL(string: "http://127.0.0.1:\(debugPort)/json/version") else {
            throw URLError(.badURL)
        }
        let (data, response) = try await session.data(from: endpoint)
        guard (response as? HTTPURLResponse)?.statusCode == 200,
              let object = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let address = object["webSocketDebuggerUrl"] as? String else {
            throw URLError(.badServerResponse)
        }
        return try validatedSocketURL(address, debugPort: debugPort)
    }

    static func validatedSocketURL(_ address: String, debugPort: Int) throws -> URL {
        guard let url = URL(string: address), url.scheme == "ws", url.host == "127.0.0.1",
              url.port == debugPort, url.user == nil, url.password == nil,
              url.path.hasPrefix("/devtools/browser/"), url.path != "/devtools/browser/" else {
            throw ExtensionLoadingError(message: "浏览器返回了无效的本地调试地址")
        }
        return url
    }

    static func send(socketURL: URL, method: String, parameters: [String: Any] = [:]) async throws -> [String: Any] {
        try Task.checkCancellation()
        let request = try JSONSerialization.data(withJSONObject: ["id": 1, "method": method, "params": parameters])
        let messages = try await RawWebSocket.exchange(url: socketURL, message: request)
        try Task.checkCancellation()
        for data in messages {
            guard let reply = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                  reply["id"] as? Int == 1 else { continue }
            if let error = reply["error"] as? [String: Any] {
                throw BrowserDevToolsError(code: error["code"] as? Int ?? 0,
                                          message: error["message"] as? String ?? "扩展接口返回错误")
            }
            guard let result = reply["result"] as? [String: Any] else { break }
            return result
        }
        throw ExtensionLoadingError(message: "浏览器没有返回扩展操作结果")
    }
}

enum BrowserExtensionService {
    static func legacyLoadingExpected(runtimeKind: BrowserKind?, version: String?) -> Bool {
        if let runtimeKind, BrowserLauncher.supportsCommandLineExtensions(runtimeKind: runtimeKind) { return true }
        guard let major = version?.split(separator: ".").first.flatMap({ Int($0) }) else { return false }
        return major > 0 && major < 137
    }

    static func ensureLoaded(debugPort: Int, paths: [String], legacyLoadingExpected: Bool) async throws {
        guard !paths.isEmpty else { return }
        do {
            let socketURL = try await BrowserDevTools.socketURL(debugPort: debugPort)
            let deadline = Date().addingTimeInterval(25)
            try await ensureLoaded(paths: paths, legacyLoadingExpected: legacyLoadingExpected) { method, parameters in
                guard Date() < deadline else { throw URLError(.timedOut) }
                return try await BrowserDevTools.send(socketURL: socketURL, method: method, parameters: parameters)
            }
        } catch is CancellationError { throw CancellationError() }
        catch let error as ExtensionLoadingError { throw error }
        catch { throw ExtensionLoadingError(message: "无法完成插件加载验证：\(error.localizedDescription)") }
    }

    /// A command seam lets regression tests exercise old and new protocol versions.
    static func ensureLoaded(
        paths: [String], legacyLoadingExpected: Bool,
        send: (String, [String: Any]) async throws -> [String: Any]
    ) async throws {
        var seen = Set<String>()
        // Deduplicate aliases without conflating distinct paths on a
        // case-sensitive macOS volume.
        let selected = paths.map { URL(fileURLWithPath: $0).standardizedFileURL.path }
            .filter { seen.insert(normalizedPath($0)).inserted }
        guard !selected.isEmpty else { return }
        var installed: [[String: Any]]?
        do { installed = try extensionList(await send("Extensions.getExtensions", [:])) }
        catch let error as BrowserDevToolsError where error.isUnavailable { }

        for path in selected {
            try Task.checkCancellation()
            if installed?.contains(where: { matches($0, path: path) && $0["enabled"] as? Bool == true
                && validID($0["id"] as? String) }) == true { continue }
            do {
                let result = try await send("Extensions.loadUnpacked", ["path": path])
                guard let id = result["id"] as? String, validID(id) else {
                    throw ExtensionLoadingError(message: "加载接口没有返回有效插件 ID")
                }
                if installed != nil {
                    var enabled = false
                    for attempt in 0..<5 {
                        installed = try extensionList(await send("Extensions.getExtensions", [:]))
                        enabled = installed?.contains(where: { $0["id"] as? String == id
                            && matches($0, path: path) && $0["enabled"] as? Bool == true }) == true
                        if enabled { break }
                        if attempt < 4 { try await Task.sleep(nanoseconds: 100_000_000) }
                    }
                    guard enabled else { throw ExtensionLoadingError(message: "浏览器未确认插件已启用") }
                }
                DiagnosticLog.info("extension.load", "CDP loaded extension=\(id)")
            } catch let error as BrowserDevToolsError where error.isUnavailable && installed == nil && legacyLoadingExpected {
                DiagnosticLog.info("extension.load", "CDP unavailable; retaining supported legacy command-line loading")
                return
            } catch is CancellationError { throw CancellationError() }
            catch {
                throw ExtensionLoadingError(message: "插件加载失败（\(URL(fileURLWithPath: path).lastPathComponent)）：\(error.localizedDescription)")
            }
        }
    }

    private static func extensionList(_ result: [String: Any]) throws -> [[String: Any]] {
        guard let list = result["extensions"] as? [[String: Any]] else {
            throw ExtensionLoadingError(message: "浏览器返回了无效的插件列表")
        }
        return list
    }

    static func normalizedPath(_ path: String) -> String {
        ExtensionInstaller.canonicalExtensionPath(path)
    }

    private static func matches(_ item: [String: Any], path: String) -> Bool {
        guard let actual = item["path"] as? String, !actual.isEmpty else { return false }
        // macOS also supports case-sensitive volumes; never lowercase paths.
        return normalizedPath(actual) == normalizedPath(path)
    }

    private static func validID(_ id: String?) -> Bool {
        guard let id, id.utf8.count == 32 else { return false }
        return id.utf8.allSatisfy { (97...112).contains($0) }
    }
}
