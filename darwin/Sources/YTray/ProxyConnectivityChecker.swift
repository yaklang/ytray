import Foundation
import Network

struct ProxyCheckResult: Equatable, Sendable {
    var isSuccess: Bool
    var message: String
}

struct ProxyCheckDetail: Identifiable, Equatable, Sendable {
    var target: String
    var isSuccess: Bool
    var message: String
    var elapsedMilliseconds: Int

    var id: String { target }
}

struct ProxyCheckReport: Equatable, Sendable {
    var details: [ProxyCheckDetail]

    var successCount: Int { details.filter(\.isSuccess).count }
    var isSuccess: Bool { successCount > 0 }
    var message: String {
        let prefix = isSuccess ? "检测成功" : "检测失败"
        return "\(prefix) · \(successCount)/\(details.count) 个目标可访问"
    }
}

enum ProxyConnectivityChecker {
    static let defaultTimeout: TimeInterval = 10
    static let defaultTargetStrings = [
        "https://example.com/",
        "https://baidu.com/",
        "https://google.com/",
    ]
    private static let queue = DispatchQueue(label: "com.yaklang.ytray.proxy-check")
    private static let fallbackTarget = URL(string: defaultTargetStrings[0])!

    static func normalizeTarget(_ rawValue: String) throws -> URL {
        let trimmed = rawValue.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { throw YTrayError.invalidURL(rawValue) }
        let candidate = trimmed.contains("://") ? trimmed : "https://\(trimmed)"
        guard var components = URLComponents(string: candidate),
              let scheme = components.scheme?.lowercased(),
              scheme == "http" || scheme == "https",
              let host = components.host,
              !host.isEmpty,
              components.user == nil,
              components.password == nil,
              components.port.map({ (1...65_535).contains($0) }) ?? true else {
            throw YTrayError.invalidURL(rawValue)
        }
        components.scheme = scheme
        if components.path.isEmpty { components.path = "/" }
        guard let url = components.url else { throw YTrayError.invalidURL(rawValue) }
        return url
    }

    static func checkDefaultTargets(
        endpoint: ProxyEndpoint,
        username: String,
        password: String,
        customTarget: String,
        timeout: TimeInterval = defaultTimeout
    ) async -> ProxyCheckReport {
        var targets = defaultTargetStrings.compactMap { try? normalizeTarget($0) }
        var validationFailures: [ProxyCheckDetail] = []
        let trimmedCustomTarget = customTarget.trimmingCharacters(in: .whitespacesAndNewlines)
        if !trimmedCustomTarget.isEmpty {
            do {
                let customURL = try normalizeTarget(trimmedCustomTarget)
                if !targets.contains(where: {
                    $0.absoluteString.caseInsensitiveCompare(customURL.absoluteString) == .orderedSame
                }) {
                    targets.append(customURL)
                }
            } catch {
                validationFailures.append(ProxyCheckDetail(
                    target: trimmedCustomTarget,
                    isSuccess: false,
                    message: (error as? LocalizedError)?.errorDescription ?? error.localizedDescription,
                    elapsedMilliseconds: 0
                ))
            }
        }

        let checked = await withTaskGroup(of: IndexedProxyCheckDetail.self) { group in
            for (index, target) in targets.enumerated() {
                group.addTask {
                    let startedAt = ContinuousClock.now
                    let result = await check(
                        endpoint: endpoint,
                        username: username,
                        password: password,
                        target: target,
                        timeout: timeout
                    )
                    let duration = startedAt.duration(to: .now)
                    let milliseconds = Int(duration.components.seconds * 1_000)
                        + Int(duration.components.attoseconds / 1_000_000_000_000_000)
                    return IndexedProxyCheckDetail(index: index, detail: ProxyCheckDetail(
                        target: target.absoluteString,
                        isSuccess: result.isSuccess,
                        message: result.message,
                        elapsedMilliseconds: milliseconds
                    ))
                }
            }
            var values: [IndexedProxyCheckDetail] = []
            for await value in group { values.append(value) }
            return values.sorted { $0.index < $1.index }.map(\.detail)
        }
        return ProxyCheckReport(details: checked + validationFailures)
    }

