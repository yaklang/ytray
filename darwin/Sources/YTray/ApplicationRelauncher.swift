import AppKit
import Combine
import Darwin
import Foundation
import SwiftUI

/// A native user action only. The MCP gateway has no restart or process-launch tool.
@MainActor
final class ApplicationRelauncher: ObservableObject {
    static let shared = ApplicationRelauncher()
    static let helperFlag = "--relaunch-after-exit"
    static let reopenedFlag = "--show-computer-settings-after-restart"
    @Published private(set) var isRestarting = false
    @Published var errorMessage: String?

    private let applicationURL: () -> URL
    private let executableURL: () -> URL?
    private let launch: (URL, [String]) throws -> Void
    private let terminate: @MainActor () -> Void

    init(applicationURL: @escaping () -> URL = { Bundle.main.bundleURL },
         executableURL: @escaping () -> URL? = { Bundle.main.executableURL },
         launch: @escaping (URL, [String]) throws -> Void = { executable, arguments in
             let helper = Process()
             helper.executableURL = executable
             helper.arguments = arguments
             helper.standardInput = FileHandle.nullDevice
             helper.standardOutput = FileHandle.nullDevice
             helper.standardError = FileHandle.nullDevice
             try helper.run()
         },
         terminate: @escaping @MainActor () -> Void = { NSApplication.shared.terminate(nil) }) {
        self.applicationURL = applicationURL
        self.executableURL = executableURL
        self.launch = launch
        self.terminate = terminate
    }

    func restart() {
        guard !isRestarting else { return }
        errorMessage = nil
        do {
            let app = applicationURL().standardizedFileURL.resolvingSymlinksInPath()
            guard let executable = executableURL()?.standardizedFileURL.resolvingSymlinksInPath(),
                  app.pathExtension.lowercased() == "app",
                  executable.path.hasPrefix(app.appendingPathComponent("Contents/MacOS", isDirectory: true).path + "/"),
                  FileManager.default.isExecutableFile(atPath: executable.path) else {
                throw SystemControlError("请从完整的 YTray.app 或 YTrayDev.app 重启；swift run 开发进程请在终端重新运行。")
            }
            isRestarting = true
            try launch(executable, [Self.helperFlag, String(ProcessInfo.processInfo.processIdentifier)])
            DiagnosticLog.info("app.restart", "relaunch helper admitted; the control session will be revoked before exit")
            // applicationWillTerminate revokes system-control tokens and stops MCP.
            // The helper waits for this process to exit before asking LaunchServices to reopen.
            terminate()
        } catch {
            isRestarting = false
            errorMessage = error.localizedDescription
            DiagnosticLog.error("app.restart", error)
        }
    }

    static func runHelperIfRequested(arguments: [String]) -> Bool {
        guard let index = arguments.firstIndex(of: helperFlag) else { return false }
        DiagnosticLog.initialize(applicationDirectory: AppEnvironment.defaultApplicationDirectory)
        guard index + 1 < arguments.count, let parent = Int32(arguments[index + 1]), parent > 0,
              parent != ProcessInfo.processInfo.processIdentifier,
              Bundle.main.bundleURL.pathExtension.lowercased() == "app",
              ["io.yaklang.ytray", "io.yaklang.ytray.dev"].contains(Bundle.main.bundleIdentifier ?? "") else {
            DiagnosticLog.warning("app.restart", "invalid relaunch helper invocation")
            return true
        }
        let deadline = Date().addingTimeInterval(15)
        while Darwin.kill(parent, 0) == 0 {
            guard Date() < deadline else {
                DiagnosticLog.warning("app.restart", "parent did not exit; skipped duplicate launch")
                return true
            }
            Thread.sleep(forTimeInterval: 0.05)
        }
        do {
            let opener = Process()
            opener.executableURL = URL(fileURLWithPath: "/usr/bin/open")
            // Fixed destination and fixed startup flag; no caller-supplied command or path.
            opener.arguments = ["-n", "-a", Bundle.main.bundleURL.path, "--args", reopenedFlag]
            try opener.run()
            opener.waitUntilExit()
            if opener.terminationStatus != 0 { throw SystemControlError("LaunchServices 未能重新打开应用（\(opener.terminationStatus)）") }
            DiagnosticLog.info("app.restart", "reopen submitted to LaunchServices")
        } catch { DiagnosticLog.error("app.restart", error) }
        return true
    }
}

struct ApplicationRestartButton: View {
    @ObservedObject private var relauncher = ApplicationRelauncher.shared
    var body: some View {
        Button(relauncher.isRestarting ? "正在重启…" : "重启并应用权限") { relauncher.restart() }
            .disabled(relauncher.isRestarting)
            .help("结束当前电脑控制会话并重新打开应用，然后回到电脑控制页面")
            .alert("无法重新打开应用", isPresented: Binding(get: { relauncher.errorMessage != nil }, set: { if !$0 { relauncher.errorMessage = nil } })) {
                Button("知道了", role: .cancel) { relauncher.errorMessage = nil }
            } message: { Text(relauncher.errorMessage ?? "") }
    }
}
