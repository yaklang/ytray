import XCTest

final class YTrayInteractionUITests: XCTestCase {
    private let app = XCUIApplication(bundleIdentifier: "io.yaklang.ytray")

    override func setUpWithError() throws { continueAfterFailure = false }

    private func capture(_ name: String) {
        let screen = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
        screen.name = name
        screen.lifetime = .keepAlways
        add(screen)
        let tree = XCTAttachment(string: app.debugDescription)
        tree.name = name + "-accessibility"
        tree.lifetime = .keepAlways
        add(tree)
        print("UI checkpoint: \(name)\n\(app.debugDescription)")
        XCTAssertNotEqual(app.state, .notRunning, "YTray exited at \(name)")
    }

    private func clickButton(_ label: String) {
        let button = app.windows.buttons[label].firstMatch
        XCTAssertTrue(button.waitForExistence(timeout: 10), "Missing button: \(label)\n\(app.debugDescription)")
        button.click()
    }

    func testPublishedAppTrayAndConsoleInteraction() throws {
        app.launch()
        capture("01-launched")
        // A fresh installation presents the login-item result. Exercise its real button.
        let notice = app.windows.buttons["知道了"].firstMatch
        if notice.waitForExistence(timeout: 8) { notice.click() }
        capture("02-first-launch-notice-dismissed")

        let manager = app.windows["YTray"].firstMatch
        if manager.exists { manager.buttons[XCUIIdentifierCloseWindow].click() }
        let tray = app.descendants(matching: .statusItem).firstMatch
        XCTAssertTrue(tray.waitForExistence(timeout: 10), "Missing status item\n\(app.debugDescription)")
        tray.click()
        capture("03-tray-widget")
        clickButton("全部管理")
        XCTAssertTrue(manager.waitForExistence(timeout: 10))
        capture("04-console-opened-from-widget")

        for (page, marker) in [
            ("浏览器运行时", "添加本地浏览器…"),
            ("启动设置", "保存设置"),
            ("运行与历史", "暂无运行中的浏览器"),
            ("插件管理", "添加或扫描插件目录…"),
            ("开机启动", "刷新状态"),
            ("快速配置", "开始配置"),
        ] {
            clickButton(page)
            let content = app.descendants(matching: .any).matching(NSPredicate(format: "label == %@", marker)).firstMatch
            XCTAssertTrue(content.waitForExistence(timeout: 10), "Page did not change to \(page)\n\(app.debugDescription)")
            capture("page-" + page)
        }

        clickButton("开始配置")
        XCTAssertTrue(app.sheets.firstMatch.waitForExistence(timeout: 5))
        capture("05-custom-launch-wizard")
        clickButton("取消")
        XCTAssertTrue(app.sheets.firstMatch.waitForNonExistence(timeout: 5))
        clickButton("启动设置")
        clickButton("保存设置")
        capture("06-settings-saved")

        manager.buttons[XCUIIdentifierCloseWindow].click()
        tray.rightClick()
        capture("07-tray-context-menu")
        let management = app.menuItems["全部管理"].firstMatch
        XCTAssertTrue(management.waitForExistence(timeout: 5))
        management.click()
        XCTAssertTrue(manager.waitForExistence(timeout: 5))
        capture("08-console-reopened-from-tray-menu")
        app.terminate()
    }

    override func tearDownWithError() throws {
        if app.state != .notRunning { capture("final-state"); app.terminate() }
    }
}
