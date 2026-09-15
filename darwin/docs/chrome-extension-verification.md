# macOS Chrome 插件加载与恢复验证

对照 [Windows PR #14](https://github.com/yaklang/ytray/pull/14) 的 `081c053`，基于主线 `a564b38` 完成 macOS 对应修复，并保留 PR #15 的代理端点校验与 `startupProxy` 配置传递。

## 行为

- 普通 Chrome、Beta、Canary 可以选择本地和内置插件。先通过浏览器级 CDP 的 `Extensions.getExtensions` 检查，再用 `Extensions.loadUnpacked` 补载缺失或禁用的插件，检查返回 ID、路径与启用状态。
- Chrome 137+ 不再携带会干扰 CDP 加载的旧 `--disable-extensions-except` 参数。旧浏览器只有在扩展接口明确不可用、且支持命令行加载时才保留旧方式；权限错误、错误清单、错误 ID 和加载失败不会被忽略。
- 插件验证前只打开空白页。带插件的历史恢复也在验证之后打开保存的最近页面，不在验证前自动恢复其他标签页。Profile、实例 ID、角标、保存的主题与登录数据保留。
- 实际失败后关闭未完成的浏览器，再提供“取消启动”和“不加载插件并启动”。重试禁用本次全部扩展，覆盖已部分加载或已存在于历史 Profile 的扩展；全局默认设置保持不变。代理认证依赖插件，不能跳过。
- 失败的新 Profile 会删除，恢复失败保留原历史。历史合并会排除待清理实例；旧 `Process` 的退出通知不会清理替换实例的图标或状态。
- macOS 的配色和新 Profile 主题已在主线实现。真机回归确认 B 的蓝色主题，并确认恢复保留用户修改的主题。
- 路径型扩展 ID 使用 POSIX `realpath`，与 Chrome 一致。Foundation 的路径规范化会把部分 `/private/var` 路径转回 `/var`，不能直接用于计算 ID；符号链接去重与工具栏固定也覆盖了此场景。

## 自动测试

```bash
swift test --package-path darwin
```

未设置真实浏览器环境变量时，8 项集成测试会明确跳过。协议、参数、失败提示、符号链接与原有单元测试照常运行。

完整真机回归（将浏览器路径替换为本机安装位置）：

```bash
swift build --package-path darwin
./script/prepare-yakit-browser-agent.sh /tmp/ytray-test-bundled
mkdir -p /tmp/ytray-test-agent
unzip -oq /tmp/ytray-test-bundled/yakit-browser-agent.zip -d /tmp/ytray-test-agent

YTRAY_CHROME_PATH='/Applications/Google Chrome.app/Contents/MacOS/Google Chrome' \
YTRAY_LAUNCHER_PATH="$PWD/darwin/.build/debug/YTray" \
YTRAY_TEST_EXTENSION_PATH='/tmp/ytray-test-agent' \
YTRAY_CFT_PATH='/path/to/Google Chrome for Testing.app/Contents/MacOS/Google Chrome for Testing' \
swift test --package-path darwin
```

测试创建独立的临时 Profile，只关闭自己创建的浏览器。代理测试在本机响应真实 HTTP 407、检查 Basic 凭据并返回测试页面，不向外转发请求。内置 Agent 的测试页面使用本机 HTTP 服务，并从其 service worker 读取存储，确认实例身份已写入后再检查页面与截图。

## 本次验证结果

2026-09-15，本机 Apple Silicon macOS：

- 全量 **88 项测试通过，0 失败、0 跳过**。其中新增 17 项协议/界面回归、7 项 Chrome 集成测试；原有 Chrome for Testing 代理认证集成测试也已启用。
- 真机：Google Chrome **152.0.7977.84**、Chrome for Testing **152.0.7977.64**、Yakit Browser Agent **0.2.4**。
- 覆盖自定义启动、快速启动、内置插件身份初始化、页面跳转/截图、历史恢复、自选主题、部分加载后取消、新 Profile 无插件重试、已有 Profile 无插件重试、默认插件状态保留、旧退出通知、代理认证和认证失败前零目标请求。
- 原生 AppKit 弹窗实测：浅色/深色可读，长错误可以滚到底部，两个动作保持可见；带认证代理只显示取消按钮。界面测试直接使用生产 `ExtensionLaunchPrompt` 源码创建隔离窗口。
- Apple Silicon `arm64` 与 Intel `x86_64` Release 构建通过，编译无警告。Intel 产物完成交叉编译，本次未在 Intel 真机运行。
- Apple Silicon Release 版 `--smoke-launch-state` 通过：加载状态 → 启动完成 → 停止入历史 → 恢复同一 Profile、角标和页面。
- Release 版小组件、自定义启动向导和插件管理页渲染通过，已逐图检查。

Chrome 136/137 的边界及旧 CDP 不可用路径由协议测试模拟；本次没有在旧版 Chrome、Beta、Canary 或 Edge 上逐一真机运行。

## 参考

- [Chrome DevTools Protocol Extensions](https://chromedevtools.github.io/devtools-protocol/tot/Extensions/)
- [Chromium 扩展 ID 的计算](https://github.com/chromium/chromium/blob/main/components/crx_file/id_util.cc)
