import AppKit
import Carbon
import XCTest
@testable import YTray

final class AppLaunchPolicyTests: XCTestCase {
    private func launchEvent(source: OSType? = nil) -> NSAppleEventDescriptor {
        let event = NSAppleEventDescriptor(eventClass: AEEventClass(kCoreEventClass), eventID: AEEventID(kAEOpenApplication),
                                          targetDescriptor: nil, returnID: AEReturnID(kAutoGenerateReturnID),
                                          transactionID: AETransactionID(kAnyTransactionID))
        if let source { event.setParam(NSAppleEventDescriptor(enumCode: source), forKeyword: keyAEPropData) }
        return event
    }

    func testManualAndTerminalLaunchesPresentManager() {
        XCTAssertTrue(AppLaunchPolicy.shouldPresentManager(event: nil, arguments: ["YTray"]))
        XCTAssertTrue(AppLaunchPolicy.shouldPresentManager(event: launchEvent(), arguments: ["YTray"]))
    }

    func testLoginAndServiceLaunchesStayQuiet() {
        for source in [keyAELaunchedAsLogInItem, keyAELaunchedAsServiceItem] {
            XCTAssertFalse(AppLaunchPolicy.shouldPresentManager(event: launchEvent(source: OSType(source)), arguments: ["YTray"]))
        }
        XCTAssertFalse(AppLaunchPolicy.shouldPresentManager(event: launchEvent(), arguments: ["YTray", "--background"]))
    }

    func testAutomationDoesNotOpenAnUnrelatedManagerWindow() {
        for argument in ["--smoke-widget-focus", "--smoke-reopen", "--render-manager", "--verify-update"] {
            XCTAssertFalse(AppLaunchPolicy.shouldPresentManager(event: nil, arguments: ["YTray", argument]))
        }
    }
}
