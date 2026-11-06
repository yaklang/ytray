import Darwin
import Foundation

struct MCPHTTPRequest {
    let method: String
    let path: String
    let headers: [String: String]
    let body: Data
}

struct MCPHTTPResponse {
    let status: Int
    let headers: [String: String]
    let body: Data

    init(status: Int, headers: [String: String] = [:], body: Data = Data()) {
        self.status = status
        self.headers = headers
        self.body = body
    }

    static func text(_ status: Int, _ value: String) -> MCPHTTPResponse {
        MCPHTTPResponse(
            status: status,
            headers: ["Content-Type": "text/plain; charset=utf-8"],
            body: Data(value.utf8)
        )
    }
}

enum MCPHTTPServerError: LocalizedError {
    case cannotCreateSocket
    case cannotBind(port: Int)
    case cannotListen

    var errorDescription: String? {
        switch self {
        case .cannotCreateSocket: return "无法创建 MCP 本地 socket"
        case .cannotBind(let port): return "MCP 端口 \(port) 已被占用或无权访问"
        case .cannotListen: return "MCP 本地服务无法开始监听"
        }
    }
}

final class MCPHTTPServer: @unchecked Sendable {
    typealias Handler = @Sendable (MCPHTTPRequest) async -> MCPHTTPResponse

    private let port: Int
    private let handler: Handler
    private let stateLock = NSLock()
    private var listener: Int32 = -1
    private let acceptQueue = DispatchQueue(label: "com.yaklang.ytray.mcp.accept", qos: .utility)
    private let clientQueue = DispatchQueue(
        label: "com.yaklang.ytray.mcp.clients",
        qos: .userInitiated,
        attributes: .concurrent
    )

    init(port: Int, handler: @escaping Handler) {
        self.port = port
        self.handler = handler
    }

    deinit { stop() }

