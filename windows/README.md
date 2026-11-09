# YTray for Windows

Windows 原生实现（C# / WPF / .NET 10），与 macOS 版本（`darwin/`）分别维护各自的托盘、窗口、进程管理和浏览器路径逻辑。发布包自带 .NET Desktop Runtime，用户无需另装 .NET Framework、.NET 或开发工具。

系统基线为 Windows 10 1809（build 17763）及以上、Windows 11。Win8 / 8.1、Win7 和 XP 不支持。电脑控制目前使用原生 x64 组件；32 位和原生 ARM64 系统不开放该功能。当前完整桌面链路已在 Win11 验证，旧 Win10/LTSC 仍需实机验收。

## 两个 MCP

小组件底部的“MCP 配置”和管理器的“MCP”页面是统一入口：

- **YTray MCP**（`ytray`，`http://127.0.0.1:19222/mcp`）：管理浏览器运行时与实例。
- **电脑控制 MCP**（`ytray-system`，`http://127.0.0.1:51373/system/mcp`）：观察窗口、截图、点击与输入。

两个服务可分别启停和复制配置；电脑控制开启后，“复制两个 MCP 配置”可生成一个含两个条目的 `mcpServers` JSON。小组件电脑控制卡片的复制按钮仅复制电脑控制配置。复制操作不会替用户开启电脑控制。

## 功能

与 macOS 版本对等：

- 托盘图标 + 左键弹出小组件、右键菜单，小组件与启动向导使用正式 YTray Logo
- A/B/C/D 实例主题色：橙 / 蓝 / 绿 / 紫，继续使用 E 品红、F 青绿并循环（与 macOS 相同）
- 任务栏角标、实例列表、Profile 详情与小组件历史使用一致的身份色；设置可关闭新 Profile 自动主题
- 预设 HTTP 代理配置（协议/Host/端口/认证/检测/历史）
- 无代理启动 / 使用 HTTP 代理启动 / 自定义启动向导
- 每个 Chrome 实例使用独立 `--user-data-dir`，Cookie/缓存/登录态天然隔离
- 独立调试端口（`--remote-debugging-port` 绑定 127.0.0.1）
- 本地插件加载（`--load-extension`，仅 Chrome for Testing / Chromium / Edge）
- 代理认证（临时 MV3 扩展注入，不写入命令行）
- CDP 截图 / 缩略图 / 页面标题记录
- 运行与历史管理（停止后保留页面标题，按 Dock 角标合并历史）
- 历史恢复（继承名称/角标/用户目录/参数/插件/上次页面）
- 浏览器来源管理（系统发现 + 自定义路径 + 安装 Chrome for Testing）
- 启动设置（调试端口/启动地址/WebRTC/通知/证书/附加参数）
- `state.json` 持久化 + ACL 权限限制为当前用户（对应 macOS 0600）
- 原生电脑控制：应用/窗口发现、UIA 控件树、窗口/主屏截图、后台点击/输入/滚动、前台输入、常驻 MCP 总开关与 UAC 权限修复

## 电脑控制与 MCP

在“全部管理 → 电脑控制”点击“授权开启”，或直接打开小组件中的电脑控制开关。首次自动准备控制组件，默认允许当前用户的全部应用、主屏截图、控件读取、点击、输入、按键和滚动，无需设置只读、应用范围或有效期。

总开关保持开启，直到用户主动关闭。没有授权期限或空闲超时；开关写入设置，YTray 重启、桌面解锁和控制组件重连后自动恢复，不会重放已投递的输入。

点击“复制电脑控制配置”，粘贴到支持 HTTP 和 Bearer 请求头的本机客户端。地址为 `http://127.0.0.1:51373/system/mcp`，与 `19222/mcp` 浏览器管理服务分开。配置在保持开启期间持续有效，组件崩溃、程序重启及结束一次客户端操作不会令它失效。主动关闭总开关后旧配置立即失效，下次开启生成新配置。凭据由 Windows DPAPI 加密保存，并限制当前用户访问。支持 MCP `2025-11-25` / `2025-06-18` 的 JSON POST 和通知 202，GET/SSE 未提供。

“修复控制权限”通过系统 `runas` 请求 UAC，确认后以管理员身份重启同一个 YTray，再检查和修复组件。取消 UAC 保持当前运行状态。重启保留总开关和 MCP 配置，已有浏览器进程继续运行。已在管理员模式时直接修复组件。锁屏、安全桌面、其他用户/会话及系统级窗口不接受输入。

