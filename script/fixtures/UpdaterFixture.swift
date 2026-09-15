// Isolated integration-test host. Never shipped with YTray.
import AppKit
import Sparkle

@MainActor final class Driver: NSObject, NSApplicationDelegate, SPUUserDriver {
    var updater: SPUUpdater?
    func finish(_ message: String, code: Int32) -> Never {
        try? message.write(toFile: Bundle.main.object(forInfoDictionaryKey: "FixtureResult") as! String, atomically: true, encoding: .utf8)
        exit(code)
    }
    func applicationDidFinishLaunching(_ notification: Notification) {
        if Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String == "2" { finish("installed-and-relaunched", code: 0) }
        do {
            updater = SPUUpdater(hostBundle: .main, applicationBundle: .main, userDriver: self, delegate: nil)
            try updater!.start()
            updater!.checkForUpdates()
        } catch { finish("startup-error: \(error)", code: 1) }
    }
    func show(_ request: SPUUpdatePermissionRequest, reply: @escaping (SUUpdatePermissionResponse) -> Void) { reply(SUUpdatePermissionResponse(automaticUpdateChecks: false, sendSystemProfile: false)) }
    func showUserInitiatedUpdateCheck(cancellation: @escaping () -> Void) {}
    func showUpdateFound(with item: SUAppcastItem, state: SPUUserUpdateState, reply: @escaping (SPUUserUpdateChoice) -> Void) { reply(.install) }
    func showUpdateReleaseNotes(with downloadData: SPUDownloadData) {}
    func showUpdateReleaseNotesFailedToDownloadWithError(_ error: Error) {}
    func showUpdateNotFoundWithError(_ error: Error, acknowledgement: @escaping () -> Void) { finish("no-update: \(error)", code: 1) }
    func showUpdaterError(_ error: Error, acknowledgement: @escaping () -> Void) { finish("updater-error: \((error as NSError).code) \(error)", code: 2) }
    func showDownloadInitiated(cancellation: @escaping () -> Void) {}
    func showDownloadDidReceiveExpectedContentLength(_ length: UInt64) {}
    func showDownloadDidReceiveData(ofLength length: UInt64) {}
    func showDownloadDidStartExtractingUpdate() {}
    func showExtractionReceivedProgress(_ progress: Double) {}
    func showReady(toInstallAndRelaunch reply: @escaping (SPUUserUpdateChoice) -> Void) { reply(.install) }
    func showInstallingUpdate(withApplicationTerminated terminated: Bool, retryTerminatingApplication: @escaping () -> Void) {}
    func showUpdateInstalledAndRelaunched(_ relaunched: Bool, acknowledgement: @escaping () -> Void) { acknowledgement() }
    func dismissUpdateInstallation() {}
}

@main enum Main {
    @MainActor static func main() {
        let app = NSApplication.shared
        let driver = Driver()
        app.delegate = driver
        app.setActivationPolicy(.accessory)
        withExtendedLifetime(driver) { app.run() }
    }
}
