# YTray for Linux

Linux 端实现，技术栈为 Electron + TypeScript + React。功能语义与 macOS/Windows 版本保持一致，
但窗口、托盘、开机启动和更新机制完全按 Linux 桌面重写。

## 与其它平台的关系

三端是**独立实现**，共享的是行为契约而不是代码：

| 能力 | macOS | Windows | Linux |
| --- | --- | --- | --- |
| 隔离用户目录 / 调试端口 | Swift | C# | TypeScript（`src/main/launcher.ts`） |
| CDP 截图 / 缩略图 | Swift | C# | TypeScript（`src/main/cdp.ts`） |
| 插件安装与 CDP 补载 | Swift | C# | TypeScript（`extension-*.ts`） |
| 代理认证扩展 | MV3 | MV3 | MV3（`proxy-auth.ts`） |
| MCP 服务 | 裸 socket | 裸 socket | `node:http`（`mcp.ts`） |
| 开机启动 | Login Items | 注册表 | XDG autostart |
| 应用内更新 | Sparkle + DMG | WinSparkle + Setup | 自研 AppImage 替换 |

## 本地开发

需要 Node.js 24+ 与 npm。

```bash
cd linux
npm install
npm run dev
```

开发构建使用独立数据目录 `~/.local/share/YTrayDev`，不会改动开机启动项。

渲染层自检（不弹窗、不截取桌面，输出 PNG 到 `dist/`）：

```bash
npm run smoke:ui
```

## 构建 AppImage

```bash
cd linux
npm run sync-version                       # 从 ../VERSION 同步版本号
bash scripts/prepare-bundled-extension.sh  # 可选：内置 Yakit 插件
npm run package
```

产物位于 `linux/dist/YTray-<version>-linux-<arch>.AppImage`。首次运行：

```bash
chmod +x YTray-*.AppImage
./YTray-*.AppImage
```

`npm run package:dir` 只生成未打包目录，便于排查打包问题。

透明托盘窗口在部分驱动/合成器上会让 GPU 进程崩溃并带崩整个应用，因此默认关闭硬件加速；
确认本机渲染正常后可用 `YTRAY_ENABLE_GPU=1` 重新开启。

## 数据目录

遵循 XDG 规范（`$XDG_DATA_HOME`，缺省 `~/.local/share`）：

```text
~/.local/share/YTray/
├── Profiles/       # 独立浏览器用户目录
├── Plugins/        # 本地与内置插件
├── Runtimes/       # YTray 安装的 Chrome for Testing
├── ProxyAuth/      # 临时代理认证扩展（实例停止后清理）
├── Screenshots/    # 用户导出的截图
├── Thumbnails/     # 自动缩略图
├── Logs/           # 运行日志与实例日志
└── state.json      # 配置与历史（0600）
```

## 平台能力边界

Linux 桌面（尤其是 Wayland）与 macOS/Windows 存在结构性差异，以下行为是**有意降级**的：

- **每实例窗口图标**：macOS 通过 `execv` 抢占 Dock 图标，Windows 使用 AUMID。Linux 只能写入
  `~/.local/share/applications/ytray-<class>.desktop` 并给 Chromium 传 `--class=YTray-<badge>`，
  由桌面环境按 `StartupWMClass` 匹配。GNOME 与部分 Wayland 合成器会忽略客户端图标。
- **贴边面板**：`EdgeDock` 未实现。Wayland 禁止客户端自行定位常驻窗口，改为托盘菜单唤出的小组件。
- **窗口聚焦**：不使用 `wmctrl` 等外部工具，改为 CDP `Target.activateTarget`，只保证标签页被激活。
- **开机启动**：写 `~/.config/autostart/ytray.desktop`，不修改系统级服务。Flatpak 环境下该路径被沙箱重定向。
- **更新**：仅 AppImage 支持应用内替换；其它安装形式请手动下载。

## MCP

默认随应用启动，监听 `127.0.0.1:19222`：

```text
http://127.0.0.1:19222/mcp
```

工具与 macOS/Windows 版本一致：`list_browser_runtimes`、`list_browser_instances`、
`open_browser_instance`、`focus_browser_instance`、`stop_browser_instance`。
服务校验 `Host` 与 `Origin`，并同时支持 2025-11-25 及更早的旧版协议与 2026-07-28 新版握手。

## 代码结构

```text
src/main/       Electron 主进程：实例管理、启动、CDP、MCP、更新
src/preload/    contextBridge，只暴露白名单 API
src/renderer/   React 界面（管理窗口 + 托盘小组件）
src/shared/     主进程与渲染层共享的类型契约
```

## 常见问题

**运行后没有窗口。** 先看日志：

```bash
tail -n 20 ~/.local/share/YTrayDev/Logs/ytray.log   # 源码构建
tail -n 20 ~/.local/share/YTray/Logs/ytray.log      # AppImage
```

正常应出现 `[window.manager] shown via ...; visible=true`。若进程直接退出，多半是第二个实例触发了
单实例锁（`pgrep -f ytray` 检查残留进程）；若窗口存在但看不见，检查是否被 `--background`
参数启动，或桌面缺少托盘支持。

**AppImage 双击无反应。** 确认已 `chmod +x`；某些内核禁用了用户命名空间时需要
`./YTray-*.AppImage --no-sandbox`；也可以用 `--appimage-extract-and-run` 绕过 FUSE。

## 尚未实现

- Yakit 联动的启动审批（`claim_launch_approval`）
- 每实例角标美术图标（当前复用应用图标）
- 发行签名与 OSS 发布索引（CI 目前只产出 AppImage artifact）
