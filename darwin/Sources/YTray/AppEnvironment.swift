import Foundation
import Security

enum AppEnvironment {
    static let developmentBundleIdentifier = "io.yaklang.ytray.dev"

    static var isDevelopmentBuild: Bool {
        Bundle.main.bundleIdentifier == developmentBundleIdentifier
    }

    static var displayName: String {
        if let value = Bundle.main.object(forInfoDictionaryKey: "CFBundleDisplayName") as? String,
           !value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            return value
        }
        return isDevelopmentBuild ? "YTrayDev" : "YTray"
    }

    static var applicationDirectoryName: String {
        isDevelopmentBuild ? "YTrayDev" : "YTray"
    }

    static var defaultApplicationDirectory: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent(applicationDirectoryName, isDirectory: true)
    }

    static var instanceColorThemesEnabled: Bool {
        if isDevelopmentBuild {
            return Bundle.main.object(forInfoDictionaryKey: "YTrayInstanceColorThemes") as? Bool ?? false
        }
        return true
    }

    static var appUpdatesEnabled: Bool {
        Bundle.main.bundleIdentifier == "io.yaklang.ytray"
            && Bundle.main.bundleURL.pathExtension == "app"
            && !CommandLine.arguments.contains { $0.hasPrefix("--smoke-") || $0.hasPrefix("--render-") || $0.hasPrefix("--verify-") }
    }
    static var launchAtLoginEnabled: Bool { !isDevelopmentBuild }
    static var bundledExtensionAutoInstallEnabled: Bool { !isDevelopmentBuild }
}

/// The public code identity used by macOS privacy authorization. This is
/// diagnostic information, never a substitute for the OS permission checks.
struct AppSigningIdentity {
    let identifier: String?
    let teamIdentifier: String?
    let codeHash: String?
    let isAdHoc: Bool

    static var current: AppSigningIdentity? {
        guard let url = Bundle.main.executableURL else { return nil }
        var code: SecStaticCode?
        guard SecStaticCodeCreateWithPath(url as CFURL, [], &code) == errSecSuccess, let code else { return nil }
        var details: CFDictionary?
        guard SecCodeCopySigningInformation(code, SecCSFlags(rawValue: kSecCSSigningInformation), &details) == errSecSuccess,
              let info = details as? [String: Any] else { return nil }
        let flags = (info[kSecCodeInfoFlags as String] as? NSNumber)?.uint32Value ?? 0
        return AppSigningIdentity(
            identifier: info[kSecCodeInfoIdentifier as String] as? String,
            teamIdentifier: info[kSecCodeInfoTeamIdentifier as String] as? String,
            codeHash: (info[kSecCodeInfoUnique as String] as? Data)?.map { String(format: "%02x", $0) }.joined(),
            isAdHoc: flags & 0x0002 != 0
        )
    }
}
