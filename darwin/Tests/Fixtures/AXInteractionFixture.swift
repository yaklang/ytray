// Standalone disposable app for exercising the real YTray MCP gateway.
// Build: swiftc -parse-as-library AXInteractionFixture.swift -o AXInteractionFixture
import AppKit
import Foundation

@main
@MainActor
enum AXInteractionFixtureMain {
    static func main() {
        guard CommandLine.arguments.count == 2 else {
            fputs("usage: AXInteractionFixture <state.json>\n", stderr)
            exit(2)
        }
        let app = NSApplication.shared
        app.setActivationPolicy(.accessory)
        let fixture = AXInteractionFixture(stateURL: URL(fileURLWithPath: CommandLine.arguments[1]))
        fixture.window.orderFront(nil)
        fixture.writeState()
        let timer = Timer.scheduledTimer(withTimeInterval: 0.1, repeats: true) { _ in
            Task { @MainActor in fixture.writeState() }
        }
        app.run()
        timer.invalidate()
        withExtendedLifetime(fixture) {}
    }
}

@MainActor
private final class AXInteractionFixture: NSObject, NSTextFieldDelegate {
    let window = NSWindow(contentRect: NSRect(x: 120, y: 140, width: 640, height: 540),
                          styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
    private let stateURL: URL
    private let counter = NSTextField(labelWithString: "Count 0")
    private let message = NSTextField()
    private let scroll = NSScrollView()
    private var count = 0
    private var submissions = 0
    private var lastState = Data()

    init(stateURL: URL) {
        self.stateURL = stateURL
        super.init()
        window.title = "YTray AX Interaction Fixture"
        window.isReleasedWhenClosed = false
        let content = window.contentView!
        let title = NSTextField(labelWithString: "AI desktop control · Test window")
        title.font = .systemFont(ofSize: 20, weight: .semibold)
        title.frame = NSRect(x: 24, y: 480, width: 590, height: 30)
        content.addSubview(title)
        let button = NSButton(title: "Increment", target: self, action: #selector(increment))
        button.setAccessibilityIdentifier("demo.increment")
        button.frame = NSRect(x: 24, y: 432, width: 130, height: 32)
        content.addSubview(button)
        counter.frame = NSRect(x: 176, y: 436, width: 200, height: 25)
        counter.setAccessibilityLabel("Counter")
        counter.setAccessibilityIdentifier("demo.counter")
        content.addSubview(counter)
        let caption = NSTextField(labelWithString: "Message")
        caption.frame = NSRect(x: 24, y: 390, width: 76, height: 25)
        content.addSubview(caption)
        message.frame = NSRect(x: 110, y: 386, width: 500, height: 30)
        message.placeholderString = "Type here"
        message.setAccessibilityLabel("Message")
        message.setAccessibilityIdentifier("demo.message")
        message.delegate = self
        message.target = self
        message.action = #selector(submit)
        content.addSubview(message)
        let help = NSTextField(labelWithString: "Scroll below to reveal later rows. All contents are disposable fixture data.")
        help.frame = NSRect(x: 24, y: 344, width: 590, height: 25)
        help.font = .systemFont(ofSize: 11)
        content.addSubview(help)
        scroll.frame = NSRect(x: 24, y: 24, width: 586, height: 304)
        scroll.hasVerticalScroller = true
        scroll.autohidesScrollers = false
        scroll.borderType = .bezelBorder
        scroll.setAccessibilityLabel("Rows")
        scroll.setAccessibilityIdentifier("demo.rows")
        let text = NSTextView(frame: NSRect(x: 0, y: 0, width: 560, height: 1800))
        text.isEditable = false
        text.isSelectable = true
        text.font = .monospacedSystemFont(ofSize: 14, weight: .regular)
        text.textContainerInset = NSSize(width: 12, height: 12)
        text.string = (1...80).map { String(format: "Row %02d  |  YTray AX / click / type / scroll", $0) }.joined(separator: "\n")
        text.setAccessibilityLabel("Scrollable rows")
        text.setAccessibilityIdentifier("demo.rows.text")
        scroll.documentView = text
        content.addSubview(scroll)
        window.makeFirstResponder(message)
    }

    @objc private func increment() { count += 1; counter.stringValue = "Count \(count)"; writeState() }
    @objc private func submit() { submissions += 1; writeState() }
    func controlTextDidChange(_ notification: Notification) { writeState() }

    func writeState() {
        let state: [String: Any] = [
            "pid": Int(ProcessInfo.processInfo.processIdentifier), "window_id": window.windowNumber,
            "title": window.title, "count": count, "message": message.stringValue,
            "submissions": submissions, "scroll_y": scroll.contentView.bounds.origin.y,
            "frontmost_pid": NSWorkspace.shared.frontmostApplication.map { Int($0.processIdentifier) } ?? 0,
            "is_key_window": window.isKeyWindow,
        ]
        guard let data = try? JSONSerialization.data(withJSONObject: state, options: [.sortedKeys, .prettyPrinted]), data != lastState else { return }
        lastState = data
        try? data.write(to: stateURL, options: .atomic)
    }
}
