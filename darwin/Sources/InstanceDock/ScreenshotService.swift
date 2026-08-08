import Foundation
import Darwin

enum ScreenshotService {
    private struct Target: Decodable {
        let type: String
        let title: String?
        let url: String?
        let webSocketDebuggerUrl: String?
    }

    struct PageState: Equatable {
        let title: String
        let url: String
    }

    static func currentPageTitle(debugPort: Int, attempts: Int = 1) async -> String? {
        await currentPageState(debugPort: debugPort, attempts: attempts)?.title
    }

    static func currentPageState(debugPort: Int, attempts: Int = 1) async -> PageState? {
        for attempt in 0..<max(attempts, 1) {
            if let targets = try? await pageTargets(debugPort: debugPort) {
                if let visible = await visiblePage(in: targets)?.state { return visible }
                if let target = targets.first(where: { $0.type == "page" }) {
                    let title = (target.title ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
                    let url = (target.url ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
                    if !title.isEmpty || !url.isEmpty { return PageState(title: title, url: url) }
                }
            }
            if attempt + 1 < attempts {
                try? await Task.sleep(nanoseconds: 150_000_000)
            }
        }
        return nil
    }

    static func waitUntilReady(debugPort: Int, attempts: Int = 60) async -> Bool {
        for attempt in 0..<max(attempts, 1) {
            if (try? await pageTargets(debugPort: debugPort)) != nil { return true }
            if attempt + 1 < attempts {
                try? await Task.sleep(nanoseconds: 250_000_000)
            }
        }
        return false
    }

    static func navigate(debugPort: Int, to url: String) async throws {
        guard let parsedURL = URL(string: url), parsedURL.scheme != nil else {
            throw InstanceDockError.invalidURL(url)
        }
        let targets = try await pageTargets(debugPort: debugPort)
        let target = await visiblePage(in: targets)?.target
            ?? targets.first(where: { $0.type == "page" && $0.webSocketDebuggerUrl != nil })
        guard let value = target?.webSocketDebuggerUrl, let socketURL = URL(string: value) else {
            throw InstanceDockError.launchFailed("找不到可恢复的浏览器标签页")
        }
        let command: [String: Any] = [
            "id": 1,
            "method": "Page.navigate",
            "params": ["url": parsedURL.absoluteString],
        ]
        let requestData = try JSONSerialization.data(withJSONObject: command)
        let messages = try await RawWebSocket.exchange(url: socketURL, message: requestData)
        for data in messages {
            guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  (json["id"] as? Int) == 1 else { continue }
            if let error = json["error"] as? [String: Any] {
                throw InstanceDockError.launchFailed(error["message"] as? String ?? "恢复页面失败")
            }
            return
        }
        throw InstanceDockError.launchFailed("浏览器没有确认恢复页面")
    }

    private static func visiblePage(in targets: [Target]) async -> (target: Target, state: PageState)? {
        for target in targets where target.type == "page" {
            guard let value = target.webSocketDebuggerUrl,
                  let socketURL = URL(string: value),
                  let state = try? await evaluateVisiblePageState(socketURL: socketURL) else { continue }
            return (target, state)
        }
        return nil
    }

    private static func evaluateVisiblePageState(socketURL: URL) async throws -> PageState? {
        let command: [String: Any] = [
            "id": 1,
            "method": "Runtime.evaluate",
            "params": [
                "expression": "document.visibilityState === 'visible' ? JSON.stringify({title: document.title, url: location.href}) : ''",
                "returnByValue": true,
            ],
        ]
        let requestData = try JSONSerialization.data(withJSONObject: command)
        let messages = try await RawWebSocket.exchange(url: socketURL, message: requestData)
        for data in messages {
            guard let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                  (json["id"] as? Int) == 1,
                  let result = json["result"] as? [String: Any],
                  let remoteObject = result["result"] as? [String: Any],
                  let value = remoteObject["value"] as? String else { continue }
            guard !value.isEmpty,
                  let data = value.data(using: .utf8),
                  let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
                return nil
            }
            return PageState(
                title: (object["title"] as? String ?? "").trimmingCharacters(in: .whitespacesAndNewlines),
                url: (object["url"] as? String ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
            )
        }
        return nil
    }

    static func capture(debugPort: Int, instanceID: UUID, outputDirectory: URL) async throws -> URL {
        var lastError = "调试端口尚未就绪"
        for _ in 0..<20 {
            do {
                let targets = try await pageTargets(debugPort: debugPort)
                guard let value = targets.first(where: { $0.type == "page" })?.webSocketDebuggerUrl,
                      let socketURL = URL(string: value) else {
                    throw InstanceDockError.screenshotFailed("没有可截图的页面")
                }
                return try await capture(socketURL: socketURL, instanceID: instanceID, outputDirectory: outputDirectory)
            } catch {
                lastError = error.localizedDescription
                try await Task.sleep(nanoseconds: 250_000_000)
            }
        }
        throw InstanceDockError.screenshotFailed(lastError)
    }

    private static func pageTargets(debugPort: Int) async throws -> [Target] {
        guard (1...65_535).contains(debugPort),
              let endpoint = URL(string: "http://127.0.0.1:\(debugPort)/json/list") else {
            throw URLError(.badURL)
        }
        var request = URLRequest(url: endpoint)
        request.timeoutInterval = 0.8
        let (data, response) = try await URLSession.shared.data(for: request)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else {
            throw URLError(.badServerResponse)
        }
        return try JSONDecoder().decode([Target].self, from: data)
    }

    private static func capture(socketURL: URL, instanceID: UUID, outputDirectory: URL) async throws -> URL {
        let command: [String: Any] = [
            "id": 1, "method": "Page.captureScreenshot",
            "params": ["format": "png", "captureBeyondViewport": false],
        ]
        let requestData = try JSONSerialization.data(withJSONObject: command)
        let messages = try await RawWebSocket.exchange(url: socketURL, message: requestData)
        for data in messages {
            guard let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                  (json["id"] as? Int) == 1 else { continue }
            if let error = json["error"] as? [String: Any] {
                throw InstanceDockError.screenshotFailed(error["message"] as? String ?? "CDP 返回错误")
            }
            guard let result = json["result"] as? [String: Any],
                  let encoded = result["data"] as? String,
                  let image = Data(base64Encoded: encoded) else {
                throw InstanceDockError.screenshotFailed("CDP 没有返回图片")
            }
            try FileManager.default.createDirectory(at: outputDirectory, withIntermediateDirectories: true)
            let formatter = DateFormatter()
            formatter.dateFormat = "yyyyMMdd-HHmmss"
            let url = outputDirectory.appendingPathComponent("\(formatter.string(from: Date()))-\(instanceID.uuidString.prefix(8)).png")
            try image.write(to: url, options: .atomic)
            return url
        }
        throw InstanceDockError.screenshotFailed("等待 CDP 截图响应超时")
    }
}

private enum RawWebSocket {
    static func exchange(url: URL, message: Data) async throws -> [Data] {
        try await withCheckedThrowingContinuation { continuation in
            DispatchQueue.global(qos: .userInitiated).async {
                do { continuation.resume(returning: try blockingExchange(url: url, message: message)) }
                catch { continuation.resume(throwing: error) }
            }
        }
    }

    private static func blockingExchange(url: URL, message: Data) throws -> [Data] {
        guard url.scheme == "ws", url.host == "127.0.0.1", let port = url.port else {
            throw InstanceDockError.screenshotFailed("只允许连接本机 ws://127.0.0.1 调试端点")
        }
        let descriptor = socket(AF_INET, SOCK_STREAM, 0)
        guard descriptor >= 0 else { throw InstanceDockError.screenshotFailed("无法创建 CDP socket") }
        defer { close(descriptor) }
        var noPipe: Int32 = 1
        setsockopt(descriptor, SOL_SOCKET, SO_NOSIGPIPE, &noPipe, socklen_t(MemoryLayout.size(ofValue: noPipe)))
        var timeout = timeval(tv_sec: 10, tv_usec: 0)
        setsockopt(descriptor, SOL_SOCKET, SO_RCVTIMEO, &timeout, socklen_t(MemoryLayout.size(ofValue: timeout)))

        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = in_port_t(port).bigEndian
        address.sin_addr = in_addr(s_addr: inet_addr("127.0.0.1"))
        let connected = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.connect(descriptor, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard connected == 0 else { throw InstanceDockError.screenshotFailed("CDP WebSocket 连接失败") }

        let key = Data((0..<16).map { _ in UInt8.random(in: 0...255) }).base64EncodedString()
        let path = url.path + (url.query.map { "?\($0)" } ?? "")
        let handshake = "GET \(path) HTTP/1.1\r\n"
            + "Host: 127.0.0.1:\(port)\r\n"
            + "Upgrade: websocket\r\nConnection: Upgrade\r\n"
            + "Sec-WebSocket-Key: \(key)\r\nSec-WebSocket-Version: 13\r\n"
            + "Origin: http://127.0.0.1:\(port)\r\n\r\n"
        try sendAll(Data(handshake.utf8), descriptor: descriptor)

        var buffer = Data()
        while buffer.range(of: Data("\r\n\r\n".utf8)) == nil {
            try receiveMore(into: &buffer, descriptor: descriptor)
            guard buffer.count < 64 * 1024 else {
                throw InstanceDockError.screenshotFailed("CDP WebSocket 握手响应过大")
            }
        }
        guard let headerEnd = buffer.range(of: Data("\r\n\r\n".utf8)) else {
            throw InstanceDockError.screenshotFailed("CDP WebSocket 握手不完整")
        }
        let header = String(decoding: buffer[..<headerEnd.upperBound], as: UTF8.self)
        guard header.hasPrefix("HTTP/1.1 101") else {
            throw InstanceDockError.screenshotFailed("CDP WebSocket 握手被拒绝：\(header.components(separatedBy: .newlines).first ?? header)")
        }
        buffer.removeSubrange(..<headerEnd.upperBound)
        try sendAll(clientFrame(opcode: 0x1, payload: message), descriptor: descriptor)

        var messages: [Data] = []
        var fragmented = Data()
        var fragmentedOpcode: UInt8?
        while messages.count < 20 {
            guard let frame = try nextFrame(buffer: &buffer, descriptor: descriptor) else { break }
            switch frame.opcode {
            case 0x1, 0x2:
                if frame.finished { messages.append(frame.payload) }
                else { fragmented = frame.payload; fragmentedOpcode = frame.opcode }
            case 0x0:
                guard fragmentedOpcode != nil else { continue }
                fragmented.append(frame.payload)
                if frame.finished { messages.append(fragmented); fragmented.removeAll(); fragmentedOpcode = nil }
            case 0x8:
                return messages
            case 0x9:
                try sendAll(clientFrame(opcode: 0xA, payload: frame.payload), descriptor: descriptor)
            default:
                continue
            }
            if messages.contains(where: { data in
                guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return false }
                return (json["id"] as? Int) == 1
            }) { return messages }
        }
        return messages
    }

    private struct Frame {
        let finished: Bool
        let opcode: UInt8
        let payload: Data
    }

    private static func nextFrame(buffer: inout Data, descriptor: Int32) throws -> Frame? {
        while buffer.count < 2 { try receiveMore(into: &buffer, descriptor: descriptor) }
        let first = buffer[buffer.startIndex]
        let second = buffer[buffer.index(after: buffer.startIndex)]
        var offset = 2
        var length = UInt64(second & 0x7F)
        if length == 126 {
            while buffer.count < 4 { try receiveMore(into: &buffer, descriptor: descriptor) }
            length = UInt64(buffer[2]) << 8 | UInt64(buffer[3])
            offset = 4
        } else if length == 127 {
            while buffer.count < 10 { try receiveMore(into: &buffer, descriptor: descriptor) }
            length = 0
            for byte in buffer[2..<10] { length = (length << 8) | UInt64(byte) }
            offset = 10
        }
        guard length <= 64 * 1024 * 1024 else {
            throw InstanceDockError.screenshotFailed("CDP 截图响应超过 64 MB")
        }
        let masked = second & 0x80 != 0
        let maskLength = masked ? 4 : 0
        let needed = offset + maskLength + Int(length)
        while buffer.count < needed { try receiveMore(into: &buffer, descriptor: descriptor) }
        var payload = Data(buffer[(offset + maskLength)..<needed])
        if masked {
            let mask = Array(buffer[offset..<(offset + 4)])
            for index in payload.indices { payload[index] ^= mask[payload.distance(from: payload.startIndex, to: index) % 4] }
        }
        buffer.removeSubrange(..<needed)
        return Frame(finished: first & 0x80 != 0, opcode: first & 0x0F, payload: payload)
    }

    private static func clientFrame(opcode: UInt8, payload: Data) -> Data {
        var frame = Data([0x80 | opcode])
        let count = payload.count
        if count < 126 {
            frame.append(UInt8(0x80 | count))
        } else if count <= 65_535 {
            frame.append(0x80 | 126)
            frame.append(UInt8((count >> 8) & 0xFF)); frame.append(UInt8(count & 0xFF))
        } else {
            frame.append(0x80 | 127)
            for shift in stride(from: 56, through: 0, by: -8) { frame.append(UInt8((UInt64(count) >> UInt64(shift)) & 0xFF)) }
        }
        let mask = (0..<4).map { _ in UInt8.random(in: 0...255) }
        frame.append(contentsOf: mask)
        for (index, byte) in payload.enumerated() { frame.append(byte ^ mask[index % 4]) }
        return frame
    }

    private static func sendAll(_ data: Data, descriptor: Int32) throws {
        var sent = 0
        try data.withUnsafeBytes { bytes in
            guard let base = bytes.baseAddress else { return }
            while sent < data.count {
                let count = Darwin.send(descriptor, base.advanced(by: sent), data.count - sent, 0)
                guard count > 0 else { throw InstanceDockError.screenshotFailed("CDP WebSocket 写入失败") }
                sent += count
            }
        }
    }

    private static func receiveMore(into buffer: inout Data, descriptor: Int32) throws {
        var bytes = [UInt8](repeating: 0, count: 64 * 1024)
        let count = Darwin.recv(descriptor, &bytes, bytes.count, 0)
        guard count > 0 else { throw InstanceDockError.screenshotFailed("CDP WebSocket 读取失败或超时") }
        buffer.append(contentsOf: bytes.prefix(count))
    }
}
