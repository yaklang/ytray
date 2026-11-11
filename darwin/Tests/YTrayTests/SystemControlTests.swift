import XCTest
@testable import YTray

@MainActor
final class SystemControlTests: XCTestCase {
    func testOriginRequiresExactSafeOrigin() throws {
        XCTAssertNil(try SystemControlController.normalizedOrigin(""))
        XCTAssertEqual(try SystemControlController.normalizedOrigin("https://EXAMPLE.com:443/"), "https://example.com")
        XCTAssertEqual(try SystemControlController.normalizedOrigin("http://localhost:3000"), "http://localhost:3000")
        for origin in ["null", "*", "https://example.com/a", "https://user@example.com", "http://example.com", "https://example.com?q=1", "https://example.com#foo"] {
            XCTAssertThrowsError(try SystemControlController.normalizedOrigin(origin), origin)
        }
    }

    func testActionErrorSummaryDoesNotSpillObservationIntoMainPage() {
        let result: [String: Any] = ["isError": true,
            "content": [["type": "text", "text": "focus_unverified: 窗口未获得焦点\n[1] AXTextField private input\n[2] AXButton"]],
            "structuredContent": ["error_code": "focus_unverified", "message": "窗口未获得焦点"]]
        XCTAssertEqual(SystemControlController.errorSummary(result), "focus_unverified: 窗口未获得焦点")
        XCTAssertFalse(SystemControlController.errorSummary(result).contains("private input"))
        XCTAssertEqual(SystemControlController.errorSummary(["content": [["type": "text", "text": "Legacy failure\nAX tree"]]]), "Legacy failure")
    }

    func testSessionRequiresNativeStartAndRevokesBearerOnStopAndRestart() async throws {
        let (controller, fake, defaults) = try fixture()
        defer { controller.stop(); defaults.removePersistentDomain(forName: fake.suite) }
        let http0 = await controller.handleHTTP(request(), port: 51373)
        XCTAssertEqual(http0.status, 401)
        await controller.start()
        XCTAssertEqual(controller.phase, .running, controller.lastError)
        XCTAssertEqual(fake.starts, 1)
        let first = try bearer(controller)
        let http1 = await controller.handleHTTP(request(token: first), port: 51373)
        XCTAssertEqual(http1.status, 200)
        controller.stop()
        let http2 = await controller.handleHTTP(request(token: first), port: 51373)
        XCTAssertEqual(http2.status, 401)
        await controller.start()
        XCTAssertEqual(controller.phase, .running, controller.lastError)
        XCTAssertNotEqual(try bearer(controller), first)
        let http3 = await controller.handleHTTP(request(token: first), port: 51373)
        XCTAssertEqual(http3.status, 401)
    }

    func testWebOriginHostPreflightAndSeparateCredentialBoundary() async throws {
        let (controller, fake, defaults) = try fixture()
        defer { controller.stop(); defaults.removePersistentDomain(forName: fake.suite) }
        controller.allowedOriginDraft = "https://console.example:8443"
        await controller.start()
        let token = try bearer(controller)
        for origin in ["null", "https://console.example", "http://console.example:8443", "https://console.example:8444", "https://evil.example"] {
            let http4 = await controller.handleHTTP(request(token: token, origin: origin), port: 51373)
            XCTAssertEqual(http4.status, 403)
        }
        let accepted = await controller.handleHTTP(request(token: token, origin: "https://console.example:8443"), port: 51373)
        XCTAssertEqual(accepted.status, 200)
        XCTAssertEqual(accepted.headers["Access-Control-Allow-Origin"], "https://console.example:8443")
        let http5 = await controller.handleHTTP(request(token: token, host: "evil.example:51373"), port: 51373)
        XCTAssertEqual(http5.status, 403)
        let http6 = await controller.handleHTTP(request(token: "unrelated-browser-mcp-token"), port: 51373)
        XCTAssertEqual(http6.status, 401)
        let preflight = await controller.handleHTTP(request(method: "OPTIONS", origin: "https://console.example:8443"), port: 51373)
        XCTAssertEqual(preflight.status, 200)
        XCTAssertTrue(preflight.headers["Access-Control-Allow-Headers"]?.contains("Authorization") == true)
        controller.stop()
        let http7 = await controller.handleHTTP(request(method: "OPTIONS", origin: "https://console.example:8443"), port: 51373)
        XCTAssertEqual(http7.status, 401)
    }