    static func check(
        endpoint: ProxyEndpoint,
        username: String,
        password: String,
        target: URL = fallbackTarget,
        timeout: TimeInterval = defaultTimeout
    ) async -> ProxyCheckResult {
        await withCheckedContinuation { continuation in
            let parameters: NWParameters = endpoint.scheme == .https ? .tls : .tcp
            guard let port = NWEndpoint.Port(rawValue: UInt16(endpoint.port)) else {
                continuation.resume(returning: ProxyCheckResult(
                    isSuccess: false,
                    message: "端口必须在 1–65535 之间"
                ))
                return
            }
            let connection = NWConnection(
                host: NWEndpoint.Host(endpoint.host),
                port: port,
                using: parameters
            )
            let completion = ProxyCheckCompletion { result in
                connection.cancel()
                continuation.resume(returning: result)
            }
            connection.stateUpdateHandler = { state in
                switch state {
                case .ready:
                    sendProbe(
                        over: connection,
                        endpoint: endpoint,
                        username: username,
                        password: password,
                        target: target,
                        completion: completion
                    )
                case .failed(let error):
                    completion.finish(ProxyCheckResult(
                        isSuccess: false,
                        message: "连接失败 · \(friendly(error))"
                    ))
                default:
                    break
                }
            }
            connection.start(queue: queue)
            queue.asyncAfter(deadline: .now() + timeout) {
                completion.finish(ProxyCheckResult(
                    isSuccess: false,
                    message: "检测超时 · 请检查 Host、端口或代理服务"
                ))
            }
        }
    }

    static func interpretResponse(_ data: Data, usedCredentials: Bool) -> ProxyCheckResult {
        guard let response = String(data: data, encoding: .utf8),
              let statusLine = response.components(separatedBy: "\r\n").first else {
            return ProxyCheckResult(isSuccess: false, message: "代理返回了无法识别的响应")
        }
        let parts = statusLine.split(separator: " ")
        guard parts.count >= 2, let status = Int(parts[1]) else {
            return ProxyCheckResult(isSuccess: false, message: "代理返回了无法识别的状态")
        }
        if (200..<300).contains(status) {
            return ProxyCheckResult(
                isSuccess: true,
                message: usedCredentials ? "检测成功 · 代理和认证可用" : "检测成功 · 代理可用"
            )
        }
        if status == 407 {
            return ProxyCheckResult(
                isSuccess: false,
                message: usedCredentials ? "认证失败 · 请检查用户名和密码" : "代理需要认证 · 请展开高级设置"
            )
        }
        return ProxyCheckResult(isSuccess: false, message: "代理响应异常 · HTTP \(status)")
    }

    static func probeRequest(username: String, password: String) -> Data {
        probeRequest(target: fallbackTarget, username: username, password: password)
    }

    static func probeRequest(target: URL, username: String, password: String) -> Data {
        let host = target.host ?? "www.example.com"
        let port = target.port ?? (target.scheme?.lowercased() == "http" ? 80 : 443)
        let authorityHost = host.contains(":") ? "[\(host)]" : host
        let authority = "\(authorityHost):\(port)"
        var lines = [
            "CONNECT \(authority) HTTP/1.1",
            "Host: \(authority)",
            "Proxy-Connection: close",
        ]
        if !username.isEmpty || !password.isEmpty {
            let token = Data("\(username):\(password)".utf8).base64EncodedString()
            lines.append("Proxy-Authorization: Basic \(token)")
        }
        return Data((lines.joined(separator: "\r\n") + "\r\n\r\n").utf8)
    }

    private static func sendProbe(
        over connection: NWConnection,
        endpoint: ProxyEndpoint,
        username: String,
        password: String,
        target: URL,
        completion: ProxyCheckCompletion
    ) {
        let usedCredentials = !username.isEmpty || !password.isEmpty
        connection.send(content: probeRequest(target: target, username: username, password: password), completion: .contentProcessed { error in
            if let error {
                completion.finish(ProxyCheckResult(
                    isSuccess: false,
                    message: "发送检测请求失败 · \(friendly(error))"
                ))
                return
            }
            connection.receive(minimumIncompleteLength: 1, maximumLength: 8_192) { data, _, _, error in
                if let error {
                    completion.finish(ProxyCheckResult(
                        isSuccess: false,
                        message: "读取代理响应失败 · \(friendly(error))"
                    ))
                    return
                }
                guard let data, !data.isEmpty else {
                    completion.finish(ProxyCheckResult(
                        isSuccess: false,
                        message: "代理已连接，但没有返回检测响应"
                    ))
                    return
                }
                completion.finish(interpretResponse(data, usedCredentials: usedCredentials))
            }
        })
    }

    private static func friendly(_ error: NWError) -> String {
        switch error {
        case .posix(let code): return POSIXError(code).localizedDescription
        case .dns(let code): return "DNS 错误 \(code)"
        case .tls(let code): return "TLS 错误 \(code)"
        @unknown default: return error.localizedDescription
        }
    }
}

private struct IndexedProxyCheckDetail: Sendable {
    var index: Int
    var detail: ProxyCheckDetail
}

private final class ProxyCheckCompletion: @unchecked Sendable {
    private let lock = NSLock()
    private var didFinish = false
    private let handler: (ProxyCheckResult) -> Void

    init(handler: @escaping (ProxyCheckResult) -> Void) {
        self.handler = handler
    }

    func finish(_ result: ProxyCheckResult) {
        lock.lock()
        guard !didFinish else {
            lock.unlock()
            return
        }
        didFinish = true
        lock.unlock()
        handler(result)
    }
}