小组件将电脑控制整理为紧凑卡片：滑动开关、状态、“复制电脑控制配置”和“高级”。复制后原位置短暂显示“已复制”；高级打开独立的电脑控制设置窗口，并复用已经打开的窗口。底部“MCP 配置”打开两个服务的总览。管理页提供授权开启/关闭、复制电脑控制配置、查看工具和权限修复。

核心工具共 11 个：`list_apps`、`list_windows`、`launch_app`、`activate_window`、`get_window_state`、`get_desktop_state`、`click`、`type_text`、`press_key`、`scroll`、`system_status`。客户端不需要创建、恢复或结束会话，也不需要维护请求编号、观察编号或截止时间。旧接口保留兼容，但不再出现在工具发现列表里。

直接调用 `launch_app({"app":"chrome","url":"https://baidu.com"})` 打开浏览器并进入网页，获得 `window_id`；已有窗口可通过 `list_windows` 查找。默认切到前台并保持目标窗口在前，最小化窗口自动恢复。`type_text` 可指定 `element_index` 聚焦输入框，`replace:true` 替换原有文本，`submit:true` 在输入成功后按 Enter。例如 `type_text({"window_id":123,"element_index":45,"text":"yaklang.io","replace":true,"submit":true})` 一次完成聚焦、替换、搜索，只返回最终结果。普通按键仍使用 `press_key({"window_id":123,"key":"ctrl+l"})`。

每次动作自动返回最新截图、带编号的控件和该应用的窗口列表，无需再调用一次观察。点击使用最新界面中的控件编号或截图像素坐标，不额外乘 DPI。前台操作会自动收起 YTray 的置顶小组件，避免它遮挡目标；组合输入的每一步都保留目标输入框，由底层确认焦点后再发送按键。窗口标题在观察后从 HWND 重新读取，避免页面跳转后仍显示旧标题。组件退出、底层连接失效和桌面重新可用时自动恢复；只会重试已确认尚未执行的输入，结果不明确时返回最新界面供客户端判断。

默认上下文为当前窗口的完整精简快照：只显示可见控件、短文本和当前值，去掉容器层级、隐藏扩展、屏幕外内容及重复标签；保留原控件编号。控件正文只在 `content[].text` 中出现一次，`structuredContent` 保留 `window_id`、标题、窗口列表、图片尺寸和裁剪信息，不再重复整棵树。读取具体控件可用 `get_window_state({"window_id":123,"query":"搜索"})`；`detail:"full"` 返回完整树，并兼容 `structuredContent.tree_markdown` 字段。快照不依赖客户端历史，也没有增量游标。

截图默认保留；明确不需要图片时，读取和输入动作可传 `include_screenshot:false`，仍返回最新控件。此时要使用图片坐标，须先读取一次带截图的状态。内部输入步骤之间只采集控件，页面加载时只轮询文本，最后采集一次截图。组合输入失败会停止后续步骤，保留底层具体错误原因和错误步骤；若提交失败但文本已输入，错误中会标记 `text_sent:true`，客户端不要重新输入整段文字。

每次应用窗口的最终观察带一行焦点与文本选区摘要，例如 `Focus: [45] | selected: "yaklang.io"`。选中文字最多显示 160 字符，正文不在元数据中重复；`structuredContent.focus` 只保留状态、控件编号和选区状态。窗口未在前台时明确返回 `other_window`，控件不支持选区或提供者未及时响应时返回 `unavailable`，不会把未知选区当作空选区。补充读取有 200 ms 上限，不参与内部输入预检和加载轮询，也不会改变焦点或选区。

`get_window_state` 可传 `region:{x,y,width,height}`，按控件边界读取与区域相交的可访问控件及文字。坐标沿用整个窗口图片的像素坐标，默认长边 1400；若截图使用其他 `max_image_dimension`，区域读取传相同值。区域坐标由当前窗口尺寸计算，不依赖之前的截图或客户端状态。区域读取默认不附截图；显式 `include_screenshot:true` 仍返回完整窗口图片，便于继续使用同一坐标系。原控件编号可直接用于下一次输入；`query`、`detail:"full"` 仍可结合使用。区域外的选中文字不带入结果，区域内也只报告选区状态。缺少文字或达到读取上限时会明确标记；图片/画布未暴露的文字不会被当作 OCR 结果。

同一完整 HTTP/HTTPS 目标的标题、URL、缩略图等链接在精简快照中只保留一个原始编号，优先保留焦点链接或可读标题。不同路径、查询参数、锚点保持区分；`duplicate_links_omitted` 报告合并数量，`detail:"full"` 保留全部别名。区域读取会核对前台控件的命中位置，剔除确认被编辑框/按钮覆盖的文字及重复 URL 别名；保留有名称的主要操作控件。刷新后也不会把已确认在屏幕外的标签重新作为无坐标文字带回来。