    func testWhitelistBlocksCommandsFilesSessionsAndImplicitForeground() async throws {
        let (controller, fake, defaults) = try fixture()
        defer { controller.stop(); defaults.removePersistentDomain(forName: fake.suite) }
        await controller.start()
        for name in ["run_script", "run_steps", "launch_app", "execute", "evaluate_javascript", "set_settings"] {
            do { _ = try await controller.perform(name); XCTFail(name) } catch { }
        }
        for args: [String: Any] in [["screenshot_out_file": "/tmp/x"], ["session": "other"]] {
            do { _ = try await controller.perform("get_window_state", arguments: args); XCTFail() } catch { }
        }
        let target: [String: Any] = ["kind": "window", "pid": 123, "window_id": 1]
        _ = try await controller.perform("click", arguments: ["target": target, "element_token": "token"])
        XCTAssertEqual(fake.calls.last?.1["delivery_mode"] as? String, "background")
        for invalid: [String: Any] in [
            ["target": ["kind": "window"], "element_token": "token"],
            ["target": ["kind": "desktop", "display_id": "primary", "pid": 1], "x": 1, "y": 2],
            ["pid": true, "window_id": 1, "text": "hello"],
            ["target": target, "element_token": "token", "delivery_mode": NSNull()]
        ] {
            do { _ = try await controller.perform("click", arguments: invalid); XCTFail("Ambiguous action admitted") } catch { }
        }
        let before = fake.calls.count
        do {
            _ = try await controller.perform("click", arguments: ["target": target, "element_token": "token", "delivery_mode": "foreground"])
            XCTFail("Foreground must require local authorization")
        } catch { }
        XCTAssertEqual(fake.calls.count, before)
        do { _ = try await controller.perform("type_text", arguments: ["text": "hello"]); XCTFail("Missing exact target") } catch { }
        let response = await controller.handleHTTP(request(token: try bearer(controller), tool: "tools/list"), port: 51373)
        let text = String(decoding: response.body, as: UTF8.self)
        XCTAssertFalse(text.contains("screenshot_out_file"))
        XCTAssertTrue(text.contains("launch_app"))
    }

    func testCompactWorkflowOptionsKeepTheNativeForegroundBoundary() async throws {
        let (controller, fake, defaults) = try fixture()
        defer { controller.stop(); defaults.removePersistentDomain(forName: fake.suite) }
        await controller.start()
        _ = try await controller.perform("type_text", arguments: ["window_id": 1, "element_index": 7,
            "snapshot_id": "latest", "text": "hello", "replace": true, "submit": true, "return_state": true])
        XCTAssertEqual(fake.calls.last?.1["delivery_mode"] as? String, "background")
        XCTAssertEqual(fake.calls.last?.1["element_index"] as? Int, 7)
        let count = fake.calls.count
        do {
            _ = try await controller.perform("type_text", arguments: ["window_id": 1, "text": "hello", "submit": true, "delivery_mode": "foreground"])
            XCTFail("Combined input bypassed foreground authorization")
        } catch { }
        XCTAssertEqual(fake.calls.count, count)
        _ = try await controller.perform("get_window_state", arguments: ["window_id": 1, "detail": "compact",
            "region": ["x": 0, "y": 0, "width": 200, "height": 100]])
        XCTAssertNotNil(fake.calls.last?.1["region"])
    }

