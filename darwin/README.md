# YTray for macOS

YTray 是使用 Swift、AppKit 和 SwiftUI 实现的 macOS 浏览器工作台，同时提供本机电脑控制 MCP。它把独立浏览器身份、代理、插件、历史恢复和 AI 桌面操作放在一个菜单栏应用中。

要求 **macOS 14 或更新版本**，支持 Apple Silicon 和 Intel。安装包见 [GitHub Releases](https://github.com/yaklang/ytray/releases/latest)，项目介绍见[仓库首页](../README.md)。本文描述当前源码实现，已发布版本可能尚未包含全部功能。

## 从哪里开始

### 只使用浏览器

1. 安装并打开 `YTray.app`，点击菜单栏图标打开“浏览器与电脑控制”小组件，或打开“全部管理”。
2. 在“浏览器运行时”中选择本机浏览器、添加本地浏览器，或下载安装 Chrome for Testing。
3. 点击“无代理启动”，或配置代理后点击“使用 HTTP 代理启动”；需要逐项设置时使用自定义启动向导。
4. 在“运行与历史”中聚焦、截图、停止实例；停止后可从历史恢复原来的身份和最近页面。

使用这些功能不需要开启电脑控制，也不需要授予辅助功能或屏幕录制权限。浏览器页面截图通过 CDP 获取，与系统桌面截图是两个不同功能。

### 让 AI 操作电脑

1. 使用完整的 `YTray.app` 或 `YTrayDev.app`，打开小组件中的“电脑控制”开关。
2. 按授权窗口指引，为 **当前这个 YTray 应用** 开启“辅助功能”和“屏幕录制”。
3. 返回 YTray 点击“检查并开启”。若 macOS 要求重开应用，点击“重启并应用权限”，重开后再次开启电脑控制。
4. 点击小组件右上角“MCP 复制”，选择“电脑控制”，确认本地 MCP 服务正在运行并复制配置。
5. 将配置添加到支持 HTTP MCP 和请求头的 AI 客户端。先让 AI 发现窗口、观察状态，再执行动作。
6. 完成后关闭开关或点击“停止控制”；客户端也可以调用 `end_session`。

YTray 提供工具和执行能力，模型由 MCP 客户端选择和调用。使用 YTray 本身不要求绑定某一个模型，也不需要把模型 API Key 填入 YTray。

## 界面与日常功能

| 入口 | 能做什么 |
| --- | --- |
| 菜单栏小组件 | 预设代理、连接检测、直连或代理启动、运行实例、最近历史、电脑控制开关、MCP 配置、固定面板、开机启动 |
| 贴边入口 | 从屏幕边缘展开同一个小组件，或展开直连与代理启动按钮 |
| 快速配置 | 查看默认运行时、启动地址、调试端口和插件，快速启动或进入向导 |
| 浏览器运行时 | 发现系统浏览器、添加本地运行时、下载或删除托管 Chrome for Testing、选择默认运行时 |
| 启动设置 | 默认浏览器、身份数据目录、首页、代理、默认插件、实例主题、Yakit 自动连接和 Chrome 高级参数 |
| 运行与历史 | 查看 PID、调试端口、版本与页面信息，聚焦、截图、停止、恢复、重命名或删除记录 |
| 电脑控制 | 开启/停止、复制 MCP 配置、查看当前与最近操作；展开权限设置或查看 AI 读到的画面与控件 |
| 插件管理 | 安装已解压插件、管理默认加载项、查看或更新内置 Yakit Browser Agent |
| MCP | 管理已有浏览器实例管理服务，复制其客户端配置并查看五个管理工具 |
| MCP 接入与函数窗口 | 管理页面观察与电脑控制网关、修改端口、查看工具参数、复制带认证信息的连接配置 |
| 开机启动 | 查看登录项真实状态、开启、重试或确认关闭 |
| 诊断日志与更新入口 | 打开运行日志、检查更新、查看下载和安装状态 |

小组件默认失焦隐藏；固定后保持显示，代理检测期间会临时固定。关闭管理窗口后，YTray 继续驻留菜单栏；再次打开应用会显示管理窗口。

### 独立浏览器身份

- 支持本机 Chrome、Chrome Beta、Chrome Canary、Chrome for Testing、Chromium 和 Edge，也可以添加本地运行时。
- 每个新实例分配独立用户目录，分别保存 Cookie、Local Storage、缓存、插件数据和登录态。
- 历史恢复继续使用原 Profile、身份、插件设置、角标与主题，并恢复保存的最近页面。
- CDP 调试端口绑定 `127.0.0.1`，从配置的起始端口向后避让占用。PID、运行时、端口和页面信息可在实例列表查看。
- 支持实例配色和 Dock 角标，方便区分不同账号或测试环境。
- YTray 使用启动参数控制所选浏览器，不修改系统浏览器的名称、Bundle ID、签名或默认浏览器设置。

### 代理与启动设置

预设代理支持 HTTP/HTTPS 地址、端口、用户名、密码、备注和最近配置，可以先检测连接再启动。直连和代理启动是不同按钮，启动时明确使用哪一套网络设置。

自定义启动向导可以选择运行时、代理、插件、角标和启动行为。高级设置提供 WebRTC 限制、通知控制、忽略证书错误、测试模式等开关，以及每行一个的 Chrome 附加参数。Profile、调试端口、代理和插件相关的保留参数由 YTray 管理，不能通过附加参数覆盖。

### 插件与 Yakit 协作

安装包内置经过下载校验的 Yakit Browser Agent，也支持导入已解压的本地插件。启动时检查实际加载状态，必要时尝试通过 CDP 加载扩展，完成检查后再打开目标页面。

插件实际加载失败时，可以选择本次不加载插件继续启动；该选择不会关闭全局默认插件。带用户名或密码的代理依赖认证插件，不能跳过认证插件继续启动。旧浏览器保留其支持的命令行加载方式，兼容边界见 [Chrome 插件验证](docs/chrome-extension-verification.md)。

可启用自动连接 Yakit；浏览器扩展配对与第一方本机控制 Socket 继续独立工作，不要求开启桌面控制会话。

## MCP：选择正确的入口

当前有三组能力。已有浏览器管理服务保留原地址；页面观察和电脑控制使用新增网关。它们的工具、配置和开关不同。

| 能力 | 默认地址 | 配置入口 | 认证与启用条件 |
| --- | --- | --- | --- |
| 浏览器实例管理 | `http://127.0.0.1:19222/mcp` | 全部管理 → MCP | 仅本机监听，校验 Host/Origin；由该页面独立控制服务与自动启动 |
| 浏览器页面观察与 JavaScript | `http://127.0.0.1:51373/mcp` | 小组件 → MCP 复制 → 浏览器 | Bearer 令牌；网关默认随 YTray 启动 |
| 原生电脑控制 | `http://127.0.0.1:51373/system/mcp` | 小组件 → MCP 复制 → 电脑控制 | 独立的临时 Bearer 令牌；需要本机开启控制会话和网关 |

`51373` 可以在“MCP 接入与函数”窗口修改，修改后需要重新复制配置。关闭网关会停止这两个地址的 HTTP 接入；关闭电脑控制会撤销桌面会话令牌。开启 MCP 网关本身不会开启电脑控制。

端口被占用时会显示启动失败，可修改网关端口或处理占用；浏览器实例管理服务的 `19222` 为固定端口。**MCP 端口和浏览器 CDP 调试端口不是同一个配置。**

### 浏览器实例管理：5 个工具

| 函数 | 功能 |
| --- | --- |
| `list_browser_runtimes` | 列出可用运行时和默认运行时 |
| `list_browser_instances` | 列出运行中的浏览器，可选择包含历史 |
| `open_browser_instance` | 使用默认或指定运行时新建实例，或按实例 ID 恢复历史 |
| `focus_browser_instance` | 将指定运行实例切到前台 |
| `stop_browser_instance` | 停止实例并保留历史 |

### 浏览器页面：4 个工具

| 函数 | 功能 |
| --- | --- |
| `list_instances` | 列出 YTray 当前运行的浏览器与 CDP 端口 |
| `get_cdp_info` | 获取实例版本、标签页、CDP WebSocket 地址 |
| `get_page_content` | 读取当前可见页面的标题、URL、文本和可选 HTML，支持限制文本长度 |
| `evaluate_javascript` | 在指定实例的当前页面执行 JavaScript 并返回结果 |

页面工具通过浏览器 CDP 工作，不依赖系统 AX 树。实例结果不包含代理密码或 Profile 路径；页面本身的文字、URL 和 JavaScript 结果仍可能包含用户数据。

### 连接配置

优先复制应用生成的配置，避免手写端口和令牌。支持 `mcpServers` 格式的客户端可以参考下面的结构；其他客户端按其格式填写 URL 和 `Authorization` 请求头。

```json
{
  "mcpServers": {
    "ytray": {
      "type": "http",
      "url": "http://127.0.0.1:51373/mcp",
      "headers": { "Authorization": "Bearer <浏览器页面令牌>" }
    },
    "ytray-system": {
      "type": "http",
      "url": "http://127.0.0.1:51373/system/mcp",
      "headers": { "Authorization": "Bearer <当前桌面会话令牌>" }
    }
  }
}
```

这是结构示例，两个占位令牌不能直接使用，也不能互换。桌面配置仅在会话运行时生成，重新开启会话后必须使用新令牌。浏览器页面令牌会保存在当前用户的数据目录中；桌面令牌只保存在内存中。

网关通过 HTTP POST 接收 JSON-RPC，支持 `initialize`、`ping`、`tools/list` 和 `tools/call`。实际参数以连接后的 `tools/list` 为准，MCP 窗口也可以查看每个工具的参数定义。

## 原生电脑控制

### 系统权限与一键重开

| 权限 | 用途 | 授权入口 |
| --- | --- | --- |
| 辅助功能 | 读取 AX 控件、调用控件动作、输入和按键 | 系统设置 → 隐私与安全性 → 辅助功能 |
| 屏幕录制 | 获取目标窗口或主显示器截图 | 系统设置 → 隐私与安全性 → 屏幕录制／屏幕与系统音频录制 |

首次开启时会弹出 YTray 的授权引导，可分别申请权限、打开系统设置、检查并开启。系统授权必须由用户在本机完成，MCP 没有申请系统权限或启动控制会话的工具。

授权窗口还显示当前应用路径、Bundle ID、签名 Team ID 和代码哈希，便于检查系统设置中的同名条目是否对应当前应用。应用依据 macOS 实际权限检查结果启用控制，不会仅根据开关外观认定授权成功。

“重启并应用权限”会先结束当前控制会话，再退出并通过 LaunchServices 重新打开同一个应用，返回电脑控制页面。**重开后控制保持关闭，需要再次手动开启。** 此操作重开的是 YTray，不会重启 macOS。

### 会话和接入范围

- 电脑控制默认关闭，不随登录或应用启动自动开启。
- 每次会话有独立随机令牌，默认 **30 分钟**后结束；停止、退出或会话到期会撤销令牌。
- 默认只接受不带 Origin 的本机 Agent。网页接入需要在本机设置精确来源，如 `https://example.com` 或 `http://localhost:3000`。
- 来源包含协议、域名和端口，不能带路径、查询、账号或通配符；非本机网页要求 HTTPS。预检和实际请求只向已授权来源开放。
- 会话内操作串行执行。停止会话会撤销排队操作和观察凭据；已经投递给操作系统的动作无法撤回。
- 控制的是当前登录用户的真实桌面，共享本机应用、屏幕、鼠标和键盘，不提供虚拟机或账号隔离。

### 电脑控制：13 个工具

| 函数 | 功能与主要参数 |
| --- | --- |
| `list_apps` | 列出运行中的应用与 PID；`query` 按应用名或 Bundle ID 筛选 |
| `list_windows` | 列出窗口 ID、所属 PID 和边界；可按 `pid`、`on_screen_only` 筛选 |
| `launch_app` | 按准确 Bundle ID（如 `com.apple.TextEdit`）打开已安装应用，返回窗口列表；需本机允许前台控制 |
| `activate_window` | 按 `window_id` 切换窗口并返回最新状态；需本机允许前台控制 |
| `check_permissions` | 读取实际权限和捕获能力，不弹授权窗口 |
| `get_window_state` | 用 `window_id` 观察确切窗口（`pid` 可省略），默认返回紧凑控件编号和可选截图 |
| `get_desktop_state` | 获取**主显示器**截图和截图尺寸；可设置 `max_image_dimension` |
| `click` | 按最新 `element_index`、`element_token` 或截图坐标点击；支持左右键和 1～2 次点击，实际行为受输入模式限制 |
| `type_text` | 向目标插入 `text`，支持中文和 emoji；单次最多 20,000 字符 |
| `press_key` | 对目标执行 `key`，前台模式支持原生实现所列按键和修饰键 |
| `scroll` | 指定 `direction`、`by: line/page`、`amount: 1..50` 和目标，执行滚动 |
| `system_status` | 查看会话状态、到期时间、权限、前台输入许可和是否正在操作 |
| `end_session` | 结束当前会话并撤销接入令牌 |

常用流程只需 `list_windows → get_window_state → click/type_text/press_key/scroll`，动作默认回传最新状态。用 `window_id` 指定窗口、`element_index` 选择控件；`snapshot_id` / `capture_id` 绑定观察与截图，避免使用旧目标。主屏幕输入可用 `window_id: 0`，需要明确的 `delivery_mode: "foreground"` 和本机许可。

`get_window_state` 支持 `query`、`region`、`detail: "full"` 和 `max_image_dimension`。普通观察默认带图，区域读取默认不带图；`include_screenshot: false` 可只读文字。MCP 工具列表只展示常用参数，旧的 `pid`、`target`、`element_token`、`tree_format` 等参数仍兼容；`tree_format` 支持 `markdown`、`elements`、`both`。

网关不提供终端命令、任意脚本、文件输出路径、权限修改或自动重启 YTray 的远程工具。`launch_app` 仅打开准确 Bundle ID 对应的已安装应用，不执行命令或任意 URL。浏览器 JavaScript 属于另一组浏览器页面工具，不在电脑控制端点开放。

### AI 看到的 AX 文本是什么

AX 是应用向 macOS 辅助功能接口公开的控件信息，不是 OCR 对截图的猜测。返回内容包括角色、名称、值、支持的动作、层级和元素 token。安全输入框的值会被遮蔽为 `[secure]`。

下面是显式指定 `tree_format:"both"` 时的完整结构示意，名称、数值和 token 不代表当前桌面的真实状态；默认紧凑格式见下一节：

```text
window_id=456 pid=123 size=640x568 elements=4 snapshot_id=sExample backend=swift-native
- [sExample:0] AXWindow "演示窗口" actions=[]
  - [sExample:1] AXButton "增加计数" actions=["AXPress"]
  - [sExample:2] AXTextField "消息" value="你好" actions=["AXConfirm"]
  - [sExample:3] AXScrollArea "列表" actions=[]
```

AI 可以选择“增加计数”的 token 调用 `click`，不必估计按钮像素中心。结构化 `elements` 同时提供 `role`、`label`、`value`、`actions`、`bounds` 等字段；实际字段取决于应用公开了什么。

AX 读取受元素数、层级和时间限制，结果中的 `truncated`、`degraded`、`ax_errors` 用于说明内容是否被截断或降级。读到部分树不代表窗口所有控件都已被发现。

### 紧凑观察与更少的往返

默认 `get_window_state` 只在文本中返回一份 `[N]` 控件列表，不再同时重复 markdown 与结构化元素数组。编号沿用完整观察的原始索引，过滤、区域读取和链接去重不会重新编号。默认最多展示 80 行、约 6,000 字符的控件文本；聚焦控件和可编辑控件优先，较长值带省略提示，`context_truncated` 与 AX 采集本身的 `truncated` 分开报告。

```json
{"name":"get_window_state","arguments":{"window_id":456,"include_screenshot":false}}
```

选择返回的控件编号即可操作；可以附上该次返回的 `snapshot_id`，明确拒绝旧观察：

```json
{"name":"click","arguments":{"window_id":456,"element_index":17,"snapshot_id":"实际返回的 snapshot_id"}}
```

YTray 根据 CG 窗口 ID 确认实际 PID，然后继续进行原有 AX 窗口归属、几何及令牌检查；不会回退到当前活动窗口。原有 `pid`、`target`、`element_token` 调用仍然支持。`element_index` 只表示最新观察的编号，不能跨观察复用；新状态到来后重新选择。

窗口动作默认自动返回一次最新紧凑状态，原始动作结果保存在 `structuredContent.action`，读取新状态不会把动作错误变成成功。Agent 应先使用动作已返回的状态，避免立即再发一次同样的观察请求。语义动作默认不附图；坐标动作默认返回新图，也可显式设置 `include_screenshot`。`return_state:false` 保留旧调用方式，跳过动作后观察。没有截图的新观察不能沿用上一次图片坐标，需要显式重新截图。桌面目标的动作需另行调用 `get_desktop_state` 观察结果。

只读取窗口的一块区域时，`region` 使用同一 `max_image_dimension` 下的窗口图片像素坐标，默认不截图，也不要求先截图：

```json
{"name":"get_window_state","arguments":{"window_id":456,"max_image_dimension":1400,"region":{"x":0,"y":0,"width":600,"height":200}}}
```

区域只返回有已知几何且与区域相交的元素；区域外焦点不返回选中文本。窗口内的区域筛选是一种观察方式，授权范围仍为该窗口。`query` 可以进一步按标签、值、标识符或角色筛选。`detail:"full"` 查看完整元素；显式 `tree_format:"elements"` 只返回结构化元素和简短摘要，`markdown`、`both` 保持兼容。`max_text_chars` 可在 256～30,000 之间设置控件文本预算。

同一 Web Area 内具有完全相同 HTTP(S) URL 的链接别名在默认视图中合并，优先保留聚焦项或描述性标题；不同查询参数、片段、状态和 Web Area 保持独立。原始 AX 节点和可操作索引留在后端，完整观察仍可查看。

`type_text` 支持 `replace:true` 和 `submit:true`，一次调用完成替换、输入、确认并返回最终观察：

```json
{"name":"type_text","arguments":{"window_id":456,"element_index":9,"text":"新的内容","replace":true,"submit":true}}
```

后台替换要求目标提供可写 AXValue；后台提交要求 AXConfirm，并在写入前检查，不能因为后台不支持就自动改前台。前台组合输入要求显式授权，并检查目标窗口中的文本焦点；优先对确切的 AX 文本控件写入，需要物理按键替换时还须读回非安全文本控件的全选范围，无法确认时拒绝发送替换文本。`press_key` 支持 `key:"cmd+l"`、`key:"cmd+shift+l"` 这类组合键简写；使用简写时不要同时传 `modifiers`。连续前台操作可显式指定 `restore_focus:false`，让目标保持活动，避免每次恢复再激活；默认仍恢复之前应用。该选项不授予前台权限，输入仍检查确切窗口焦点。输入中断可能已产生部分效果，仍应根据返回状态确认，不自动重放。

### 截图坐标是怎么换算的

窗口观察会返回 `image_width`、`image_height`、`coordinate_scale`、`capture_id` 和 `window_bounds`。模型在**返回的图片坐标系**里定位，YTray 按实际缩放比例换算为系统屏幕坐标：

```text
屏幕点 x = window_bounds.x + 图片 x / coordinate_scale
屏幕点 y = window_bounds.y + 图片 y / coordinate_scale
```

窗口图片以左上角为原点，x 向右、y 向下；桌面截图坐标相对于主显示器。调用者若另外缩放或裁剪图片，必须先把点映射回 YTray 原始返回图片的坐标系。

图片尺寸由捕获结果直接提供，不应让模型估计，也不能直接假定 Retina 比例为 2。返回截图时，元素的 `bounds` 会同步转换到该截图坐标系；未返回截图时按返回的比例解释。

窗口通过 PID、位置、尺寸和标题唯一匹配，匹配不唯一时拒绝操作。元素 token 属于当前会话、目标窗口和该窗口的最新观察，**60 秒过期**；窗口移动或关闭后需要重新观察。坐标动作要求最新成功截图，`click` 可以附带 `capture_id` 校验截图归属。

窗口观察可针对其他显示器上的窗口；整屏捕获工具当前只支持主显示器。光标使用系统屏幕坐标换算，不能把“支持窗口坐标换算”理解为已提供所有显示器的整屏捕获。

### 后台动作与显式前台输入

默认 `delivery_mode` 为 `background`。后台操作针对控件调用 AX API，不会因失败而自动切换到鼠标键盘事件。

| 动作 | 后台模式 | 显式前台模式 |
| --- | --- | --- |
| 点击 | 对目标调用 `AXPress` / `AXShowMenu`；坐标可先命中 AX 控件 | 通过 CGEvent 发送实际鼠标事件，支持任意有效截图位置 |
| 输入 | 写入 `AXSelectedText` 或可写文本值 | 优先直接写入确切 AX 文本控件；无法使用 AX 时才用 Unicode 键盘事件，可先定位目标 |
| 按键 | 只执行控件提供的 `AXConfirm`、`AXCancel`、`AXPress` 等动作 | 发送受支持的原生按键和组合键 |
| 滚动 | 使用 AX 滚动条或容器动作；步数由应用定义 | 发送原生滚轮事件 |

例如，在支持 `AXConfirm` 的输入框中，后台 `press_key` 的 Enter 会调用确认动作；这不代表向所有应用投递了物理回车。后台 `amount: 3` 表示三个应用定义的滚动步骤，不保证恰好移动三行或固定像素。

需要前台输入时，先停止会话，在本机勾选“允许切换应用与使用鼠标、键盘”，再重新创建会话。调用者还必须明确传入 `delivery_mode: "foreground"`。窗口前台输入前检查目标窗口焦点，不能通过参数绕过本机会话授权，也不会为失败的后台调用自动激活应用。

所有输入必须指定准确的窗口目标（`pid` 与 `window_id`，或等价 `target`），或明确的主桌面目标 `{"kind":"desktop","display_id":"primary"}`。桌面输入会影响当前实际焦点，不能提供后台隔离。

### 一次可靠操作的顺序

1. 用 `list_apps` / `list_windows` 选择目标。
2. 用 `get_window_state` 读取最新 AX 树和需要的截图。
3. 优先使用最新控件编号或元素 token；AX 无法表达目标时再考虑截图定位。
4. 执行一次动作，核对自动返回的新状态；若设置 return_state:false，则显式重新观察。
5. 遇到旧 token、页面切换或滚动后重新定位；结果未知的发送、提交等动作先确认是否已完成，不能盲目重放。

下面是 `tools/call` 的参数结构示意。运行时将 PID、窗口 ID 和 token 替换为刚刚观察到的真实值：

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "method": "tools/call",
  "params": {
    "name": "click",
    "arguments": {
      "pid": 123,
      "window_id": 456,
      "element_token": "sExample:1",
      "delivery_mode": "background"
    }
  }
}
```

工具成功返回表示动作被投递或属性被写入，应用是否完成用户任务仍需要读回确认。窗口标题、AX 内容、网页文字和截图都属于被观察的数据，客户端应将其中的指令与用户请求区分开。

### 看得见的电脑控制

macOS 与 Windows 使用同一套陶土红 `#C96B50` 视觉反馈：

