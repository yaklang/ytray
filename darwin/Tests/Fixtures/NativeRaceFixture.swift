import AppKit

@MainActor
final class ValueOnlyField: NSTextField {
    override func isAccessibilitySelectorAllowed(_ selector: Selector) -> Bool {
        if NSStringFromSelector(selector) == "setAccessibilitySelectedText:" { return false }
        return super.isAccessibilitySelectorAllowed(selector)
    }
}

// Compile with: swiftc NativeRaceFixture.swift -o /tmp/ytray-native-race-fixture
// Only temporary fixture files and this process's own window are modified.
@MainActor
final class Fixture: NSObject, NSApplicationDelegate {
    let stateURL = URL(fileURLWithPath: CommandLine.arguments[1])
    let commandURL = URL(fileURLWithPath: CommandLine.arguments[2])
    let window = NSWindow(contentRect: NSRect(x: 120, y: 160, width: 520, height: 240), styleMask: [.titled], backing: .buffered, defer: false)
    let first = NSSecureTextField(frame: NSRect(x: 20, y: 90, width: 220, height: 28))
    let second = NSSecureTextField(frame: NSRect(x: 270, y: 90, width: 220, height: 28))
    let button = NSButton(frame: NSRect(x: 20, y: 150, width: 160, height: 32))
    let valueOnly = ValueOnlyField(frame: NSRect(x: 20, y: 35, width: 470, height: 28))
    var cues = 0, submissions = 0
    var timer: Timer?
    func applicationDidFinishLaunching(_ notification: Notification) {
        window.title = "YTray isolated input race test"
        window.isReleasedWhenClosed = false
        first.setAccessibilityLabel("Race Editor A")
        second.setAccessibilityLabel("Race Editor B")
        first.target = self; first.action = #selector(submit)
        second.target = self; second.action = #selector(submit)
        button.title = "Race Increment"; button.target = self; button.action = #selector(submit)
        valueOnly.stringValue = "original"; valueOnly.setAccessibilityLabel("Race Value Only")
        for view in [first, second, button, valueOnly] { window.contentView?.addSubview(view) }
        window.makeKeyAndOrderFront(nil)
        NSApplication.shared.activate(ignoringOtherApps: true)
        window.makeFirstResponder(first)
        timer = Timer.scheduledTimer(withTimeInterval: 0.01, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.poll() }
        }
        save()
    }
    func poll() {
        if let change = try? String(contentsOf: commandURL, encoding: .utf8) {
            try? FileManager.default.removeItem(at: commandURL)
            cues += 1
            switch change {
            case "move_control": button.setFrameOrigin(NSPoint(x: 280, y: 150))
            case "move_window": window.setFrameOrigin(window.frame.origin.applying(CGAffineTransform(translationX: 80, y: 0)))
            case "change_focus": window.makeFirstResponder(second)
            case "edit_value": valueOnly.stringValue = "user edited"
            default: break
            }
        }
        save()
    }
    func save() {
        let state: [String: Any] = ["pid": Int(ProcessInfo.processInfo.processIdentifier), "window_id": window.windowNumber,
            "frontmost_pid": Int(NSWorkspace.shared.frontmostApplication?.processIdentifier ?? 0),
            "cues": cues, "submissions": submissions, "first": first.stringValue, "second": second.stringValue,
            "value_only": valueOnly.stringValue]
        if let data = try? JSONSerialization.data(withJSONObject: state) { try? data.write(to: stateURL, options: .atomic) }
    }
    @objc func submit() { submissions += 1; save() }
}
MainActor.assumeIsolated {
    let app = NSApplication.shared
    app.setActivationPolicy(.regular)
    let fixture = Fixture()
    app.delegate = fixture
    app.run()
}