    func testBackendErrorsPermissionsAndExplicitForegroundArePreserved() async throws {
        let (controller, fake, defaults) = try fixture()
        defer { controller.stop(); defaults.removePersistentDomain(forName: fake.suite) }
        controller.allowForegroundDraft = true
        fake.accessibility = false
        await controller.start()
        XCTAssertEqual(controller.statusText, "等待辅助功能授权")
        fake.result = ["isError": true, "content": [["type": "text", "text": "background_unavailable: exact reason"]], "structuredContent": ["error_code": "background_unavailable", "degraded": true]]
        let result = try await controller.perform("click", arguments: ["target": ["kind": "desktop", "display_id": "primary"], "x": 1, "y": 2, "delivery_mode": "foreground"])
        XCTAssertTrue(result["isError"] as? Bool == true)
        XCTAssertEqual((result["structuredContent"] as? [String: Any])?["error_code"] as? String, "background_unavailable")
        XCTAssertEqual(fake.calls.last?.1["delivery_mode"] as? String, "foreground")
        XCTAssertEqual(controller.lastError, "background_unavailable: exact reason")
        XCTAssertEqual(controller.phase, .running, controller.lastError)
    }

    func testBadIdentityBackendFailureAndEndAreRecoverable() async throws {
        let (controller, fake, defaults) = try fixture()
        defer { controller.stop(); defaults.removePersistentDomain(forName: fake.suite) }
        fake.validIdentity = false
        await controller.start()
        XCTAssertEqual(controller.phase, .failed)
        XCTAssertNil(controller.configurationText(port: 51373))
        fake.validIdentity = true
        await controller.start()
        XCTAssertEqual(controller.phase, .running, controller.lastError)
        fake.onFailure?("Native backend failed 42")
        XCTAssertEqual(controller.phase, .failed)
        XCTAssertTrue(controller.lastError.contains("42"))
        await controller.start()
        let token = try bearer(controller)
        let response = await controller.handleHTTP(request(token: token, tool: "end_session"), port: 51373)
        XCTAssertEqual(response.status, 200)
        XCTAssertEqual(controller.phase, .stopped)
        let http8 = await controller.handleHTTP(request(token: token), port: 51373)
        XCTAssertEqual(http8.status, 401)
        let unbundled = SystemControlController(defaults: defaults, hostBundleID: { "unbundled" }, factory: { fake })
        await unbundled.start()
        XCTAssertEqual(unbundled.phase, .failed)
        XCTAssertTrue(unbundled.lastError.contains("签名"))
    }

    func testExpiryRevokesBearerAndStopsBackend() async throws {
        let (controller, fake, defaults) = try fixture(ttl: 1)
        defer { controller.stop(); defaults.removePersistentDomain(forName: fake.suite) }
        await controller.start()
        let token = try bearer(controller)
        let stopsBeforeExpiry = fake.stops
        // Timer delivery can be delayed on a loaded CI host. Check the eventual
        // state instead of requiring an unrelated RPC to finish inside 100 ms.
        let deadline = Date().addingTimeInterval(5)
        while controller.phase == .running && Date() < deadline {
            try await Task.sleep(nanoseconds: 10_000_000)
        }
        XCTAssertEqual(controller.phase, .stopped)
        XCTAssertEqual(fake.stops, stopsBeforeExpiry + 1)
        XCTAssertNil(controller.configurationText(port: 51373))
        let response = await controller.handleHTTP(request(token: token), port: 51373)
        XCTAssertEqual(response.status, 401)
        let calls = fake.calls.count
        do { _ = try await controller.perform("list_apps"); XCTFail("Expired session admitted work") } catch { }
        XCTAssertEqual(fake.calls.count, calls)
    }