- **全局氛围灯**：操作期间，所有已连接显示器的四周亮起柔和渐变光；执行时缓慢呼吸，结束后淡出。
- **飞镖光标**：与 Windows 相同的纸飞机/飞镖轮廓、陶土红填充、浅色描边与折线，保留较大范围的虚化发光和动作波纹。尖端对齐实际操作位置；AX 控件动作取窗口内的可见中心。
- **操作提示**：目标屏幕顶部显示“即将执行 → 执行中 → 已执行／未完成／待确认”。能从已有观察获得名称时显示按钮和窗口名称；输入只显示字符数，不在提示或操作记录中保存输入原文。组合输入与自动回读只产生一条提示。
- **简洁控制器**：电脑控制页保留开启/停止、复制配置、MCP 工具和当前操作；最近 12 条操作保存在内存中，重新开启会话清空。权限、来源设置、AX/截图诊断默认折叠。

提示层不替换系统鼠标、不抢焦点、不拦截鼠标事件。四边使用窄窗口，避免为每个屏幕创建整屏位图；桌面截图排除氛围灯、提示卡和光标。停止会话立即撤销输入并关闭提示层，已发送的系统动作不能撤回；旧操作的迟到结果不会重新点亮提示。系统开启“减少动态效果”时停止呼吸与光标移动动画。

