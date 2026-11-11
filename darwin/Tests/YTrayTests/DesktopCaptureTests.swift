import CoreGraphics
import XCTest
@testable import YTray

final class DesktopCaptureTests: XCTestCase {
    private let frame = CGRect(x: 0, y: 0, width: 1920, height: 1080)
    private let capturedAt = Date(timeIntervalSince1970: 1000)

    func testDesktopCoordinatesRejectPrimaryMonitorReplacementEvenAtIdenticalResolution() throws {
        let capture = DesktopCapture(id: "capture", date: capturedAt, scale: 0.5, frame: frame, displayID: 11)
        try capture.validate(currentDisplayID: 11, currentFrame: frame, now: capturedAt.addingTimeInterval(10))
        XCTAssertThrowsError(try capture.validate(currentDisplayID: 12, currentFrame: frame, now: capturedAt.addingTimeInterval(10))) {
            XCTAssertEqual(($0 as? DesktopOperationError)?.code, "stale_capture")
        }
    }

    func testDesktopCoordinatesRejectResizeRelocationAndExpiration() throws {
        let capture = DesktopCapture(id: "capture", date: capturedAt, scale: 1, frame: frame, displayID: 11)
        for changed in [frame.offsetBy(dx: -1920, dy: 0), CGRect(x: 0, y: 0, width: 1280, height: 720)] {
            XCTAssertThrowsError(try capture.validate(currentDisplayID: 11, currentFrame: changed, now: capturedAt))
        }
        try capture.validate(currentDisplayID: 11, currentFrame: frame, now: capturedAt.addingTimeInterval(60))
        for interval in [-1.0, 60.01, Double.infinity] {
            XCTAssertThrowsError(try capture.validate(currentDisplayID: 11, currentFrame: frame, now: capturedAt.addingTimeInterval(interval)))
        }
    }

    func testInvalidCaptureGeometryCannotAuthorizeDesktopInput() {
        for scale: CGFloat in [0, -1, .infinity, .nan] {
            let capture = DesktopCapture(id: "capture", date: capturedAt, scale: scale, frame: frame, displayID: 11)
            XCTAssertThrowsError(try capture.validate(currentDisplayID: 11, currentFrame: frame, now: capturedAt))
        }
        let empty = DesktopCapture(id: "capture", date: capturedAt, scale: 1, frame: .zero, displayID: 11)
        XCTAssertThrowsError(try empty.validate(currentDisplayID: 11, currentFrame: .zero, now: capturedAt))
    }
}