    func testBusyGateAndStopDiscardInFlightResults() async throws {
        let (controller, fake, defaults) = try fixture()
        defer { controller.stop(); defaults.removePersistentDomain(forName: fake.suite) }
        await controller.start()
        fake.delay = 150_000_000
        let action = Task { try await controller.perform("list_apps") }
        try await Task.sleep(nanoseconds: 20_000_000)
        XCTAssertTrue(controller.busy)
        do { _ = try await controller.perform("list_windows"); XCTFail("Concurrent call admitted") } catch { }
        controller.stop()
        await controller.start()
        do { _ = try await action.value; XCTFail("Old generation returned data after stop") } catch { }
        XCTAssertEqual(controller.phase, .running, controller.lastError)
    }

    func testRealHTTPGatewayHandlesBrowserPreflightWithoutContentLength() async throws {
        let (controller, fake, defaults) = try fixture()
        defer { controller.stop(); defaults.removePersistentDomain(forName: fake.suite) }
        controller.allowedOriginDraft = "http://localhost:3000"
        await controller.start()
        let port = Int.random(in: 52000...59000)
        let server = MCPHTTPServer(port: port) { request in
            await controller.handleHTTP(request, port: port)
        }
        try server.start()
        defer { server.stop() }
        var request = URLRequest(url: URL(string: "http://127.0.0.1:\(port)/system/mcp")!)
        request.httpMethod = "OPTIONS"
        request.setValue("http://localhost:3000", forHTTPHeaderField: "Origin")
        request.setValue("POST", forHTTPHeaderField: "Access-Control-Request-Method")
        let (_, response) = try await URLSession.shared.data(for: request)
        XCTAssertEqual((response as? HTTPURLResponse)?.statusCode, 200)
        XCTAssertEqual((response as? HTTPURLResponse)?.value(forHTTPHeaderField: "Access-Control-Allow-Origin"), "http://localhost:3000")
        request.httpMethod = "POST"
        request.setValue("Bearer " + (try bearer(controller)), forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONSerialization.data(withJSONObject: ["jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": ["name": "list_apps", "arguments": [:]]])
        let (body, response2) = try await URLSession.shared.data(for: request)
        XCTAssertEqual((response2 as? HTTPURLResponse)?.statusCode, 200)
        XCTAssertTrue(String(decoding: body, as: UTF8.self).contains("ok"))
        controller.stop()
        let (_, stopped) = try await URLSession.shared.data(for: request)
        XCTAssertEqual((stopped as? HTTPURLResponse)?.statusCode, 401)
    }

    func testComputerToggleRequiresBothHostGrantsBeforeStartingBackend() async throws {
        var grants = ComputerControlPermissions(accessibility: false, screenRecording: false)
        let (controller, fake, defaults) = try fixture(readHostPermissions: { grants })
        defer { controller.stop(); defaults.removePersistentDomain(forName: fake.suite) }
        for partial in [ComputerControlPermissions(accessibility: false, screenRecording: false),
                        ComputerControlPermissions(accessibility: true, screenRecording: false),
                        ComputerControlPermissions(accessibility: false, screenRecording: true)] {
            grants = partial
            let enabled = await controller.enableIfAuthorized()
            XCTAssertFalse(enabled)
            XCTAssertEqual(fake.starts, 0)
            XCTAssertNil(controller.configurationJSON(port: 51373))
        }
        grants = ComputerControlPermissions(accessibility: true, screenRecording: true)
        let enabled = await controller.enableIfAuthorized()
        XCTAssertTrue(enabled, controller.lastError)
        XCTAssertEqual(fake.starts, 1)
        let json = try XCTUnwrap(controller.configurationJSON(port: 51373))
        let object = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(json.utf8)) as? [String: Any])
        let servers = object["mcpServers"] as? [String: [String: Any]]
        XCTAssertEqual(servers?["ytray-system"]?["url"] as? String, "http://127.0.0.1:51373/system/mcp")
        controller.stop()
        XCTAssertNil(controller.configurationJSON(port: 51373))
    }

    func testFeedbackIsOneRedactedLifecyclePerPublicAction() async throws {
        let (controller, fake, defaults) = try fixture()
        defer { controller.stop(); defaults.removePersistentDomain(forName: fake.suite) }
        await controller.start()
        _ = try await controller.perform("type_text", arguments: ["window_id": 7, "element_index": 2, "text": "private-secret", "replace": true, "submit": true])
        let updates = fake.feedback.compactMap { $0 }
        XCTAssertEqual(updates.map(\.phase), [.preparing, .running, .completed])
        XCTAssertEqual(Set(updates.map(\.id)).count, 1)
        XCTAssertEqual(controller.recentActivity.count, 1)
        XCTAssertFalse(updates.contains { $0.summary.contains("private-secret") })
        XCTAssertTrue(updates.last?.summary.contains("14 个字符") == true)
        XCTAssertTrue(updates.last?.summary.contains("按回车提交") == true)
        let resultCount = fake.feedback.count
        _ = try await controller.perform("list_windows")
        XCTAssertEqual(fake.feedback.count, resultCount, "Readback must not create another input banner")
        controller.stop()
        XCTAssertNil(fake.feedback.last!)
    }

    func testStopDuringPreparationNeverSendsInputOrResurrectsOverlay() async throws {
        let (controller, fake, defaults) = try fixture()
        defer { controller.stop(); defaults.removePersistentDomain(forName: fake.suite) }
        await controller.start()
        let task = Task { try await controller.perform("click", arguments: ["window_id": 1, "element_index": 0]) }
        while controller.activity == nil { await Task.yield() }
        XCTAssertEqual(controller.activity?.phase, .preparing)
        controller.stop()
        do { _ = try await task.value; XCTFail("Stopped input returned success") } catch { }
        XCTAssertFalse(fake.calls.contains { $0.0 == "click" })
        XCTAssertNil(fake.feedback.last!)
        XCTAssertEqual(controller.activity?.phase, .failed)
        XCTAssertFalse(controller.busy)
    }

    func testStopDuringRunningInputDiscardsItsLateFeedback() async throws {
        let (controller, fake, defaults) = try fixture()
        defer { controller.stop(); defaults.removePersistentDomain(forName: fake.suite) }
        await controller.start()
        fake.delay = 150_000_000
        let task = Task { try await controller.perform("click", arguments: ["window_id": 1, "element_index": 0]) }
        while controller.activity?.phase != .running { await Task.yield() }
        controller.stop()
        do { _ = try await task.value; XCTFail("Stopped input returned success") } catch { }
        XCTAssertNil(fake.feedback.last!)
        XCTAssertEqual(controller.activity?.phase, .uncertain)
        XCTAssertEqual(controller.recentActivity.count, 1)
    }

    func testApplicationToolsAndDesktopAliasRequireLocalForegroundGrant() async throws {
        let (controller, fake, defaults) = try fixture()
        defer { controller.stop(); defaults.removePersistentDomain(forName: fake.suite) }
        await controller.start()
        for name in ["launch_app", "activate_window"] {
            XCTAssertThrowsError(try controller.validatedArguments(name, arguments: name == "launch_app" ? ["app": "com.apple.TextEdit"] : ["window_id": 7]))
        }
        controller.stop()
        controller.allowForegroundDraft = true
        await controller.start()
        XCTAssertEqual(try controller.validatedArguments("launch_app", arguments: ["app": "com.apple.TextEdit"])["app"] as? String, "com.apple.TextEdit")
        XCTAssertThrowsError(try controller.validatedArguments("launch_app", arguments: ["app": "com.apple.TextEdit", "command": "echo unsafe"]))
        let mapped = try controller.validatedArguments("click", arguments: ["window_id": 0, "x": 10, "y": 20, "delivery_mode": "foreground"])
        XCTAssertNil(mapped["window_id"])
        XCTAssertEqual((mapped["target"] as? [String: String])?["kind"], "desktop")
        XCTAssertThrowsError(try controller.validatedArguments("click", arguments: ["window_id": 0, "pid": 1, "delivery_mode": "foreground"]))
        XCTAssertThrowsError(try controller.validatedArguments("click", arguments: ["window_id": false, "delivery_mode": "foreground"]))
    }

    private func fixture(ttl: TimeInterval = 1800, readHostPermissions: @escaping () -> ComputerControlPermissions = { .current() }) throws -> (SystemControlController, FakeDesktopBackend, UserDefaults) {
        let suite = "io.yaklang.ytray.system.tests.\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let driver = FakeDesktopBackend(suite: suite)
        return (SystemControlController(defaults: defaults, ttl: ttl, readHostPermissions: readHostPermissions, hostBundleID: { "io.yaklang.ytray.dev" }, factory: { driver }), driver, defaults)
    }

    private func bearer(_ controller: SystemControlController) throws -> String {
        let config = try XCTUnwrap(controller.configurationText(port: 51373)?.components(separatedBy: "\n").first)
        let object = try JSONSerialization.jsonObject(with: Data(config.utf8)) as? [String: Any]
        let servers = object?["mcpServers"] as? [String: [String: Any]]
        let headers = servers?["ytray-system"]?["headers"] as? [String: String]
        return String(try XCTUnwrap(headers?["Authorization"]).dropFirst(7))
    }

    private func request(token: String = "", method: String = "POST", origin: String? = nil, host: String = "127.0.0.1:51373", tool: String = "system_status") -> MCPHTTPRequest {
        var headers = ["host": host, "authorization": "Bearer " + token, "content-type": "application/json"]
        headers["origin"] = origin
        let rpc: [String: Any] = ["jsonrpc": "2.0", "id": 1, "method": tool == "tools/list" ? "tools/list" : "tools/call", "params": ["name": tool, "arguments": [:]]]
        return MCPHTTPRequest(method: method, path: "/system/mcp", headers: headers, body: try! JSONSerialization.data(withJSONObject: rpc))
    }
}