动作结束后，飞镖光标驻留 5 秒，成功提示和氛围灯驻留 8 秒；未完成或待确认的提示保留 12 秒，随后用 0.6 秒平滑淡出。下一步操作立即接替当前提示，不因视觉驻留增加 API 延迟；停止控制时立即清除全部提示。

动作失败和回读失败分开报告：结果未知会显示“待确认”，不会自动重试；动作已投递但未取得最新画面，会保留投递结果并说明需要重新观察。“已执行”不代表应用内的业务任务已经成功。

### 常见问题与恢复

| 现象或返回值 | 含义与处理 |
| --- | --- |
| 系统设置开关已亮，YTray 仍提示缺权 | 检查是否为当前路径和签名的应用；先使用“重启并应用权限”，仍无效再移除旧条目并重新添加当前 `.app` |
| `stale_observation` / `invalid_element_token` / `stale_capture` | 观察过期、窗口改变或截图不匹配；重新观察并重新选择目标 |
| `window_ambiguous` / `window_unresolved` | 无法唯一关联窗口；重新列出窗口并选择可明确识别的目标 |
| `capture_required` | 没有目标窗口的最新成功截图；先获取带截图的状态 |
| `background_unavailable` / `hit_test_failed` | 应用没有提供相应 AX 能力；刷新或检查 AX 状态，需要真实输入时由用户明确授权前台模式 |
| `foreground_unavailable` | 目标窗口不满足前台操作条件；确认目标与焦点，再重新观察 |
| 401 | 令牌错误、会话未开启或已结束；重新开启并复制对应端点配置 |
| 403 | Host 或网页 Origin 不符合接入范围；检查地址与精确来源 |
| 操作超时、结果未知 | 先观察应用是否已经响应，再决定是否继续；不要直接重复发送或提交 |

