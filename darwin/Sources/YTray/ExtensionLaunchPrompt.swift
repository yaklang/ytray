import AppKit

struct ExtensionLaunchPrompt {
    let message: String
    let canSkipPlugins: Bool

    init(runtime: BrowserRuntime, settings: LaunchSettings, pluginCount: Int, failure: String) {
        canSkipPlugins = settings.proxyUsername.isEmpty && settings.proxyPassword.isEmpty
        message = "\(runtime.displayTitle) 未能完成插件加载。\n\n原因：\(failure)\n\n"
            + (canSkipPlugins
               ? "本次选择了 \(pluginCount) 个插件。可以不加载插件继续启动；本次插件功能将不可用，其他启动配置保持不变。此选择不会关闭插件的默认加载设置。"
               : "当前代理使用了账号或密码，代理认证依赖插件，无法跳过插件继续启动。请改用 Chrome for Testing、Chromium 或 Edge，或选择无需认证的代理。")
    }

    func retrySettings(from settings: LaunchSettings) -> LaunchSettings? {
        guard canSkipPlugins else { return nil }
        var retry = settings
        retry.defaultPluginIDs = []
        // A restored profile may contain extensions from a partial successful load.
        retry.additionalFlags += "\n--disable-extensions"
        return retry
    }

    @MainActor
    func present() -> Bool {
        let alert = makeAlert()
        NSApp.activate(ignoringOtherApps: true)
        return alert.runModal() == .alertSecondButtonReturn && canSkipPlugins
    }

    @MainActor
    func makeAlert() -> NSAlert {
        let alert = NSAlert()
        alert.messageText = "插件加载失败"
        alert.alertStyle = .warning
        alert.addButton(withTitle: "取消启动").keyEquivalent = "\u{1b}"
        if canSkipPlugins { alert.addButton(withTitle: "不加载插件并启动") }
        // Keep long browser errors scrollable so neither action leaves the screen.
        let scroll = NSScrollView(frame: NSRect(x: 0, y: 0, width: 430, height: 220))
        scroll.hasVerticalScroller = true
        scroll.drawsBackground = false
        let text = NSTextView(frame: scroll.bounds)
        text.isEditable = false
        text.drawsBackground = false
        text.font = .systemFont(ofSize: NSFont.systemFontSize)
        text.textColor = .labelColor
        text.string = message
        text.isVerticallyResizable = true
        text.textContainer?.widthTracksTextView = true
        scroll.documentView = text
        alert.accessoryView = scroll
        return alert
    }
}