@MainActor
private final class FakeDesktopBackend: DesktopControlBackend {
    var onFailure: ((String) -> Void)?
    var delay: UInt64 = 0
    var starts = 0
    var stops = 0
    var calls: [(String, [String: Any])] = []
    var feedback: [DesktopOperationFeedback?] = []
    func updateFeedback(_ value: DesktopOperationFeedback?) { feedback.append(value) }
    var validIdentity = true
    var accessibility = true
    var result: [String: Any] = ["content": [["type": "text", "text": "ok"]]]
    let suite: String
    init(suite: String) { self.suite = suite }
    func start(hostBundleID: String) async throws { starts += 1 }
    func stop() { stops += 1 }
    func request(_ method: String, params: [String: Any]) async throws -> [String: Any] {
        if method == "tools/list" {
            return ["tools": SystemControlController.fields.map { name, fields in
                ["name": name, "inputSchema": ["type": "object", "properties": Dictionary(uniqueKeysWithValues: fields.union(["session", "screenshot_out_file"]).map { ($0, ["type": "string"]) })]] as [String: Any]
            }]
        }
        let name = params["name"] as? String ?? ""
        let args = params["arguments"] as? [String: Any] ?? [:]
        calls.append((name, args))
        if name == "health_report" {
            return ["structuredContent": ["checks": [["name": "bundle_identity", "status": validIdentity ? "pass" : "fail", "data": ["bundle_identifier": "io.yaklang.ytray.dev", "identity_source": "application", "process_id": Int(ProcessInfo.processInfo.processIdentifier)]]]]]
        }
        if name == "check_permissions" {
            return ["structuredContent": ["accessibility": accessibility, "screen_recording": false, "source": ["attribution": "host"]]]
        }
        if delay > 0 { try await Task.sleep(nanoseconds: delay) }
        return result
    }
}
