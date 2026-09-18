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

    private func onScreenButton(_ label: String, timeout: TimeInterval = 10) -> XCUIElement? {
        let query = app.descendants(matching: .button).matching(identifier: label)
        let deadline = Date().addingTimeInterval(timeout)
        repeat {
            // macOS exposes duplicate buttons in Touch Bar (y=0). Select the
            // desktop control from the full tree; accessory panels may not be
            // returned by the windows query even when present in the snapshot.
            if let button = query.allElementsBoundByIndex.first(where: {
                $0.frame.minY >= 30 && $0.frame.width > 0 && $0.frame.height > 0
            }) { return button }
            Thread.sleep(forTimeInterval: 0.25)
        } while Date() < deadline
        return nil
    }

    private func clickButton(_ label: String) throws {
        let button = try XCTUnwrap(onScreenButton(label), "Missing desktop button: \(label)\n\(app.debugDescription)")
        button.click()
    }

    func testPublishedAppTrayAndConsoleInteraction() throws {
        app.launch()
        capture("01-launched")
        // A fresh installation presents the login-item result. Exercise its real button.
        if let notice = onScreenButton("知道了", timeout: 8) {
            notice.click()
            XCTAssertTrue(app.sheets.firstMatch.waitForNonExistence(timeout: 5), "First-launch notice did not close")
        }
        capture("02-first-launch-notice-dismissed")

        let manager = app.windows["YTray"].firstMatch
        if manager.exists { manager.buttons[XCUIIdentifierCloseWindow].click() }
        let tray = app.descendants(matching: .statusItem).firstMatch
        XCTAssertTrue(tray.waitForExistence(timeout: 10), "Missing status item\n\(app.debugDescription)")
        tray.click()
        capture("03-tray-widget")
        try clickButton("全部管理")
        XCTAssertTrue(manager.waitForExistence(timeout: 10))
        capture("04-console-opened-from-widget")

        let versionButton = app.buttons["YTray 版本与更新"].firstMatch
        XCTAssertTrue(versionButton.waitForExistence(timeout: 5), "Missing version button\n\(app.debugDescription)")
        versionButton.click()
        let updatePopover = app.descendants(matching: .any)
            .matching(identifier: "YTray 版本更新弹层").firstMatch
        XCTAssertTrue(updatePopover.waitForExistence(timeout: 5), "Update popover did not open\n\(app.debugDescription)")
        Thread.sleep(forTimeInterval: 2)
        XCTAssertTrue(updatePopover.exists, "Update popover closed during the update state refresh")
        capture("04-update-popover")
        versionButton.click()
        XCTAssertTrue(updatePopover.waitForNonExistence(timeout: 5), "Update popover did not close")

        for (page, marker) in [
            ("浏览器运行时", "添加本地浏览器…"),
            ("启动设置", "保存设置"),
            ("运行与历史", "暂无运行中的浏览器"),
            ("插件管理", "添加或扫描插件目录…"),
            ("开机启动", "刷新状态"),
            ("快速配置", "开始配置"),
        ] {
            try clickButton(page)
            let content = app.descendants(matching: .any).matching(NSPredicate(format: "label == %@ OR value == %@", marker, marker)).firstMatch
            XCTAssertTrue(content.waitForExistence(timeout: 10), "Page did not change to \(page)\n\(app.debugDescription)")
            capture("page-" + page)
        }

        try clickButton("开始配置")
        XCTAssertTrue(app.sheets.firstMatch.waitForExistence(timeout: 5))
        capture("05-custom-launch-wizard")
        try clickButton("取消")
        XCTAssertTrue(app.sheets.firstMatch.waitForNonExistence(timeout: 5))
        try clickButton("启动设置")
        try clickButton("保存设置")
        capture("06-settings-saved")

        manager.buttons[XCUIIdentifierCloseWindow].click()
        XCTAssertTrue(manager.waitForNonExistence(timeout: 5))
        // Activate before opening the menu: XCTest's implicit activation during
        // MenuItem.click can otherwise dismiss an accessory application's menu.
        app.activate()
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
