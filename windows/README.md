# YTray for Windows

Windows 原生实现（C# / WPF / .NET Framework 4.8.1），与 macOS 版本（`darwin/`）分别维护各自的托盘、窗口、进程管理和浏览器路径逻辑。

## 功能

与 macOS 版本对等：

- 托盘图标 + 左键弹出小组件、右键菜单
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

macOS 版本通过 Dock 角标（A/B/C）区分实例图标。Windows 版本使用 **AppUserModelID (AUMID)** 实现等价效果，采用稳妥的三步方案：

1. **从已安装 Chrome 快捷方式读取基础 AUMID**：`ShellLink.ResolveBaseAumid` 通过 `IShellLinkW` + `IPersistFile` + `IPropertyStore` 读取 Chrome `.lnk` 中的 `System.AppUserModel.ID`；失败时回退到按浏览器类型推断的默认值（`Chrome` / `Chromium` / `MicrosoftEdge`）。
2. **启动后读取首个 Chrome 顶层窗口的真实 AUMID**：`WindowEnum.PollForWindowAumid` 枚举 `Chrome_WidgetWin_1` 类窗口并匹配 PID，通过 `SHGetPropertyStoreForWindow` + `IPropertyStore.GetValue(PKEY_AppUserModel_ID)` 读取 Chrome 自己设置的 AUMID。
3. **按 Chromium profile ID 规则复算目标值**：`AumidResolver.ComputeProfileId` 复现 Chromium 的 `GetProfileIdFromPath` 规则——`profile_id = parent_basename + "." + profile_basename`，仅保留 `[A-Za-z0-9.]`；目标 AUMID = `基础AUMID + "." + profile_id`（如 `Chrome.InstA.Default`）。
4. **写入实例 metadata 持久化**：`AumidResolver.ResolveAsync` 优先取窗口真实 AUMID，回退到复算值，最终存入 `BrowserInstance.AppUserModelId` 并持久化到 `state.json`。

每个实例使用唯一 `--user-data-dir`（basename = `Inst{badge}`，如 `InstA`），Chrome 自动生成不同 AUMID → 任务栏天然分组。`BrowserProcessIcon` 用 GDI+ 合成 Chrome 图标 + 橙色圆形角标（白色 A/B/C 字母），并创建携带该 AUMID + 合成图标的 `.lnk`，使任务栏按钮显示带角标的图标。

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
│   │   ├── AumidResolver.cs            # ★ AUMID 解析（窗口读取 + Chromium 规则复算 + 持久化）
│   │   ├── BrowserProcessIcon.cs        # 图标合成 + .lnk 创建
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

### 构建

```powershell
pwsh -File windows/build.ps1
# Release:
pwsh -File windows/build.ps1 -Release
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

测试覆盖：Dock 角标序列/校验、浏览器类型推断、HTTP 代理规范化、代理探测请求/响应解析、启动参数隔离边界、自定义参数防护、插件加载、Chrome for Testing 标志、会话恢复、命令行扩展能力、AUMID profile-ID 规则。

### 命令行模式

```powershell
# 探测 Chrome 窗口的真实 AUMID 并与 Chromium 规则复算值比对
YTray.exe --probe-aumid "C:\Program Files\Google\Chrome\Application\chrome.exe"

# 端到端冒烟：启动真实 Chrome 实例，验证 AUMID 解析 + CDP 截图
YTray.exe --smoke-browser "C:\Program Files\Google\Chrome\Application\chrome.exe"
```

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