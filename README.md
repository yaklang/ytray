# Instance Dock

Instance Dock 是一个面向 Chrome 独立实例的开源桌面启动器。它把常用启动参数、代理、调试端口、本地插件和独立用户目录组合成可重复使用的实例，并通过托盘与桌面小组件快速管理。

当前已经实现 macOS 原生版本；`windows/` 是独立实现的预留目录。项目不包含授权码、许可证校验、订阅或功能解锁逻辑。

## 启动 macOS 版本

需要 macOS 14 或更高版本以及 Xcode Command Line Tools。进入仓库根目录后运行：

```bash
./script/startup.sh
```

首次编译完成后，菜单栏会出现 Instance Dock 图标。左键菜单栏图标会在图标下方展开并自动聚焦小组件；点击小组件外部时，小组件会立即隐藏，当前点击会继续交给目标应用，不需要再点击第二次。点击标题栏的 PIN 图标可以让小组件在失焦后继续显示，再次点击即可恢复自动隐藏。右键菜单可启动新实例、显示小组件、打开全部管理或退出。

如果脚本没有执行权限，可先运行：

```bash
chmod +x ./script/startup.sh
```

## 直接使用本机浏览器

Instance Dock 不要求安装额外浏览器运行时。启动时会自动发现标准目录中的 Google Chrome、Chrome Beta、Chrome Canary、Chrome for Testing、Chromium 和 Microsoft Edge；发现后即可新建实例。

点击小组件右上角“新建实例”，可以：

- 使用上次选择的默认浏览器直接启动；
- 从已发现的本机浏览器中选择一个并启动；
- 选择其他位置的 `.app` 或浏览器可执行文件；
- 自定义本次代理、调试端口、启动地址和插件；
- 跳转到“浏览器来源”安装一个新的 Chrome for Testing 版本。

直接选择浏览器或在自定义向导中勾选“记住此浏览器”后，后续新建实例会默认使用该选择。界面会同时显示浏览器类型、完整版本号和来源：

- `系统环境`：自动发现于 `/Applications` 或 `~/Applications`；
- `自定义路径`：用户从其他位置选择的浏览器；
- `Instance Dock 安装`：由镜像下载并管理的 Chrome for Testing。

## 可选：安装 Chrome for Testing

打开“全部管理 → 浏览器来源”，可以重新扫描本机，也可以安装或添加浏览器：

- 安装特定版本：刷新版本列表，选择一个 Chrome for Testing 版本后安装。下载内容是 ZIP，Instance Dock 会校验 SHA-256、解压并直接使用其中的浏览器可执行文件，不运行 PKG、DMG 或任何安装器。
- 选择其他本地浏览器：选择已有的浏览器 `.app`，或直接选择其可执行文件。

托管运行时保存在：

```text
~/Library/Application Support/InstanceDock/Runtimes/
```

## 启动方式

- 启动新实例：使用记住的浏览器，并应用“启动设置”中的代理、启动地址、精简参数和插件选择，然后为新实例创建独立用户目录。
- 自定义启动：通过步骤向导依次选择运行时、网络与调试参数、Dock 角标、插件并确认。本次设置不会覆盖默认配置。角标留空时自动分配，也可以填写 1–2 个英文字母。

每个实例的用户目录位于：

```text
~/Library/Application Support/InstanceDock/Profiles/<实例 UUID>/
```

因此不同实例的 Cookie、缓存、Local Storage、登录态和插件数据不会混在一起。

## 代理、调试与浏览器精简

“启动设置”可以配置：

- 代理服务器，例如 `http://127.0.0.1:8080` 或 `socks5://127.0.0.1:1080`
- 调试端口的起始值；端口占用时会向后自动选择可用端口
- 启动地址，例如 `chrome://newtab` 或完整的 `https://` URL
- 跳过首次欢迎、关闭默认浏览器检测、后台网络、同步、翻译与通知
- 限制 WebRTC 使用非代理 UDP 和暴露本地 IP
- 自定义附加 Chrome 参数（每行一个）

调试服务始终绑定到 `127.0.0.1`。用户目录、调试地址/端口和插件加载参数由 Instance Dock 维护，附加参数不能覆盖这些隔离边界。

## 本地插件

Chrome 的 `--load-extension` 需要一个已解压插件目录，而不是 `.crx` 安装包。打开“插件管理”，选择根目录包含以下文件的文件夹：

```text
my-extension/
├── manifest.json
├── background.js       # 具体文件取决于插件
└── ...
```

Instance Dock 会读取 `manifest.json` 中的名称、版本和 Manifest 版本。启用的插件可加入默认设置，也可在自定义启动向导中仅为本次实例选择。

## 运行与历史

托盘和“全部管理 → 运行与历史”会分别显示全部运行中的浏览器和已经停止的历史记录。运行中的浏览器会显示 PID、浏览器版本、独立用户目录、启动模式和调试端口，并支持：

- 使用 A、B、C、D 依次区分每个浏览器进程的 Dock 图标
- 在自定义启动向导中把本次角标设置为任意 1–2 个英文字母
- 停止实例
- 在 Finder 中打开用户目录
- 通过 Chrome DevTools Protocol 对当前页面快速截图

Instance Dock 会在运行期间记录当前活动标签页的 Title 和 URL，并在浏览器停止时保留为历史标题。同一 Dock 角标只保留最新一条历史，避免出现多个 A、B 等重复记录。历史名称可以修改，历史记录可以单独删除，也可以通过“清理全部”在二次确认后一次清空；这些操作不会终止正在运行的浏览器。

历史记录右侧的“打开”会恢复同一实例，而不是创建一个新编号：它会继承原实例名称、UUID、A/B/C Dock 角标、独立用户目录、启动参数与插件，并恢复上次活动标签页。若原角标正在被另一个运行中实例占用，会提示用户先释放该角标。升级前已经保存且没有活动页 URL 的旧历史，只能由浏览器尽力恢复上次会话。

Dock 角标只设置在本次启动的进程上。Instance Dock 先让启动进程显示带角标的浏览器图标，再由同一个 PID 直接执行用户选择的原始浏览器可执行文件。它不会复制浏览器 `.app`，不会修改应用名称、Bundle ID、`Info.plist`、磁盘图标或签名，也不会替换浏览器的钥匙链身份。浏览器启动后仍然是原来的 Chrome、Edge 或 Chromium。

运行中的角标必须唯一，默认从 `A` 排到 `Z`，随后使用 `AA` 到 `ZZ`。macOS 的公开接口不能在 `exec` 完成后从另一个进程即时替换该 Dock tile 的图标，因此自定义角标需要在启动前填写；要更换运行中实例的角标，请停止后用新角标重新启动。

截图保存在：

```text
~/Pictures/InstanceDock/
```

截图完成后会在 Finder 中定位文件，小组件的当前实例卡片也会显示最近一张缩略图。

## 开发验证

```bash
swift build --package-path darwin
swift test --package-path darwin
```

项目结构按平台分开：

```text
darwin/     # Swift / AppKit / SwiftUI 原生实现
windows/    # Windows 原生实现预留
script/     # 本地启动脚本
```
