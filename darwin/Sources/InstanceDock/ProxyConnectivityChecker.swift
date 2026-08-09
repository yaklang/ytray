import Foundation
import Network

struct ProxyCheckResult: Equatable {
    var isSuccess: Bool
    var message: String
}

enum ProxyConnectivityChecker {
    private static let queue = DispatchQueue(label: "com.yaklang.instance-dock.proxy-check")

    static func check(
        endpoint: ProxyEndpoint,
        username: String,
        password: String,
        timeout: TimeInterval = 6
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
        var lines = [
            "CONNECT www.example.com:443 HTTP/1.1",
            "Host: www.example.com:443",
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
        completion: ProxyCheckCompletion
    ) {
        let usedCredentials = !username.isEmpty || !password.isEmpty
        connection.send(content: probeRequest(username: username, password: password), completion: .contentProcessed { error in
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
