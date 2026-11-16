import CoreGraphics
import Foundation

/// Desktop coordinates belong to one captured display and geometry, not whichever
/// monitor later becomes primary. The worker checks this again after visual cues.
struct DesktopCapture: Sendable {
    let id: String
    let date: Date
    let scale: CGFloat
    let frame: CGRect
    let displayID: CGDirectDisplayID

    func validatePrimaryDisplay() throws {
        let current = CGMainDisplayID()
        try validate(currentDisplayID: current, currentFrame: CGDisplayBounds(current), now: Date())
    }

    func validate(currentDisplayID: CGDirectDisplayID, currentFrame: CGRect, now: Date) throws {
        let age = now.timeIntervalSince(date)
        guard age >= 0, age <= 60 else {
            throw DesktopOperationError("stale_capture", "桌面截图已过期，请重新截取主显示器")
        }
        guard displayID != 0, scale.isFinite, scale > 0,
              [frame.minX, frame.minY, frame.width, frame.height].allSatisfy(\.isFinite), frame.width > 0, frame.height > 0,
              currentDisplayID == displayID, currentFrame == frame else {
            throw DesktopOperationError("stale_capture", "主显示器或显示区域已改变，请重新截图；没有继续发送桌面输入")
        }
    }
}
