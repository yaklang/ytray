import Darwin
import CoreFoundation
import Foundation

final class MCPServer {
    static let port = 19_222
    static let endpoint = "http://127.0.0.1:19222/mcp"
    private static let modernVersion = "2026-07-28"
    private static let legacyVersions = ["2025-11-25", "2025-06-18", "2025-03-26"]
    private static let maxHeaderBytes = 64 * 1024
    private static let maxBodyBytes = 1024 * 1024
    private weak var store: InstanceStore?
    private var listener: Int32 = -1

    static let toolNames = [
        "list_browser_runtimes",
        "list_browser_instances",
        "open_browser_instance",
        "focus_browser_instance",
        "stop_browser_instance",
    ]

    private static let tools: [[String: Any]] = [
        [
            "name": "list_browser_runtimes",
            "description": "List browser runtimes available to YTray. Use a runtime id with open_browser_instance.",
            "inputSchema": ["type": "object", "additionalProperties": false],
            "annotations": ["readOnlyHint": true, "idempotentHint": true],
        ],
        [
            "name": "list_browser_instances",
            "description": "List YTray browser instances. Running instances are returned by default.",
            "inputSchema": [
                "type": "object", "additionalProperties": false,
                "properties": [
                    "include_history": [
                        "type": "boolean", "default": false,
                        "description": "Include stopped historical instances.",
                    ],
                ],
            ],
            "annotations": ["readOnlyHint": true, "idempotentHint": true],
        ],
        [
            "name": "open_browser_instance",
            "description": "Open a new browser instance, or restore a stopped history instance by id.",
            "inputSchema": [
                "type": "object", "additionalProperties": false,
                "properties": [
                    "instance_id": ["type": "string", "description": "Optional stopped history instance id to restore."],
                    "url": ["type": "string", "description": "Optional http, https, chrome, or about URL to open."],
                    "runtime_id": ["type": "string", "description": "Optional runtime id from list_browser_runtimes."],
                    "use_proxy": [
                        "type": "boolean", "default": false,
                        "description": "Use the proxy preset currently configured in YTray.",
                    ],
                ],
            ],
            "annotations": ["readOnlyHint": false, "destructiveHint": false, "idempotentHint": false],
        ],
        [
            "name": "focus_browser_instance",
            "description": "Bring a running YTray browser instance to the foreground.",
            "inputSchema": [
                "type": "object", "additionalProperties": false,
                "properties": ["instance_id": ["type": "string"]], "required": ["instance_id"],
            ],
            "annotations": ["readOnlyHint": false, "destructiveHint": false, "idempotentHint": true],
        ],
        [
            "name": "stop_browser_instance",
            "description": "Stop a running YTray browser instance while preserving it in history.",
            "inputSchema": [
                "type": "object", "additionalProperties": false,
                "properties": ["instance_id": ["type": "string"]], "required": ["instance_id"],
            ],
            "annotations": ["readOnlyHint": false, "destructiveHint": true, "idempotentHint": true],
        ],
    ]

    @MainActor
    init(store: InstanceStore) {
        self.store = store
    }

    deinit {
        if listener >= 0 {
            Darwin.shutdown(listener, SHUT_RDWR)
            Darwin.close(listener)
        }
    }

