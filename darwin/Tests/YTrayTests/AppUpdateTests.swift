import XCTest
@testable import YTray

final class AppUpdateTests: XCTestCase {
    private func catalog(architecture: String = "arm64", edit: (inout [String: Any]) -> Void = { _ in }) throws -> Data {
        let filename = "YTray-0.2.1-darwin-\(architecture).dmg"
        var json: [String: Any] = ["schema_version": 1, "product": "ytray", "version": "0.2.1",
            "release_notes": "https://github.com/yaklang/ytray/releases/tag/v0.2.1",
            "release_notes_text": "修复与改进",
            "assets": [["platform": "darwin", "architecture": architecture, "kind": "dmg", "filename": filename,
                "url": "https://aliyun-oss.yaklang.com/ytray/0.2.1/\(filename)", "sha256": String(repeating: "a", count: 64), "size": 123]]]
        edit(&json)
        return try JSONSerialization.data(withJSONObject: json)
    }

    func testBothArchitecturesAndLegacyCatalog() throws {
        for architecture in ["arm64", "amd64"] {
            let release = try AppUpdateManager.parseManifest(catalog(architecture: architecture), architecture: architecture)
            XCTAssertEqual(release.version, "0.2.1")
            XCTAssertEqual(release.releaseNotesText, "修复与改进")
        }
        let legacy = try catalog { $0.removeValue(forKey: "release_notes"); $0.removeValue(forKey: "release_notes_text") }
        XCTAssertNoThrow(try AppUpdateManager.parseManifest(legacy, architecture: "arm64"))
    }

    func testRejectsWrongProductVersionsNotesAndPayloads() throws {
        for (field, value) in [("product", "yconnect"), ("version", "0.02.1"), ("version", "0.2.1\n"),
                               ("version", "../../evil"), ("release_notes", "https://example.com"),
                               ("release_notes_text", String(repeating: "a", count: 32_001))] {
            XCTAssertThrowsError(try AppUpdateManager.parseManifest(catalog { $0[field] = value }, architecture: "arm64"))
        }
        for (field, value) in [("url", "https://example.com/installer.dmg"), ("architecture", "amd64"),
                               ("filename", "../YTray.app"), ("sha256", "invalid")] {
            let data = try catalog {
                var assets = $0["assets"] as! [[String: Any]]; assets[0][field] = value; $0["assets"] = assets
            }
            XCTAssertThrowsError(try AppUpdateManager.parseManifest(data, architecture: "arm64"))
        }
        XCTAssertThrowsError(try AppUpdateManager.parseManifest(catalog { $0["assets"] = [] }, architecture: "arm64"))
        XCTAssertThrowsError(try AppUpdateManager.parseManifest(catalog {
            let assets = $0["assets"] as! [[String: Any]]; $0["assets"] = assets + assets
        }, architecture: "arm64"))
        XCTAssertThrowsError(try AppUpdateManager.parseManifest(Data(repeating: 32, count: 524_289), architecture: "arm64"))
    }

    @MainActor func testBusyWorkPreventsStartingTheNativeInstaller() {
        let updater = AppUpdateManager(currentVersion: "0.2.0", updatesEnabled: true)
        updater.canInstall = { false }
        updater.installUpdate()
        XCTAssertEqual(updater.phase, .failed)
        XCTAssertTrue(updater.statusText.contains("请先完成"))
        XCTAssertFalse(updater.isBusy)
    }

    @MainActor func testBackgroundPreferenceAndDevelopmentIsolation() {
        let suite = "YTrayUpdaterTests-\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        defer { defaults.removePersistentDomain(forName: suite) }
        let enabled = AppUpdateManager(currentVersion: "0.2.0", updatesEnabled: true, defaults: defaults)
        XCTAssertTrue(enabled.automaticallyChecks)
        enabled.automaticallyChecks = false
        XCTAssertFalse(AppUpdateManager(currentVersion: "0.2.0", updatesEnabled: true, defaults: defaults).automaticallyChecks)
        let disabled = AppUpdateManager(currentVersion: "0.2.0", updatesEnabled: false, defaults: defaults)
        disabled.automaticallyChecks = true
        disabled.installUpdate()
        XCTAssertFalse(defaults.bool(forKey: "YTrayCheckUpdates"))
        XCTAssertFalse(AppEnvironment.appUpdatesEnabled)
    }
}
