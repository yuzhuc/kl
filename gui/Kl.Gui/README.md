# kl

Avalonia 桌面客户端，采用 kl 品牌和 Codex 风格界面。它是 **KL Core 的协议客户端**：
会话、Turn、流式事件、工具执行、审批、取消、历史、沙箱与审计全部由 Rust
`kl-core` 完成；GUI 只通过 `sdk/Kl.Client` 的 `KlSessionClient` 启动/恢复会话并渲染事件，
不实现 Agent/Policy/Sandbox/Tool 逻辑，也不直接调用模型 API。

## 架构与代码边界

- `Services/CoreSessionService.cs`：唯一管理 Core 会话生命周期的地方（打开/恢复/关闭、
  run turn、cancel、history、权限模式、快照探测、自动化临时会话）。传输选择（无本地
  server 时启动 stdio 子进程；发现 `server.json` 时连接本地 server）由 `Kl.Client` 决定。
- `Services/CoreSessionPresenter.cs`：把 `ISessionPresenter` 回调桥接到 GUI 的事件通道；
  审批请求会阻塞 Core 事件循环直到 UI 返回 `allow` / `allow_session` / `deny`。
- `Services/CoreProtocolModels.cs`：解析 `session.list` / `session.history` /
  `provider.list` 等协议响应，并提供标题、时间等展示派生。
- `ViewModels/MainWindowViewModel.Core.cs`：Core 事件到 UI 状态的映射（会话、消息、
  工具卡片、审批、只读观察者、Usage、连接横幅与重试）。
- `MainWindow.axaml(.cs)`：只负责视图、对话框与 UI 线程调度；不包含任何 JSON-RPC。

## 功能

- Core 会话：新建、恢复、列表、搜索、重命名、删除；历史来自 `session.history`
  （含 reasoning、工具 summary/output/diff）。
- 流式聊天：`turn.started` / `turn.delta` / `turn.reasoning` / `turn.completed`，
  按 per-session `seq` 去重并可跟踪 `LastSeq`。
- 工具卡片：参数、运行状态、有界输出、summary 与 diff（Phase 1 同一时刻展示一张活动卡片）。
- 审批：动作、资源、原因、参数与三决定（允许一次 / 本会话允许 / 拒绝；Esc 拒绝）。
- 取消当前 Turn；按 use 统计 token（Core 报告 cost 时显示费用）。
- 连接状态横幅与重试；另一客户端持有写租约时以只读观察者身份展示。
- 设置 → 模型：只读显示 Core 运行时信息（连接状态、版本/协议、Provider/模型、
  API Key 是否配置、Core 路径、KL Home）；Provider/模型/API Key 由 Core 配置管理。
- 设置 → 通用：权限模式（映射到 Core `permissions.set`）与内容字号。
- 自动化：每个任务通过 `RunEphemeralTurnAsync` 在临时 Core 会话中执行，仅保存结果。
- 插件页：继续扫描共享的 `~/.kl/plugins` 本地目录（Core `plugin.*` 管理属后续阶段）。
- 文本与代码附件会折叠进发给 Core 的 prompt；会话消息以 Core 为准。
- 快捷键：Ctrl+N、Ctrl+Enter、Ctrl+F、Ctrl+,

## 界面（对齐 Codex 桌面端）

- 无边框窗口 + 40px 自绘标题栏：←/→、侧栏开关、文件/编辑/视图/帮助菜单、最小化/最大化/关闭。
- 52px 图标 rail：首页、自动化、插件（自定义）、更多、代码审查；底部只有一个设置按钮。
- 280px 侧栏按页面切换：聊天（项目列表 + 最近会话 + 搜索）、自定义（插件）、自动化、项目。
- 对话视图：用户右侧气泡（时间/复制/编辑）、assistant 纯 Markdown、思考与工具为可展开活动行、错误红卡；composer 常驻底部（项目 pill、权限、Provider/模型、发送/停止）。
- 左栏交互对齐 Codex：rail/侧栏的悬浮、选中、按下态与配色按 Codex 实测值（`#0D0D0F` rail、`#0F0F10` 侧栏、`#1D1D1F` 悬浮/选中行、`#1C1C1E` rail 悬浮/选中块、图标 `#727274`→`#E3E4E6`），120ms 颜色过渡、200ms 侧栏收起动画、最近分组 chevron 旋转、rail 悬浮提示延迟 450ms。
- 更多菜单的「置顶」会把项目/代码审查快捷键添加到 rail 分隔线下方（再次点击取消，状态持久化到 GUI 本地状态），点击快捷键跳到对应页面。
- 设置：左导航 + 分组卡片；审批与对话框统一深色圆角卡片。视觉 token（颜色/字体/图标）集中在 `App.axaml`。

## 配置与数据

KL Core 配置（由 Core 读取，GUI 只读展示）：

- `%USERPROFILE%\.kl\config.toml`：Provider、模型、Agent、Policy、Sandbox 等。
- `%USERPROFILE%\.kl\providers\*.toml`：Provider 目录。
- `%USERPROFILE%\.kl\credentials\<provider>.key`：Provider API Key（GUI 不展示明文）。

GUI 自身状态（UI 缓存，不是会话真值）：

- 状态文件：`%USERPROFILE%\.kl\gui\state.json`（工作区、布局、自动化、权限模式）。
- 会话/消息/事件：`%USERPROFILE%\.kl\sessions\`（由 Core 管理）。
- 插件目录：`%USERPROFILE%\.kl\plugins`。
- 旧版设置：`%USERPROFILE%\.kl\gui\gui-settings.json`。

旧版 `%LOCALAPPDATA%\DeepSeekHarnessGui` 和 `%LOCALAPPDATA%\Kl\gui-settings.json`
会在首次启动时自动迁移，随后删除旧目录。旧 state 中的 Endpoint/Model/ApiKey
字段只用于迁移，不再驱动请求。

环境变量覆盖：

- `KL_HOME` / `KL_CONFIG`：Core home 与配置文件。
- `KL_CORE_PATH`：显式指定 `kl-core` 可执行文件。
- `KL_GUI_STATE` / `KL_GUI_WORKSPACE` / `KL_GUI_PLUGINS`：GUI 状态/工作区/插件目录。
- `KL_GUI_E2E_OPEN_LAST` / `KL_GUI_E2E_PROMPT` / `KL_GUI_E2E_APPROVAL` /
  `KL_GUI_E2E_APPROVAL_DELAY_MS`：scripted Core 端到端冒烟钩子。

## 构建、测试、发布

```powershell
dotnet run --project .\Kl.Gui.csproj
dotnet test ..\Kl.Gui.Tests\Kl.Gui.Tests.csproj
```

在仓库根目录运行 `python scripts/build.py` 后，GUI 发布到 `D:\kl\bin\gui`，
`kl-core.exe` 会被同时复制到 `bin\gui` 与 `bin\tui`，启动脚本为
`D:\kl\bin\run-gui.bat`。若从组件构建直接运行，可设置 `KL_CORE_PATH` 指向
`target\release\kl-core.exe` 等位置。

## 已知限制（Phase 2）

- 自动断线重连与观察者升级 UX 尚未接入（SDK 已支持 `LastSeq` 重连；当前为错误横幅 + 重试 + 重新打开会话）。
- 并发工具卡片：SDK 的 `SessionEvent` 已提供 `Seq`/`CallId`，GUI Phase 1 仍按单活动卡片渲染。
- GUI 发布尚未进入 release 归档（本地 build 已可用）。
- Core `plugin.*` 管理、MCP/技能页面尚未接入 GUI。
