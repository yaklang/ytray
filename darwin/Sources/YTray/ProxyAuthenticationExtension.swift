import Foundation

enum ProxyAuthenticationExtension {
    static func write(
        instanceID: UUID,
        username: String,
        password: String,
        applicationDirectory: URL
    ) throws -> URL? {
        guard !username.isEmpty || !password.isEmpty else { return nil }
        let directory = extensionURL(for: instanceID, applicationDirectory: applicationDirectory)
        try? FileManager.default.removeItem(at: directory)
        try FileManager.default.createDirectory(
            at: directory,
            withIntermediateDirectories: true,
            attributes: [.posixPermissions: 0o700]
        )

        let manifest: [String: Any] = [
            "manifest_version": 3,
            "name": "YTray Proxy Authentication",
            "version": "1.0.0",
            "permissions": ["webRequest", "webRequestAuthProvider"],
            "host_permissions": ["<all_urls>"],
            "background": ["service_worker": "background.js"],
        ]
        let manifestData = try JSONSerialization.data(
            withJSONObject: manifest,
            options: [.prettyPrinted, .sortedKeys]
        )
        let usernameLiteral = try javascriptLiteral(username)
        let passwordLiteral = try javascriptLiteral(password)
        let script = """
        const username = \(usernameLiteral);
        const password = \(passwordLiteral);
        const attempts = new Map();

        chrome.webRequest.onAuthRequired.addListener(
          (details, callback) => {
            if (!details.isProxy) {
              callback({});
              return;
            }
            const count = (attempts.get(details.requestId) || 0) + 1;
            attempts.set(details.requestId, count);
            if (count > 1) {
              callback({ cancel: true });
              return;
            }
            callback({ authCredentials: { username, password } });
          },
          { urls: ["<all_urls>"] },
          ["asyncBlocking"]
        );

        const clearAttempt = details => attempts.delete(details.requestId);
        chrome.webRequest.onCompleted.addListener(clearAttempt, { urls: ["<all_urls>"] });
        chrome.webRequest.onErrorOccurred.addListener(clearAttempt, { urls: ["<all_urls>"] });
        """
        let manifestURL = directory.appendingPathComponent("manifest.json")
        let scriptURL = directory.appendingPathComponent("background.js")
        try manifestData.write(to: manifestURL, options: .atomic)
        try Data(script.utf8).write(to: scriptURL, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: directory.path)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: manifestURL.path)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: scriptURL.path)
        return directory
    }

    static func remove(instanceID: UUID, applicationDirectory: URL) {
        try? FileManager.default.removeItem(
            at: extensionURL(for: instanceID, applicationDirectory: applicationDirectory)
        )
    }

    static func extensionURL(for instanceID: UUID, applicationDirectory: URL) -> URL {
        applicationDirectory
            .appendingPathComponent("ProxyAuth", isDirectory: true)
            .appendingPathComponent(instanceID.uuidString, isDirectory: true)
    }

    private static func javascriptLiteral(_ value: String) throws -> String {
        let data = try JSONEncoder().encode(value)
        guard let literal = String(data: data, encoding: .utf8) else {
            throw YTrayError.launchFailed("无法生成代理认证配置")
        }
        return literal
    }
}