```json
{"window_id":123,"region":{"x":100,"y":105,"width":555,"height":22}}
```

上例直接取得搜索栏附近的精简文字，不需要先创建会话。激活窗口也支持 `include_screenshot:false`；常规动作继续自动返回最新状态。

后端使用固定的官方 Rust **cua-driver 0.38.0 集成候选（上游标记 prerelease）**，采用 `standard` 模式和明确的工具 allowlist，不限制应用资源，也不设置 expiry/idle timeout。只有独立测试的受限范围使用 resource manifest。使用私有 stdio 进程与创建时绑定的 kill-on-close Job Object，不启动共享 daemon，不安装感知模型，关闭 telemetry。版本、源码和 archive/EXE/helper 摘要见 `resources/cua-driver/runtime-manifest.json`。发布包内嵌经校验的 archive，可离线准备；源码未内嵌时按固定地址下载并校验。

当前支持 Windows x64，ARM64 与 32 位 Windows 未启用。x86 宿主到 x64 helper 的 stdio/Job 链路已有独立回归，真实应用、RDP、跨屏和不同 DPI 仍需扩大实机覆盖。复制配置用于本机客户端，网页跨来源接入尚未提供设置入口。桌面端点不公开任意 shell、文件路径、JavaScript 或 raw CDP；现有 Yakit 浏览器 Pipe 保留原有消息。

### 本机验证

构建与测试统一使用 .NET 10 SDK，项目由 `windows/global.json` 选择稳定的 10.0 SDK；开发时需安装 SDK，发布包包含自己的运行库。

```powershell
pwsh -File windows/build.ps1 -Release -Test -Architecture amd64
pwsh -File windows/test-system-control.ps1 -Configuration Release -Suite Protocol
pwsh -File windows/test-system-control.ps1 -Configuration Release -Suite Lifecycle
pwsh -File windows/test-system-control.ps1 -Configuration Release -Suite Authorization
pwsh -File windows/test-system-control.ps1 -Configuration Release -Suite Desktop -Interactive
pwsh -File windows/test-system-control.ps1 -Configuration Release -Suite AlwaysOn
windows/src/bin/Release/YTray.exe --capture-control-review windows/artifacts/control-ui-review
```

`Desktop -Interactive` 仅对独立 WPF fixture 输入，覆盖后台控件点击、中文/emoji、滚动、同标题窗口、缩放坐标和前台回车读回。`AlwaysOn` 使用真实组件空闲 130 秒，再验证崩溃自动恢复、客户端结束操作、重启后配置不变，以及主动关闭后撤销。协议/生命周期测试覆盖乱序回复、stderr 排空、坏帧、超时、父进程强杀和 Job 子进程清理。TRX 与 UI PNG 位于忽略提交的 `windows/artifacts/`。

## ★ Chrome 任务栏图标区分（AUMID）

macOS 版本通过 Dock 角标（A/B/C）区分实例图标。Windows 版本使用 **AppUserModelID (AUMID)**、窗口级 `RelaunchIconResource` 和启动前 WinEvent Hook 实现等价效果：

1. **启动前生成稳定实例身份**：AUMID 由浏览器类型、Dock 角标和持久化实例 UUID 组成，例如 `YTray.Chrome.InstA.<uuid>`；同一历史实例恢复后保持不变，不同实例不会合并到同一个任务栏组。
2. **启动前准备 ICO 和 `.lnk`**：`BrowserProcessIcon` 用 GDI+ 合成浏览器图标与彩色字母角标，快捷方式提前写入相同 AUMID 和 ICO。沿用 Windows 左上角布局，保留 Chrome for Testing 的右下标记；为 16–256px 输出独立 ICO 帧，按底色选择对比度至少 4.5:1 的黑字或白字。
3. **先启用 Hook，再启动 Chrome**：`BrowserWindowTaskbarController` 的独立 STA 线程先注册并运行 WinEvent 消息循环，确认 ready 后 `BrowserLauncher` 才调用 `Process.Start`，避免 Chrome 窗口抢在 Hook 前出现在任务栏。
4. **暂存首次展示**：Chrome 创建顶层窗口时使用 DWM cloak 暂时阻止画面呈现，同时临时设置 `WS_EX_TOOLWINDOW` 取消任务栏资格。这里不使用 `SW_HIDE`，不会打断 Chromium 的 GPU/DWM 首次初始化。
5. **写入并稳定窗口属性**：等待 Chrome 自己生成非空原生 AUMID 后，通过 `SHGetPropertyStoreForWindow` 写入实例 AUMID 和 `System.AppUserModel.RelaunchIconResource`。属性连续稳定 250ms 后恢复窗口样式并解除 cloak，因此第一枚任务栏图标就是带 A/B/C 角标的版本。
6. **持续管理新窗口**：控制器在浏览器进程生命周期内继续处理 `Ctrl+N` 等后续新窗口；进程退出或 YTray 关闭时恢复所有暂存窗口并释放 Hook。