    @MainActor
    func start() -> Bool {
        guard let store else { return false }
        if listener >= 0 { return true }
        let fd = Darwin.socket(AF_INET, SOCK_STREAM, 0)
        guard fd >= 0 else {
            let error = POSIXError(POSIXErrorCode(rawValue: errno) ?? .ENOTSOCK)
            store.setMCPStatus(available: false, message: "启动失败：\(error.localizedDescription)")
            DiagnosticLog.error("mcp.start", error)
            return false
        }
        var reuse: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &reuse, socklen_t(MemoryLayout.size(ofValue: reuse)))
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = in_port_t(Self.port).bigEndian
        address.sin_addr = in_addr(s_addr: inet_addr("127.0.0.1"))
        let bound = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard bound == 0, Darwin.listen(fd, 16) == 0 else {
            let error = POSIXError(POSIXErrorCode(rawValue: errno) ?? .EADDRINUSE)
            Darwin.close(fd)
            store.setMCPStatus(available: false, message: "启动失败：\(error.localizedDescription)")
            DiagnosticLog.error("mcp.start", error)
            return false
        }
        listener = fd
        DispatchQueue.global(qos: .utility).async { [weak self] in
            Self.acceptLoop(listener: fd, server: { self })
        }
        store.setMCPStatus(available: true, message: "正在监听 \(Self.endpoint)")
        DiagnosticLog.info("mcp.start", "listening on \(Self.endpoint)")
        return true
    }

    @MainActor
    func stop() {
        guard listener >= 0 else {
            store?.setMCPStatus(available: false, message: "MCP 服务已停止，可随时重新启动")
            return
        }
        Darwin.shutdown(listener, SHUT_RDWR)
        Darwin.close(listener)
        listener = -1
        store?.setMCPStatus(available: false, message: "MCP 服务已停止，可随时重新启动")
        DiagnosticLog.info("mcp.stop", "listener stopped")
    }

    private static func acceptLoop(listener: Int32, server: @escaping () -> MCPServer?) {
        while true {
            var address = sockaddr()
            var length = socklen_t(MemoryLayout<sockaddr>.size)
            let client = Darwin.accept(listener, &address, &length)
            if client < 0 { return }
            guard let owner = server() else { Darwin.close(client); return }
            var noSignal: Int32 = 1
            setsockopt(client, SOL_SOCKET, SO_NOSIGPIPE, &noSignal, socklen_t(MemoryLayout.size(ofValue: noSignal)))
            var timeout = timeval(tv_sec: 10, tv_usec: 0)
            setsockopt(client, SOL_SOCKET, SO_RCVTIMEO, &timeout, socklen_t(MemoryLayout.size(ofValue: timeout)))
            setsockopt(client, SOL_SOCKET, SO_SNDTIMEO, &timeout, socklen_t(MemoryLayout.size(ofValue: timeout)))
            DispatchQueue.global(qos: .utility).async {
                handleClient(client, server: owner)
            }
        }
    }

    private static func handleClient(_ client: Int32, server: MCPServer) {
        defer { Darwin.close(client) }
        guard let request = readRequest(client) else {
            writeResponse(client, HTTPResponse(status: 400, body: jsonError(id: nil, code: -32700, message: "Invalid HTTP request")))
            return
        }
        let semaphore = DispatchSemaphore(value: 0)
        var response: HTTPResponse?
        DispatchQueue.main.async {
            response = server.handle(request)
            semaphore.signal()
        }
        semaphore.wait()
        writeResponse(client, response ?? HTTPResponse(
            status: 500,
            body: jsonError(id: nil, code: -32603, message: "Internal error")
        ))
    }

    @MainActor
    private func handle(_ request: HTTPRequest) -> HTTPResponse {
        guard listener >= 0, store != nil else {
            return HTTPResponse(status: 503, body: Self.jsonError(id: nil, code: -32603, message: "YTray MCP has stopped"))
        }
        guard request.method == "POST", request.path == "/mcp" else {
            return HTTPResponse(status: 404, body: Self.jsonError(id: nil, code: -32601, message: "MCP endpoint not found"))
        }
        guard Self.allowedHost(request.header("host")), Self.allowedOrigin(request.header("origin")) else {
            return HTTPResponse(status: 403, body: Self.jsonError(id: nil, code: -32600, message: "Forbidden host or origin"))
        }
        guard request.header("content-type")?.lowercased().hasPrefix("application/json") == true else {
            return HTTPResponse(status: 415, body: Self.jsonError(id: nil, code: -32600, message: "Content-Type must be application/json"))
        }
        guard let object = try? JSONSerialization.jsonObject(with: request.body),
              let message = object as? [String: Any] else {
            return HTTPResponse(status: 400, body: Self.jsonError(id: nil, code: -32700, message: "Parse error"))
        }
        let id = message["id"]
        guard message["jsonrpc"] as? String == "2.0",
              let method = message["method"] as? String, !method.isEmpty else {
            return HTTPResponse(status: 400, body: Self.jsonError(id: id, code: -32600, message: "Invalid Request"))
        }
        let modern = Self.isModern(message, request: request)
        if modern, let error = Self.validateModern(message, request: request, method: method) {
            return HTTPResponse(status: 400, body: Self.jsonError(
                id: id, code: error.code, message: error.message, data: error.data
            ))
        }
        guard id != nil else { return HTTPResponse(status: 202, body: nil) }

        switch method {
        case "initialize":
            let requested = (message["params"] as? [String: Any])?["protocolVersion"] as? String
            let selected = requested.flatMap { Self.legacyVersions.contains($0) ? $0 : nil }
                ?? Self.legacyVersions[0]
            return Self.result(id: id!, value: [
                "protocolVersion": selected,
                "capabilities": ["tools": ["listChanged": false]],
                "serverInfo": Self.serverInfo,
                "instructions": "Use YTray tools to open and manage local isolated browser instances.",
            ], modern: false)
        case "server/discover":
            return Self.result(id: id!, value: [
                "resultType": "complete",
                "supportedVersions": [Self.modernVersion] + Self.legacyVersions,
                "capabilities": ["tools": ["listChanged": false]],
                "_meta": Self.serverMeta,
                "instructions": "Use YTray tools to open and manage local isolated browser instances.",
                "ttlMs": 3_600_000,
                "cacheScope": "public",
            ], modern: true)
        case "ping":
            return Self.result(id: id!, value: [:], modern: modern)
        case "tools/list":
            var value: [String: Any] = ["tools": Self.tools]
            if modern {
                value["resultType"] = "complete"
                value["ttlMs"] = 3_600_000
                value["cacheScope"] = "public"
            }
            return Self.result(id: id!, value: value, modern: modern)
        case "tools/call":
            return Self.result(id: id!, value: callTool(message), modern: modern)
        default:
            return HTTPResponse(
                status: modern ? 404 : 200,
                body: Self.jsonError(id: id, code: -32601, message: "Method not found")
            )
        }
    }

    @MainActor
    private func callTool(_ message: [String: Any]) -> [String: Any] {
        guard let store else { return Self.toolError("YTray is stopping") }
        guard let parameters = message["params"] as? [String: Any],
              let name = parameters["name"] as? String else {
            return Self.toolError("params and tool name are required")
        }
        if let arguments = parameters["arguments"], !(arguments is [String: Any]) {
            return Self.toolError("arguments must be an object")
        }
        let arguments = parameters["arguments"] as? [String: Any] ?? [:]
        switch name {
        case "list_browser_runtimes":
            if let error = Self.validate(arguments, allowed: []) { return Self.toolError(error) }
            return Self.toolResult(["runtimes": store.runtimes.map(runtimeJSON)])
        case "list_browser_instances":
            if let error = Self.validate(arguments, allowed: ["include_history"]) { return Self.toolError(error) }
            guard let includeHistory = Self.optionalBool(arguments, "include_history") else {
                return Self.toolError("include_history must be a boolean")
            }
            return Self.toolResult([
                "instances": store.instances
                    .filter { includeHistory || $0.status == .running }
                    .map(Self.instanceJSON),
            ])
        case "open_browser_instance":
            if let error = Self.validate(arguments, allowed: ["instance_id", "url", "runtime_id", "use_proxy"]) {
                return Self.toolError(error)
            }
            return openBrowser(arguments)
        case "focus_browser_instance":
            if let error = Self.validate(arguments, allowed: ["instance_id"]) { return Self.toolError(error) }
            return withRunningInstance(arguments) { instance in
                store.focus(instance)
                return Self.toolResult(["instance_id": instance.id.uuidString, "status": "focused"])
            }
        case "stop_browser_instance":
            if let error = Self.validate(arguments, allowed: ["instance_id"]) { return Self.toolError(error) }
            return withRunningInstance(arguments) { instance in
                store.stop(instance)
                return Self.toolResult(["instance_id": instance.id.uuidString, "status": "stopping"])
            }
        default:
            return Self.toolError("Unknown tool: \(name)")
        }
    }

    @MainActor
    private func openBrowser(_ arguments: [String: Any]) -> [String: Any] {
        guard let store else { return Self.toolError("YTray is stopping") }
        let instanceID: String?
        switch Self.optionalString(arguments, "instance_id") {
        case .success(let value): instanceID = value
        case .failure(let error): return Self.toolError(error.message)
        }
        if let instanceID {
            guard arguments.keys.allSatisfy({ $0 == "instance_id" }) else {
                return Self.toolError("instance_id cannot be combined with new-instance options")
            }
            guard let id = UUID(uuidString: instanceID) else {
                return Self.toolError("instance_id is not a valid id")
            }
            if store.isLaunching { return Self.toolError("YTray is already starting a browser instance") }
            guard let instance = store.instances.first(where: { $0.id == id && $0.status != .running }) else {
                return Self.toolError("stopped browser instance was not found")
            }
            store.restoreHistory(instance)
            guard store.isLaunching else {
                return Self.toolError(store.errorMessage ?? "YTray could not restore the browser instance")
            }
            var value = Self.instanceJSON(store.instances.first(where: { $0.id == id }) ?? instance)
            value["launch_status"] = "starting"
            return Self.toolResult(value)
        }

        let url: String?
        switch Self.optionalString(arguments, "url") {
        case .success(let value): url = value?.trimmingCharacters(in: .whitespacesAndNewlines)
        case .failure(let error): return Self.toolError(error.message)
        }
        if let url, !Self.isAllowedStartURL(url) {
            return Self.toolError("url must use http, https, chrome, or about")
        }
        guard let useProxy = Self.optionalBool(arguments, "use_proxy") else {
            return Self.toolError("use_proxy must be a boolean")
        }
        let runtimeText: String?
        switch Self.optionalString(arguments, "runtime_id") {
        case .success(let value): runtimeText = value
        case .failure(let error): return Self.toolError(error.message)
        }
        let runtimeID = runtimeText.flatMap(UUID.init(uuidString:))
        if runtimeText != nil, runtimeID == nil { return Self.toolError("runtime_id is not a valid id") }
        if store.isLaunching { return Self.toolError("YTray is already starting a browser instance") }
        guard var configuration = store.quickLaunchConfiguration(usePresetProxy: useProxy) else {
            return Self.toolError(store.errorMessage ?? "The configured proxy is invalid")
        }
        if let runtimeID {
            guard store.runtimes.contains(where: { $0.id == runtimeID }) else {
                return Self.toolError("runtime_id was not found")
            }
            configuration.defaultRuntimeID = runtimeID
        }
        if let url { configuration.homeURL = url }
        let before = Set(store.instances.map(\.id))
        store.launch(mode: .quick, customSettings: configuration)
        guard let opened = store.instances.first(where: { !before.contains($0.id) }) else {
            return Self.toolError(store.errorMessage ?? "YTray could not start the browser instance")
        }
        var value = Self.instanceJSON(opened)
        value["launch_status"] = "starting"
        return Self.toolResult(value)
    }

    @MainActor
    private func withRunningInstance(
        _ arguments: [String: Any],
        action: (BrowserInstance) -> [String: Any]
    ) -> [String: Any] {
        guard let store else { return Self.toolError("YTray is stopping") }
        let raw: String
        switch Self.optionalString(arguments, "instance_id") {
        case .success(let value):
            guard let value else { return Self.toolError("instance_id is required") }
            raw = value
        case .failure(let error): return Self.toolError(error.message)
        }
        guard let id = UUID(uuidString: raw) else { return Self.toolError("instance_id is not a valid id") }
        guard let instance = store.instances.first(where: { $0.id == id && $0.status == .running }) else {
            return Self.toolError("running browser instance was not found")
        }
        return action(instance)
    }

    @MainActor
    private func runtimeJSON(_ runtime: BrowserRuntime) -> [String: Any] {
        [
            "id": runtime.id.uuidString,
            "name": runtime.displayTitle,
            "version": runtime.versionLabel,
            "kind": runtime.kind.rawValue,
            "source": runtime.source.rawValue,
            "default": runtime.id == store?.defaultRuntime?.id,
        ]
    }

    private static func instanceJSON(_ instance: BrowserInstance) -> [String: Any] {
        var value: [String: Any] = [
            "id": instance.id.uuidString,
            "name": instance.name,
            "runtime": instance.runtimeName,
            "runtime_id": instance.runtimeID.uuidString,
            "status": instance.status.rawValue,
            "pid": instance.processID,
            "debug_port": instance.debugPort,
            "start_url": instance.startURL,
            "started_at": ISO8601DateFormatter().string(from: instance.startedAt),
        ]
        value["page_title"] = instance.lastPageTitle
        value["page_url"] = instance.lastPageURL
        value["dock_badge"] = instance.dockBadge
        return value
    }

    static func isAllowedStartURL(_ value: String) -> Bool {
        guard value.utf8.count <= 2048,
              let components = URLComponents(string: value),
              let scheme = components.scheme?.lowercased(),
              ["http", "https", "chrome", "about"].contains(scheme) else { return false }
        return !["http", "https"].contains(scheme) || !(components.host ?? "").isEmpty
    }

    private static func validate(_ arguments: [String: Any], allowed: Set<String>) -> String? {
        arguments.keys.first(where: { !allowed.contains($0) }).map { "unexpected argument: \($0)" }
    }

    private static func optionalBool(_ arguments: [String: Any], _ name: String) -> Bool? {
        guard let value = arguments[name] else { return false }
        guard let number = value as? NSNumber,
              CFGetTypeID(number) == CFBooleanGetTypeID() else { return nil }
        return number.boolValue
    }

    private enum StringResult {
        case success(String?)
        case failure(StringError)
    }

    private struct StringError { let message: String }

    private static func optionalString(_ arguments: [String: Any], _ name: String) -> StringResult {
        guard let value = arguments[name] else { return .success(nil) }
        guard let text = value as? String, !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return .failure(StringError(message: "\(name) must be a non-empty string"))
        }
        return .success(text)
    }

    private static func toolResult(_ value: [String: Any]) -> [String: Any] {
        let text = (try? JSONSerialization.data(withJSONObject: value))
            .flatMap { String(data: $0, encoding: .utf8) } ?? "{}"
        return [
            "resultType": "complete",
            "content": [["type": "text", "text": text]],
            "structuredContent": value,
            "isError": false,
        ]
    }

    private static func toolError(_ message: String) -> [String: Any] {
        [
            "resultType": "complete",
            "content": [["type": "text", "text": message]],
            "isError": true,
        ]
    }

    private static var serverInfo: [String: Any] {
        [
            "name": "ytray",
            "version": Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "development",
        ]
    }

    private static var serverMeta: [String: Any] {
        ["io.modelcontextprotocol/serverInfo": serverInfo]
    }

    private static func result(id: Any, value original: [String: Any], modern: Bool) -> HTTPResponse {
        var value = original
        if modern {
            if value["resultType"] == nil { value["resultType"] = "complete" }
            if value["_meta"] == nil { value["_meta"] = serverMeta }
        }
        return HTTPResponse(status: 200, body: ["jsonrpc": "2.0", "id": id, "result": value])
    }

    private static func jsonError(
        id: Any?, code: Int, message: String, data: Any? = nil
    ) -> [String: Any] {
        var error: [String: Any] = ["code": code, "message": message]
        if let data { error["data"] = data }
        return ["jsonrpc": "2.0", "id": id ?? NSNull(), "error": error]
    }

    private static func isModern(_ message: [String: Any], request: HTTPRequest) -> Bool {
        let parameters = message["params"] as? [String: Any]
        let metadata = parameters?["_meta"] as? [String: Any]
        return metadata?["io.modelcontextprotocol/protocolVersion"] != nil
            || request.header("mcp-protocol-version") == modernVersion
    }

    private static func validateModern(
        _ message: [String: Any], request: HTTPRequest, method: String
    ) -> (code: Int, message: String, data: Any?)? {
        let parameters = message["params"] as? [String: Any]
        let metadata = parameters?["_meta"] as? [String: Any]
        let bodyVersion = metadata?["io.modelcontextprotocol/protocolVersion"] as? String
        let headerVersion = request.header("mcp-protocol-version")
        if bodyVersion != headerVersion {
            return (-32020, "MCP request headers do not match the body", nil)
        }
        guard bodyVersion == modernVersion else {
            return (-32022, "Unsupported protocol version", [
                "supported": [modernVersion] + legacyVersions,
                "requested": bodyVersion ?? headerVersion ?? "missing",
            ])
        }
        guard metadata?["io.modelcontextprotocol/clientCapabilities"] is [String: Any] else {
            return (-32602, "Modern MCP requests require clientCapabilities metadata", nil)
        }
        guard request.header("mcp-method") == method else {
            return (-32020, "Mcp-Method header does not match the body", nil)
        }
        if method == "tools/call" {
            let bodyName = parameters?["name"] as? String
            guard let bodyName, decodeHeader(request.header("mcp-name")) == bodyName else {
                return (-32020, "Mcp-Name header does not match the tool name", nil)
            }
        }
        return nil
    }

    private static func decodeHeader(_ value: String?) -> String? {
        guard let value, value.hasPrefix("=?base64?"), value.hasSuffix("?=") else { return value }
        let start = value.index(value.startIndex, offsetBy: 9)
        let end = value.index(value.endIndex, offsetBy: -2)
        guard let data = Data(base64Encoded: String(value[start..<end])) else { return nil }
        return String(data: data, encoding: .utf8)
    }

    private static func allowedHost(_ value: String?) -> Bool {
        guard let value = value?.lowercased() else { return false }
        return value == "127.0.0.1:\(port)" || value == "localhost:\(port)"
    }

    private static func allowedOrigin(_ value: String?) -> Bool {
        guard let value = value?.lowercased() else { return true }
        return value == "http://127.0.0.1:\(port)" || value == "http://localhost:\(port)"
    }

    private static func readRequest(_ fd: Int32) -> HTTPRequest? {
        var data = Data()
        let marker = Data([13, 10, 13, 10])
        var headerRange: Range<Data.Index>?
        while headerRange == nil {
            guard receive(fd, into: &data, maximum: maxHeaderBytes + maxBodyBytes) else { return nil }
            headerRange = data.range(of: marker)
            if headerRange == nil, data.count > maxHeaderBytes { return nil }
        }
        guard let range = headerRange,
              let header = String(data: data[..<range.lowerBound], encoding: .utf8) else { return nil }
        let lines = header.components(separatedBy: "\r\n")
        let requestLine = lines[0].split(separator: " ")
        guard requestLine.count == 3 else { return nil }
        var headers: [String: String] = [:]
        for line in lines.dropFirst() {
            guard let colon = line.firstIndex(of: ":") else { return nil }
            let name = line[..<colon].trimmingCharacters(in: .whitespaces).lowercased()
            let value = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
            headers[name] = value
        }
        guard let rawLength = headers["content-length"], let length = Int(rawLength),
              (0...maxBodyBytes).contains(length) else { return nil }
        let bodyStart = range.upperBound
        while data.count - bodyStart < length {
            guard receive(fd, into: &data, maximum: maxHeaderBytes + maxBodyBytes) else { return nil }
        }
        return HTTPRequest(
            method: String(requestLine[0]),
            path: String(requestLine[1]),
            headers: headers,
            body: Data(data[bodyStart..<(bodyStart + length)])
        )
    }

    private static func receive(_ fd: Int32, into data: inout Data, maximum: Int) -> Bool {
        var buffer = [UInt8](repeating: 0, count: 4096)
        let capacity = buffer.count
        let count = buffer.withUnsafeMutableBytes { Darwin.recv(fd, $0.baseAddress, capacity, 0) }
        guard count > 0, data.count + count <= maximum else { return false }
        data.append(contentsOf: buffer.prefix(count))
        return true
    }

    private static func writeResponse(_ fd: Int32, _ response: HTTPResponse) {
        let payload = response.body.flatMap { try? JSONSerialization.data(withJSONObject: $0) } ?? Data()
        let reason: String
        switch response.status {
        case 200: reason = "OK"
        case 202: reason = "Accepted"
        case 400: reason = "Bad Request"
        case 403: reason = "Forbidden"
        case 404: reason = "Not Found"
        case 415: reason = "Unsupported Media Type"
        default: reason = "Internal Server Error"
        }
        let header = "HTTP/1.1 \(response.status) \(reason)\r\n"
            + "Content-Type: application/json; charset=utf-8\r\n"
            + "Content-Length: \(payload.count)\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n"
        send(fd, Data(header.utf8) + payload)
    }

    private static func send(_ fd: Int32, _ data: Data) {
        data.withUnsafeBytes { bytes in
            guard let base = bytes.baseAddress else { return }
            var sent = 0
            while sent < bytes.count {
                let count = Darwin.send(fd, base.advanced(by: sent), bytes.count - sent, 0)
                if count <= 0 { return }
                sent += count
            }
        }
    }

    private struct HTTPRequest {
        let method: String
        let path: String
        let headers: [String: String]
        let body: Data
        func header(_ name: String) -> String? { headers[name.lowercased()] }
    }

    private struct HTTPResponse {
        let status: Int
        let body: [String: Any]?
    }
}
