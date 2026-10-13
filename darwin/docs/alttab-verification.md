# macOS AltTab 浏览器切换验证

浏览器现在直接运行原始可执行文件，不再先把 YTray 注册为 AppKit 应用后通过 `execv` 替换进程。启动时清除继承的 `__CFBundleIdentifier`，聚焦时使用 macOS 14+ 的协作激活 API。

Dock 使用浏览器原生图标。YTray 中的 A/B 实例标识、独立 Profile、浏览器主题、插件和历史恢复继续保留。

自动验证：

```bash
swift test --package-path darwin
./script/package-macos.sh --arch arm64
dist/darwin-arm64/YTray.app/Contents/MacOS/YTray \
  --smoke-browser '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome' \
  --verify-window-focus
```

冒烟测试检查窗口切换工具首次收到的应用启动通知、浏览器 Bundle ID、原始可执行文件、正常窗口所属 PID，并从独立的 YTray 测试窗口切回浏览器三次。它使用临时 Profile 和本地生成的测试页面，结束后只关闭测试创建的浏览器。macOS 14 与 macOS 26 CI 执行相同检查，并保存 `browser-window-smoke.txt`。

AltTab 人工回归需要已安装 AltTab 的 macOS 桌面：

1. 打开 AltTab 后再启动 YTray，分别启动 A、B 两个浏览器实例。
2. 打开 YTray 控制台，展开贴边的启动按钮，在其他应用和 A/B 浏览器之间反复切换。
3. 确认 AltTab 每次激活所选浏览器窗口，浏览器窗口没有被归入 YTray 的应用条目。
4. 覆盖浏览器最小化、不同 Space、关闭 YTray 控制台、停止后从历史恢复的场景；重新打开 AltTab 后再次切换。

自动检查验证原生应用身份和前台切换；AltTab 自身的筛选设置、最小化和跨 Space 行为仍需上述人工回归。