如果当前系统无法建立 WinEvent/DWM 暂存，程序会回退到窗口可见后的 AUMID/ICO 设置，不影响浏览器实例启动。

## 目录结构

```text
windows/
├── YTray.sln
├── build.ps1                           # 构建 + 测试脚本
├── src/
│   ├── YTray.csproj
│   ├── App.xaml / App.xaml.cs           # 入口、命令行模式（--probe-aumid / --smoke-browser）
│   ├── Models/Models.cs                  # 数据模型（对等 Models.swift）
│   ├── Native/                           # P/Invoke（Win32 / ShellLink / WindowEnum）
│   ├── Core/                            # 核心逻辑
│   │   ├── InstanceStore.cs             # 状态管理 + 进程生命周期
│   │   ├── BrowserLauncher.cs           # 参数构建 + 进程启动
│   │   ├── AumidResolver.cs            # ★ 稳定实例 AUMID + Chromium 原生 AUMID 回退解析
│   │   ├── BrowserProcessIcon.cs        # 图标合成 + .lnk 创建
│   │   ├── BrowserWindowTaskbarController.cs # 启动前 Hook + DWM cloak + 窗口属性稳定
│   │   ├── SystemBrowserDiscovery.cs    # 浏览器发现（注册表 + Program Files）
│   │   ├── ScreenshotService.cs          # CDP 截图/导航/页面状态
│   │   ├── ProxyConnectivityChecker.cs  # 代理检测
│   │   ├── ProxyAuthenticationExtension.cs
│   │   ├── RuntimeInstaller.cs           # Chrome for Testing 下载安装
│   │   └── StatePersistence.cs           # state.json + ACL
│   └── Views/                           # WPF UI
│       ├── TrayApp.xaml.cs              # 托盘 + 菜单 + 小组件
│       ├── WidgetView.xaml              # 托盘小组件
│       ├── ManagerView.xaml             # 管理窗口
│       ├── CustomLaunchWizard.xaml      # 4 步启动向导
│       └── Pages/                       # 快速配置/运行时/设置/运行与历史/插件
└── tests/YTray.Tests/                   # MSTest（对等 BrowserLauncherTests）
```

## 构建与验证

### 依赖

- .NET 10 SDK；不要求 Visual Studio 或 .NET Framework 引用程序集
- NuGet 包在构建时自动还原（Newtonsoft.Json / MSTest 等）
- `-Package` 使用 .NET 自包含单文件发布，将 Desktop Runtime、HandyControl、Newtonsoft.Json 等依赖打包进单个 `YTray.exe`；关闭裁剪，保留 WPF 和反射所需类型
- `-Package` 会读取 OSS 发布清单的 `latest`，下载并校验对应的 Yakit Browser Agent，再作为资源嵌入同一个 EXE
- 正式版启动后会读取 YTray OSS `latest.json`；用户可在设置页查看下载进度，安装包通过大小与 SHA-256 校验后由 Inno Setup 静默替换并自动重启

### 构建

```powershell
pwsh -File windows/build.ps1
# Release:
pwsh -File windows/build.ps1 -Release
# 生成单文件 windows/artifacts/amd64/YTray-{版本}-windows-amd64.exe：
pwsh -File windows/build.ps1 -Release -Test -Package
# 构建 + 测试:
pwsh -File windows/build.ps1 -Test
```

或直接使用 SDK：

```powershell
dotnet build windows\YTray.sln -c Debug
```

### 测试

```powershell
pwsh -File windows/build.ps1 -Test
```

测试覆盖：应用更新版本比较与架构选择、Dock 角标序列/校验、浏览器类型推断、HTTP 代理规范化、代理探测请求/响应解析、启动参数隔离边界、自定义参数防护、插件加载、Chrome for Testing 标志、会话恢复、命令行扩展能力、稳定实例 AUMID、Windows Shell PropertyKey 和 PROPVARIANT 生命周期。

Windows 便携产物为一个自包含 `YTray.exe`，同时带有经过大小和 SHA-256 校验的 Yakit Browser Agent 和电脑控制组件。目标机器无需安装 .NET；首次启动时 .NET 会将所需原生运行库释放到当前用户临时目录。浏览器插件首次启动会默认启用，之后仍可在插件页更新、重新下载或移除。安装包明确拒绝 Windows 10 1809 以前的系统。

