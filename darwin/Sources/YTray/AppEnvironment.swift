import Foundation

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

    static var instanceColorThemesEnabled: Bool {
        if isDevelopmentBuild {
            return Bundle.main.object(forInfoDictionaryKey: "YTrayInstanceColorThemes") as? Bool ?? false
        }
        return true
    }

    static var appUpdatesEnabled: Bool { !isDevelopmentBuild }
    static var launchAtLoginEnabled: Bool { !isDevelopmentBuild }
    static var bundledExtensionAutoInstallEnabled: Bool { !isDevelopmentBuild }
}
