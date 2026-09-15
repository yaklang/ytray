# Windows Chrome 扩展接口验证

## 行为

旧版 Windows 通过浏览器类型判断拒绝普通 Chrome 加载本地插件。现在先保留可用的命令行加载方式，随后连接该实例的浏览器级 CDP endpoint：

1. `Extensions.getExtensions` 返回插件 ID、路径、名称和启用状态，用于判断现有 Profile 或旧参数是否已加载插件。
2. 缺失或禁用项使用 `Extensions.loadUnpacked`，接收浏览器返回的插件 ID。
3. 再次查询列表，核对 ID、路径和启用状态；不以“进程创建成功”或“接口返回 ID”代替加载成功。
4. 插件就绪后才进入内置 Yakit Browser Agent 初始化页或目标页，避免代理认证和实例身份绑定抢跑。

新接口为浏览器级命令，页面级连接不具备同样的加载能力。当前 Chrome 152 可直接使用本地 WebSocket endpoint；没有增加远程调试暴露范围或额外的 unsafe-debugging 参数。请求有超时、消息大小限制和进程生命周期取消。

对缺少新接口的旧浏览器，只在原本支持命令行加载的版本/发行版上保留旧路径。其他情况呈现实际错误，并允许本次不加载插件。实际验证的是本机 Chrome 152.0.7977.83；旧版接口缺失及版本边界通过模拟协议测试覆盖。

## 本机发现并修复的问题

- Chrome 152 忽略旧 `--load-extension`，但可通过 `Extensions.loadUnpacked` 启用插件。
- 在完整 GUI 启动参数下保留 `--disable-extensions-except`，会出现返回插件 ID 但插件列表仍为空的情况。新版普通 Chrome 去掉这个旧参数，加载后仍核对状态。
- 代理认证启动不再使用无法通过 .NET URL 校验的未转义 data URL；启动阶段统一使用空白页，认证插件就绪后才导航。
- 未完成的启动停止时，不再让同角标历史去重逻辑删除此前成功的历史记录。
- 旧进程的异步退出回调先核对进程归属，避免同一个历史实例重试时误标记新进程为停止。
- 无插件重试显式禁用扩展，覆盖部分加载成功和旧 Profile 已装插件的情况；不删除原有 Profile。

## 验证

Debug x64、Release x64 和 Release x86 构建通过。99 项 MSTest 中 98 通过，1 项已有的联网诊断默认跳过。

真实 Chrome 验证使用临时独立 Profile、内置 Yakit Browser Agent 0.2.4，以及包含中文和空格路径的 MV3 测试插件：

- 小组件直连启动、自定义向导、历史恢复：核对实际启用的插件 ID、页面内容脚本注入、内置插件存储中的实例 ID、CDP 页面标题和截图。
- 代理认证：本地测试代理返回 407，验证浏览器发送正确的 Basic 认证信息并完成页面加载；代理不向外转发请求。
- 第三个无效插件触发部分加载失败：测试取消、重新启动为无插件实例、全局设置保留、旧历史保留。
- 已有插件的历史 Profile：无插件重试保留原路径，并验证没有启用的本地插件。
- 带认证的失败启动：确认目标页未请求、不提供不带认证的重试。
- 明暗弹窗全文换行、长错误滚动、按钮颜色和可见性；普通 Chrome 自定义向导的插件选项可操作。
- 协议模拟：已加载项不重复加载、缺失项/禁用项补载、重复路径去重、错误 ID、路径不匹配、仍未启用、接口不可用及 Chrome 136/137 边界。

复现环境变量和命令见 [Windows README](../README.md#chrome-插件加载与故障恢复)。普通 CI 执行单元/WPF 回归；设置 `YTRAY_TEST_CHROME` 后附加上述真实浏览器验证，结果保存在 `extension-api-smoke.txt` 和截图中。

## 参考

- [Chrome 官方旧参数移除说明](https://developer.chrome.com/blog/extension-news-june-2025)
- [Chrome DevTools Protocol 扩展接口定义](https://github.com/ChromeDevTools/devtools-protocol/blob/master/pdl/domains/Extensions.pdl)
- [Chromium 扩展加载及状态查询实现](https://github.com/chromium/chromium/blob/main/chrome/browser/devtools/protocol/extensions_handler.cc)