### 命令行模式

```powershell
# 探测 Chrome 窗口的真实 AUMID 并与 Chromium 规则复算值比对
YTray.exe --probe-aumid "C:\Program Files\Google\Chrome\Application\chrome.exe"

# 端到端冒烟：启动真实 Chrome 实例，验证 AUMID 解析 + CDP 截图
YTray.exe --smoke-browser "C:\Program Files\Google\Chrome\Application\chrome.exe"
```

## 实例主题与外观验证

新 Profile 默认接收与角标相同的 Chromium 自动主题。设置中的“使用 A/B/C/D 独立主题色”只影响之后创建的 Profile；历史恢复（包括代理认证的启动流程）不会重新安装主题，因此保留用户已有的外观选择。关闭此选项也不会去掉任务栏和列表中的彩色身份标识。

```powershell
# 使用隔离的 A–F 测试数据，渲染所有页面、向导、明暗小组件、下拉菜单、历史、空状态和缩略图
windows/src/bin/Release/YTray.exe --capture-design-review windows/artifacts/design-review

# 本机 Chrome 真机验证：A–D 颜色落盘、窗口 AUMID/图标资源、CDP 页面及修改主题后的历史恢复
windows/src/bin/Release/YTray.exe --smoke-identities "C:\Program Files\Google\Chrome\Application\chrome.exe" windows/artifacts/identity-smoke
```

设计验证输出 `capture-complete.txt`、截图索引与联系表；真机验证输出 `identity-smoke.json`、浏览器截图和 `smoke-complete.txt`。失败返回非零退出码并输出错误文件。两种模式的测试状态只保存在指定目录；真机模式会关闭自己启动的测试浏览器，保留测试 Profile 供复核。自动测试另外验证原有小组件在明暗/系统主题间切换后仍使用正确的文字、操作图标和输入框边框颜色。

## Chrome 插件加载与故障恢复

YTray 不再按浏览器名称直接拒绝加载插件。启动后使用浏览器级 CDP `Extensions.getExtensions` 检查已启用的插件；缺失或禁用的插件通过 `Extensions.loadUnpacked` 加载，并核对返回的 ID、目录和启用状态。已加载插件不会重复加载。普通新版 Chrome 不再携带会干扰新接口的旧 `--disable-extensions-except` 参数；支持旧命令行方式的浏览器保留原有路径。

启动期间先停留在空白页，插件就绪后再打开内置插件初始化页或目标页面。Chrome 的代理认证插件也使用同一加载机制。自定义向导允许普通 Chrome 选择插件，历史恢复保留现有 Profile 和主题。

仅在实际加载失败后显示完整错误原因。选择“不加载插件并启动”会关闭未完成的浏览器，并重新启动本次配置；同时禁用该次运行中的扩展，避免旧 Profile 或部分成功加载的插件残留。全局插件设置不变。取消会清理未完成的实例、保留旧历史。代理认证插件未就绪时不允许跳过，防止认证失效后仍打开目标页。

本地真实 Chrome 验证可在测试前设置以下环境变量（使用独立临时 Profile，并关闭自己创建的浏览器）：

```powershell
$env:YTRAY_TEST_CHROME = 'C:\Program Files\Google\Chrome\Application\chrome.exe'
$env:YTRAY_TEST_CAPTURE = "$PWD\windows\artifacts\extension-fallback-review"
pwsh -File windows/build.ps1 -Test
```

该验证实际点击小组件、向导和确认弹窗，检查直连、自定义启动、历史恢复的插件启用状态、页面脚本注入、内置插件的实例身份绑定及截图。代理测试使用不会向外转发的本地测试服务器，真实返回 HTTP 407 并核对 Chrome 提交的认证信息。还覆盖部分插件加载失败后的取消、重试、历史保留和认证阻断。输出包含明暗弹窗截图和 `extension-api-smoke.txt`。详情见 [接口验证说明](docs/chrome-extension-api-review.md)。

## 数据位置

```text
%LOCALAPPDATA%\YTray\
├── state.json              # 运行时/插件/实例/设置（ACL 限制为当前用户）
├── Profiles\Inst{Badge}\   # 每个 Chrome 实例的独立用户目录
├── ProcessIcons\           # 合成图标 .ico/.png + 实例 .lnk
├── Thumbnails\             # 自动缩略图
├── ProxyAuth\              # 代理认证临时扩展
├── Runtimes\               # 安装的 Chrome for Testing
└── Logs\                   # 实例启动日志
```

手动截图保存在 `%USERPROFILE%\Pictures\YTray\`。