自绘界面、画布、游戏、部分 Electron 应用或系统保护界面可能只暴露有限 AX 信息。Swift 原生实现可以减少额外运行时与权限归属问题，无法让没有 AX 支持的应用自动拥有完整控件树。截图定位也依赖模型能力，不能保证所有界面零误差。

## 开机启动、更新和数据

安装版首次启动会尝试开启 macOS Login Items，并显示结果；失败时可以重试，关闭需要确认。登录时仅启动 YTray 进入菜单栏，不会自动启动浏览器或电脑控制。从源码运行和开发包不修改登录项。

正式安装版支持应用内检查、下载和安装更新，使用 Sparkle 与签名更新元数据。开发包禁用正式版本自动更新；发布签名、公证和构建流程见[仓库首页](../README.md)。

正式版数据位于 `~/Library/Application Support/YTray/`；开发包使用独立的 `YTrayDev/`：

```text
Profiles/       独立浏览器用户目录
Plugins/        本地与内置插件
Runtimes/       托管 Chrome for Testing
Screenshots/    用户导出的浏览器页面截图
Logs/           运行、错误与实例日志
MCP/token       浏览器页面 MCP 令牌，文件权限 0600
control.sock    第一方本机控制 Socket
state.json      配置与实例历史
```

电脑控制令牌和 AX 观察凭据仅保存在内存中，结束后不能恢复旧连接。授权网页来源和网关设置会保存在本机偏好中。不要将复制出来的真实 MCP 配置提交到仓库或公开发布。