    func start() throws {
        guard (1...65_535).contains(port) else { throw MCPHTTPServerError.cannotBind(port: port) }
        let descriptor = socket(AF_INET, SOCK_STREAM, 0)
        guard descriptor >= 0 else { throw MCPHTTPServerError.cannotCreateSocket }

        var reuse: Int32 = 1
        setsockopt(descriptor, SOL_SOCKET, SO_REUSEADDR, &reuse, socklen_t(MemoryLayout.size(ofValue: reuse)))
        var noPipe: Int32 = 1
        setsockopt(descriptor, SOL_SOCKET, SO_NOSIGPIPE, &noPipe, socklen_t(MemoryLayout.size(ofValue: noPipe)))

        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = in_port_t(port).bigEndian
        address.sin_addr = in_addr(s_addr: inet_addr("127.0.0.1"))
        let didBind = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.bind(descriptor, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard didBind == 0 else {
            close(descriptor)
            throw MCPHTTPServerError.cannotBind(port: port)
        }
        guard Darwin.listen(descriptor, 16) == 0 else {
            close(descriptor)
            throw MCPHTTPServerError.cannotListen
        }

        stateLock.lock()
        listener = descriptor
        stateLock.unlock()
        acceptQueue.async { [weak self] in self?.acceptConnections(descriptor: descriptor) }
    }

    func stop() {
        stateLock.lock()
        let descriptor = listener
        listener = -1
        stateLock.unlock()
        guard descriptor >= 0 else { return }
        Darwin.shutdown(descriptor, SHUT_RDWR)
        close(descriptor)
    }

    private func acceptConnections(descriptor: Int32) {
        while isCurrentListener(descriptor) {
            let client = Darwin.accept(descriptor, nil, nil)
            guard client >= 0 else {
                if !isCurrentListener(descriptor) { return }
                continue
            }
            clientQueue.async { self.handleClient(client) }
        }
    }

    private func isCurrentListener(_ descriptor: Int32) -> Bool {
        stateLock.lock()
        defer { stateLock.unlock() }
        return listener == descriptor
    }

    private func handleClient(_ descriptor: Int32) {
        var noPipe: Int32 = 1
        setsockopt(descriptor, SOL_SOCKET, SO_NOSIGPIPE, &noPipe, socklen_t(MemoryLayout.size(ofValue: noPipe)))
        var timeout = timeval(tv_sec: 10, tv_usec: 0)
        setsockopt(descriptor, SOL_SOCKET, SO_RCVTIMEO, &timeout, socklen_t(MemoryLayout.size(ofValue: timeout)))

        let request: MCPHTTPRequest
        do {
            request = try readRequest(descriptor)
        } catch {
            writeResponse(.text(400, "Bad Request"), descriptor: descriptor)
            close(descriptor)
            return
        }

        Task { [handler] in
            let response = await handler(request)
            self.writeResponse(response, descriptor: descriptor)
            close(descriptor)
        }
    }

    private func readRequest(_ descriptor: Int32) throws -> MCPHTTPRequest {
        let separator = Data("\r\n\r\n".utf8)
        var buffer = Data()
        var headerRange: Range<Data.Index>?
        while headerRange == nil {
            try receiveMore(into: &buffer, descriptor: descriptor)
            guard buffer.count <= 64 * 1024 else { throw URLError(.dataLengthExceedsMaximum) }
            headerRange = buffer.range(of: separator)
        }
        guard let headerRange else { throw URLError(.cannotParseResponse) }
        let headerData = buffer[..<headerRange.lowerBound]
        let lines = String(decoding: headerData, as: UTF8.self).components(separatedBy: "\r\n")
        guard let requestLine = lines.first else { throw URLError(.cannotParseResponse) }
        let parts = requestLine.split(separator: " ", omittingEmptySubsequences: true)
        guard parts.count == 3, parts[2].hasPrefix("HTTP/1.") else { throw URLError(.cannotParseResponse) }

        var headers: [String: String] = [:]
        for line in lines.dropFirst() {
            guard let colon = line.firstIndex(of: ":") else { throw URLError(.cannotParseResponse) }
            let name = line[..<colon].trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
            let value = line[line.index(after: colon)...].trimmingCharacters(in: .whitespacesAndNewlines)
            guard !name.isEmpty, headers[name] == nil else { throw URLError(.cannotParseResponse) }
            headers[name] = value
        }
        guard headers["transfer-encoding"] == nil,
              let lengthText = headers["content-length"] ?? (String(parts[0]) == "POST" ? nil : "0"),
              let contentLength = Int(lengthText),
              (0...2_000_000).contains(contentLength) else {
            throw URLError(.cannotParseResponse)
        }
        buffer.removeSubrange(..<headerRange.upperBound)
        while buffer.count < contentLength { try receiveMore(into: &buffer, descriptor: descriptor) }
        return MCPHTTPRequest(
            method: String(parts[0]),
            path: String(parts[1]),
            headers: headers,
            body: Data(buffer.prefix(contentLength))
        )
    }

    private func receiveMore(into buffer: inout Data, descriptor: Int32) throws {
        var bytes = [UInt8](repeating: 0, count: 16 * 1024)
        let count = Darwin.recv(descriptor, &bytes, bytes.count, 0)
        guard count > 0 else { throw URLError(.networkConnectionLost) }
        buffer.append(contentsOf: bytes.prefix(count))
    }

    private func writeResponse(_ response: MCPHTTPResponse, descriptor: Int32) {
        var headers = response.headers
        headers["Content-Length"] = String(response.body.count)
        headers["Connection"] = "close"
        headers["Cache-Control"] = "no-store"
        let reason: String
        switch response.status {
        case 200: reason = "OK"
        case 202: reason = "Accepted"
        case 400: reason = "Bad Request"
        case 401: reason = "Unauthorized"
        case 403: reason = "Forbidden"
        case 404: reason = "Not Found"
        case 405: reason = "Method Not Allowed"
        case 415: reason = "Unsupported Media Type"
        default: reason = "Error"
        }
        var text = "HTTP/1.1 \(response.status) \(reason)\r\n"
        for (name, value) in headers.sorted(by: { $0.key < $1.key }) {
            text += "\(name): \(value)\r\n"
        }
        text += "\r\n"
        var data = Data(text.utf8)
        data.append(response.body)
        data.withUnsafeBytes { bytes in
            guard let base = bytes.baseAddress else { return }
            var sent = 0
            while sent < data.count {
                let count = Darwin.send(descriptor, base.advanced(by: sent), data.count - sent, 0)
                guard count > 0 else { return }
                sent += count
            }
        }
    }
}
