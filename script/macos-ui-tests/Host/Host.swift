import SwiftUI

// Xcode's UI-test runner host. Tests launch the separately installed, unmodified
// release by bundle identifier; this host contains no YTray application code.
@main
struct InteractionHost: App {
    var body: some Scene { WindowGroup { Text("YTray CI UI test host") } }
}