## 从源码构建与验证

需要 macOS 14+、Xcode Command Line Tools。打包还需要 ImageMagick（提供 `magick`）；Swift Package Manager 会解析项目依赖，打包脚本会准备内置浏览器扩展。

在仓库根目录执行：

```bash
# 普通 UI 开发，不用于验证桌面权限归属
./script/startup.sh

# 单元与按条件启用的集成测试
swift test --package-path darwin

# 构建独立开发应用，Intel 改为 --arch amd64
./script/package-macos.sh --dev --arch arm64
open dist/darwin-arm64-dev/YTrayDev.app

# 发布形态的本机架构 DMG
./script/package-macos.sh --dmg
```

### 稳定开发签名

桌面权限验证应使用完整 `.app`。普通 `swift run` 进程不具有产品应用的权限身份，不能开启电脑控制。

开发打包从已有有效代码签名身份中选择并固定一个身份。只有一个可用身份时自动选择；存在多个时可显式设置：

```bash
YTRAY_DEV_SIGN_IDENTITY='<已有证书指纹或名称>' \
  ./script/package-macos.sh --dev --arch arm64
```

选择保存在 `~/Library/Application Support/YTrayBuild/dev-signing-identity`，不会生成或导出私钥。固定证书不可用时，脚本会在修改已有应用前失败，不会自动换证书或降级。临时构建可以显式使用 `YTRAY_DEV_SIGN_IDENTITY=- YTRAY_DEV_ALLOW_ADHOC=1`，但重建后可能丢失 TCC 授权。

