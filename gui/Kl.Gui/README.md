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
- 工具卡片：参数、运行状态、有界输出、summary 与 diff；按 `CallId` 路由，可同时展示多张工具卡片。
- 审批：动作、资源、原因、参数与三决定（允许一次 / 本会话允许 / 拒绝；Esc 拒绝）。
- 取消当前 Turn；按 use 统计 token（Core 报告 cost 时显示费用）。
- 连接状态横幅与重试；另一客户端持有写租约时以只读观察者身份展示。
- 设置 → 模型：显示 Core 运行时信息（连接状态、版本/协议、Provider/模型、
  API Key 是否配置、Core 路径、KL Home）；Provider/模型/API Key 的修改通过 Core RPC
  完成，Key 始终掩码，GUI 不读取 `credentials/*.key`。
- 设置：22 个分页（常规/通知/外观/配置/模型/提供商/记忆/插件/MCP/存储/导入/代码审查/Git/Worktrees 等）；权限模式映射到 Core `permissions.set`，Provider/模型/Key 全部经 Core RPC。
- 自动化：每个任务通过 `RunEphemeralTurnAsync` 在临时 Core 会话中执行，仅保存结果。
- 插件页：插件、MCP 工具与技能通过 Core RPC/工作区读取；共享的 `~/.kl/plugins` 本地目录仍可使用。
- 文本与代码附件会折叠进发给 Core 的 prompt；会话消息以 Core 为准。
- 快捷键：9 个动作可在设置 → 键盘快捷键中改键（新建/归档/复制代码块/未读/置顶/搜索/设置/终端/关窗），其余为固定快捷键。

## 界面（对齐 Codex 桌面端）

- 无边框窗口 + 44px 自绘标题栏：←/→、侧栏开关、文件/编辑/视图/帮助菜单、最小化/最大化/关闭。
- 52px 图标 rail：首页、项目、自动化、插件、更多；代码审查是可置顶的 rail 项；底部只有一个设置按钮。
- 238px 侧栏（可拖到 468px）按页面切换：聊天（项目列表 + 最近会话 + 搜索）、自定义（插件）、自动化、项目。
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

- 状态文件：`%USERPROFILE%\.kl\gui\state.json`（工作区、布局、主题/字体、终端、Worktree、快捷键、自动化、权限模式、Provider 展示缓存；不含任何 Key）。
- 会话/消息/事件：`%USERPROFILE%\.kl\sessions\`（由 Core 管理）。
- 插件目录：`%USERPROFILE%\.kl\plugins`。
- 旧版设置：`%USERPROFILE%\.kl\gui\gui-settings.json`。

旧版 `%LOCALAPPDATA%\DeepSeekHarnessGui` 和 `%LOCALAPPDATA%\Kl\gui-settings.json`
会在首次启动时自动迁移，随后删除旧目录。旧 state 中的 `ApiKey` 字段会被忽略，
并在首次保存 state 时清除；GUI 不读取或写入 `credentials/*.key`。

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

- 打包后的 GUI 尚未在真实 Windows/macOS release runner 上做启动冒烟；本地 `build.py` 已包含 GUI、`kl-core` 与 `kl-sandbox-runner`。
- 多个 GUI 窗口共享同一份 `gui/state.json`，没有跨进程合并。
- MCP OAuth 仍留给后续阶段。
