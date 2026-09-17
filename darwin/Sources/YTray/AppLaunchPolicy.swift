import AppKit
import Carbon

enum AppLaunchPolicy {
    static func shouldPresentManager(event: NSAppleEventDescriptor?, arguments: [String]) -> Bool {
        guard !arguments.contains("--background"),
              !arguments.contains(where: { $0.hasPrefix("--smoke-") || $0.hasPrefix("--render-") || $0.hasPrefix("--verify-") }) else {
            return false
        }
        guard let event, event.eventID == kAEOpenApplication else { return true }
        let launchSource = event.paramDescriptor(forKeyword: keyAEPropData)?.enumCodeValue
        return launchSource != keyAELaunchedAsLogInItem && launchSource != keyAELaunchedAsServiceItem
    }
}