从旧临时签名迁移到稳定签名时，需要在辅助功能和屏幕录制列表中移除旧条目，重新添加当前 `.app` 一次。后续尽量保持 Bundle ID、应用路径和签名身份不变。

### 自动验证与真实桌面验证

```bash
# 不需要系统授权，使用模拟证书列表验证签名选择规则
./script/test-macos-dev-signing.sh

# 带原始请求、结果、AX 文本、截图和测试窗口读回的真实验证
dist/darwin-arm64-dev/YTrayDev.app/Contents/MacOS/YTrayDev \
  --smoke-system-control --system-control-output /tmp/ytray-system-smoke
```

真实桌面验证检查应用权限归属、AX、截图、点击、中文输入、回车、滚动、无效 token 拒绝和结束会话。它不自动申请系统权限；缺权时记录 `blocked_by_permissions`，不能将空树或零张截图算作通过。

可先编译 [AXInteractionFixture.swift](Tests/Fixtures/AXInteractionFixture.swift)，再通过 `--system-control-external-fixture /absolute/AXInteractionFixture` 使用独立测试应用。附加 `--system-control-test-foreground` 会明确为该次测试授权短暂激活测试窗口，并验证前台点击、Unicode 输入和按键。

UI 预览命令包括 `--render-manager`、`--render-widget`、`--render-mcp-window`和 `--render-computer-permissions`。CI 会构建、测试、渲染原生界面并验证打包；CI 无系统授权时的测试结果不能替代已授权机器上的真实桌面验证。

控制核心位于 [SwiftDesktopBackend.swift](Sources/YTray/SwiftDesktopBackend.swift) 和 [NativeAXWorker.swift](Sources/YTray/NativeAXWorker.swift)，直接调用 Apple Accessibility、ScreenCaptureKit 和 CGEvent，运行在 YTray 进程内，无需第三方桌面 Driver。外部应用 AX 读取在专用串行队列执行，自身 AppKit AX 对象在主线程处理。
