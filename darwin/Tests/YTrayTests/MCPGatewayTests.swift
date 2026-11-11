import Darwin
import XCTest
@testable import YTray

@MainActor
final class MCPGatewayTests: XCTestCase {
    func testAcceptedRequestCannotOutliveGatewayStopOrRestart() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("ytray-mcp-tests-\(UUID())")
        let suite = "io.yaklang.ytray.gateway.tests.\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defaults.set(false, forKey: YTrayMCPController.enabledKey)
        let store = InstanceStore(applicationDirectory: directory, discoverSystemBrowsers: false)
        let controller = YTrayMCPController(store: store, defaults: defaults)
        defer { controller.stop(); defaults.removePersistentDomain(forName: suite); try? FileManager.default.removeItem(at: directory) }
        let port = BrowserLauncher.nextAvailablePort(startingAt: Int.random(in: 52000...58000))
        controller.draftPort = String(port)
        XCTAssertTrue(controller.applyPort())
        let configuration = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(controller.configurationJSON.utf8)) as? [String: Any])
        let servers = try XCTUnwrap(configuration["mcpServers"] as? [String: [String: Any]])
        let authorization = try XCTUnwrap((servers["ytray"]?["headers"] as? [String: String])?["Authorization"])
        for restart in [false, true] {
            controller.setEnabled(true)
            XCTAssertEqual(controller.phase, .running)
            let response = try await Self.partialRequest(port: port, authorization: authorization) {
                await MainActor.run {
                    controller.setEnabled(false)
                    if restart { controller.setEnabled(true) }
                }
            }
            XCTAssertTrue(response.isEmpty || response.hasPrefix("HTTP/1.1 503"), "Old connection admitted after stop/restart: \(response)")
            controller.setEnabled(false)
        }
    }

    func testBrowserToolsRejectInvalidArgumentsBeforeSelectingOrAccessingABrowser() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("ytray-mcp-tests-\(UUID())")
        defer { try? FileManager.default.removeItem(at: directory) }
        let store = InstanceStore(applicationDirectory: directory, discoverSystemBrowsers: false)
        let router = YTrayMCPRouter(store: store)
        let cases: [(String, Any, String)] = [
            ("list_instances", ["ignored": true], "arguments"),
            ("list_instances", "not-an-object", "arguments"),
            ("get_cdp_info", ["instance_id": 12], "instance_id"),
            ("evaluate_javascript", ["instance_id": false, "expression": "document.title"], "instance_id"),
            ("evaluate_javascript", ["expression": 12], "expression"),
            ("get_page_content", ["max_chars": "1000"], "max_chars"),
            ("get_page_content", ["max_chars": true], "max_chars"),
            ("get_page_content", ["max_chars": 1000.5], "max_chars"),
            ("get_page_content", ["include_html": "false"], "include_html")
        ]
        for (name, arguments, field) in cases {
            let rpc: [String: Any] = ["jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": ["name": name, "arguments": arguments]]
            let response = await router.handle(body: try JSONSerialization.data(withJSONObject: rpc))
            let payload = try XCTUnwrap(JSONSerialization.jsonObject(with: response.body) as? [String: Any])
            let result = try XCTUnwrap(payload["result"] as? [String: Any])
            XCTAssertEqual(result["isError"] as? Bool, true, name)
            let message = SystemControlController.text(result)
            XCTAssertTrue(message.contains(field), message)
            XCTAssertFalse(message.contains("没有 YTray 浏览器"), "Invalid input must fail before implicit browser selection")
        }
        let valid: [String: Any] = ["jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": ["name": "list_instances"]]
        let response = await router.handle(body: try JSONSerialization.data(withJSONObject: valid))
        let payload = try XCTUnwrap(JSONSerialization.jsonObject(with: response.body) as? [String: Any])
        XCTAssertEqual((payload["result"] as? [String: Any])?["isError"] as? Bool, false)
    }

    nonisolated private static func partialRequest(port: Int, authorization: String,
                                                   whileReading: @escaping @Sendable () async -> Void) async throws -> String {
        let descriptor = socket(AF_INET, SOCK_STREAM, 0)
        guard descriptor >= 0 else { throw URLError(.cannotConnectToHost) }
        defer { Darwin.close(descriptor) }
        var noPipe: Int32 = 1
        setsockopt(descriptor, SOL_SOCKET, SO_NOSIGPIPE, &noPipe, socklen_t(MemoryLayout.size(ofValue: noPipe)))
        var timeout = timeval(tv_sec: 3, tv_usec: 0)
        setsockopt(descriptor, SOL_SOCKET, SO_RCVTIMEO, &timeout, socklen_t(MemoryLayout.size(ofValue: timeout)))
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = in_port_t(port).bigEndian
        address.sin_addr = in_addr(s_addr: inet_addr("127.0.0.1"))
        let connected = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { Darwin.connect(descriptor, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) }
        }
        guard connected == 0 else { throw URLError(.cannotConnectToHost) }
        let body = Data("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"list_instances\"}}".utf8)
        let header = Data("POST /mcp HTTP/1.1\r\nHost: 127.0.0.1:\(port)\r\nAuthorization: \(authorization)\r\nContent-Type: application/json\r\nContent-Length: \(body.count)\r\n\r\n".utf8)
        _ = header.withUnsafeBytes { Darwin.send(descriptor, $0.baseAddress, $0.count, 0) }
        // Let the accept worker begin waiting for the declared body. A closed
        // connection is also a valid refusal if the stop wins the accept race.
        try await Task.sleep(nanoseconds: 100_000_000)
        await whileReading()
        let sent = body.withUnsafeBytes { Darwin.send(descriptor, $0.baseAddress, $0.count, 0) }
        if sent < 0 { return "" }
        var data = Data(), buffer = [UInt8](repeating: 0, count: 4096)
        while true {
            let count = Darwin.recv(descriptor, &buffer, buffer.count, 0)
            guard count > 0 else { break }
            data.append(contentsOf: buffer.prefix(count))
        }
        return String(decoding: data, as: UTF8.self)
    }
}
