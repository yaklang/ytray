# YTray for Windows

Windows 原生实现（C# / WPF / .NET Framework 4.8.1），与 macOS 版本（`darwin/`）分别维护各自的托盘、窗口、进程管理和浏览器路径逻辑。

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
├── src/YTray/
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

- Visual Studio 2022 Build Tools（MSBuild + Roslyn csc）
- .NET Framework 4.8.1 引用程序集（随 VS BuildTools 安装）
- NuGet 包通过 MSBuild 自动还原（Newtonsoft.Json / MSTest）
- Release 使用 Costura/Fody 将 HandyControl、Newtonsoft.Json 等托管依赖嵌入单个 `YTray.exe`
- `-Package` 会读取 OSS 发布清单的 `latest`，下载并校验对应的 Yakit Browser Agent，再作为资源嵌入同一个 EXE
- 正式版启动后会读取 YTray OSS `latest.json`；用户可在设置页查看下载进度，安装包通过大小与 SHA-256 校验后由 Inno Setup 静默替换并自动重启

### 构建

```powershell
pwsh -File windows/build.ps1
# Release:
pwsh -File windows/build.ps1 -Release
# 生成可直接分发的单文件 windows/artifacts/YTray.exe：
pwsh -File windows/build.ps1 -Release -Test -Package
# 构建 + 测试:
pwsh -File windows/build.ps1 -Test
```

或直接用 MSBuild：

```powershell
$msbuild = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
& $msbuild windows\YTray.sln -p:Configuration=Debug -restore -nologo -v:minimal
```

### 测试

```powershell
pwsh -File windows/build.ps1 -Test
```

测试覆盖：应用更新版本比较与架构选择、Dock 角标序列/校验、浏览器类型推断、HTTP 代理规范化、代理探测请求/响应解析、启动参数隔离边界、自定义参数防护、插件加载、Chrome for Testing 标志、会话恢复、命令行扩展能力、稳定实例 AUMID、Windows Shell PropertyKey 和 PROPVARIANT 生命周期。

CI 上传的 Windows Release artifact 只包含 `YTray.exe`，其中同时带有经过大小和 SHA-256 校验的 Yakit Browser Agent。首次启动会释放并默认启用该插件，之后仍可在插件页更新、重新下载或移除。目标机器仍需安装 Windows 自带/系统提供的 .NET Framework 4.8.1；第三方托管 DLL 不需要与 exe 一起分发。

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

## Chrome 插件兼容性确认

普通 Chrome、Chrome Beta 和 Chrome Canary 无法通过 YTray 自动加载本地插件时，启动前会显示完整说明。选择“不加载插件并启动”仅跳过本次选中的本地插件（包括内置插件），保留代理、主页、角标、主题和其他参数；全局插件启用状态保持不变。取消或关闭弹窗不会创建实例或显示启动失败。该确认覆盖小组件、托盘、快速启动、自定义启动及历史恢复。

带账号密码的代理同样依赖认证插件，此时不提供跳过插件的按钮，并提示使用兼容浏览器或无需认证的代理。

自动测试覆盖明暗弹窗、取消、兼容浏览器不弹窗及认证保护。本地真实 Chrome 验证可在测试前设置以下环境变量（测试使用独立临时 Profile，并关闭自己创建的浏览器）：

```powershell
$env:YTRAY_TEST_CHROME = 'C:\Program Files\Google\Chrome\Application\chrome.exe'
$env:YTRAY_TEST_CAPTURE = "$PWD\windows\artifacts\extension-fallback-review"
pwsh -File windows/build.ps1 -Test
```

该验证实际点击小组件和确认弹窗，检查直连、HTTP 代理、自定义启动、历史恢复的 CDP 页面和截图，并验证插件默认设置仍能保存和重载。HTTP 代理用本地地址和内嵌页面测试参数保留，不验证外部代理连通性。输出包含明暗弹窗截图和 `chrome-fallback.txt`。

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
