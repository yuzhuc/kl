# KL 方案 V14

> 更新日期：2026-11-25
>
> `dev` / `main` 基线：`960d760`
>
> 说明：本文件是产品总方案；具体界面与交互见 `plan/UI方案.md`，GUI 技术契约见
> `docs/gui/mvp-spec.md`，热更新见 `plan/up.md`。

---

## 1. 产品定位

KL 是一个本地优先的 AI 编程代理，由 Rust 可信内核与 C# 客户端组成：

- `kl-core`：会话、Agent Loop、Provider、Tool、Policy、Sandbox、Audit、MCP、插件和持久化。
- `Kl.Client`：Rust Core 的共享 C# 协议客户端。
- TUI：Terminal.Gui 终端客户端。
- GUI：Avalonia 桌面客户端，视觉和交互以 Codex Desktop 为参考。

产品目标不是做一层“套壳聊天框”，而是让 GUI/TUI 都成为同一 Core 协议之上的正式客户端。

---

## 2. 架构与不可破坏边界

```text
TUI ─┐
GUI ─┼── Kl.Client ── JSON-RPC ── kl-core ── Provider / Tools / Policy / Sandbox
SDK ─┘
```

必须保持：

- Core 不依赖 UI。
- GUI/TUI 不实现 Agent Loop、工具执行、Policy、Sandbox、Audit 或 Provider 调用。
- GUI/TUI 只通过 `Kl.Client` 发起会话、事件、审批、历史和自动化请求。
- 客户端不直接读取 Core 管理的 session/event/config 私有文件。
- 产品运行时不依赖 Python；Python 只用于构建、检查、打包和 benchmark。
- 用户数据统一位于 `%USERPROFILE%\.kl`，不得重新引入 AppData 应用数据目录。
- 本地构建使用 `python scripts/build.py`；缓存留在仓库 `target/` 与项目 `bin/obj`，
  最终产物发布到仓库内 `bin/`（默认 self-contained，MSVC CRT 静态链接）。
- 业务代码不依赖 `VCRUNTIME140.dll` 之类的额外系统组件。

---

## 3. 当前状态

| 模块 | 状态 | 说明 |
|---|---|---|
| Rust Core | 可用 | 13 个 crate；会话、Agent、工具、策略、沙箱、审计和本地多客户端传输已实现 |
| `Kl.Client` | 可用 | stdio、本地 server、attach/replay、LastSeq 重连、writer lease、observer |
| TUI | 可用 | Terminal.Gui 2 客户端；命令、审批、历史和 server 模式已接入 |
| GUI | 可用，持续美化 | Avalonia 12.1.4；Core 协议客户端；Codex 风格三栏、会话树、聊天活动行、标尺、设置与项目页已实现 |
| GUI 测试 | 通过 | 260 GUI + 7 Avalonia headless passed；TUI 55 passed |
| 本地构建 | 可用 | 默认 self-contained（含 .NET 10 运行时），静态 MSVC CRT；GUI/TUI 发布到 `bin/` |
| 正式发布 | 部分完成 | release 归档已包含 GUI（win-x64/win-arm64/osx-arm64）和 `kl-sandbox-runner.exe`；release 尚未接测试门禁；Linux 包暂不含 GUI；真实 Provider/MCP live smoke 仍待凭据 |

最近一批 GUI 工作集中在：

- Codex 风格左侧 rail、会话侧栏和可拖拽宽度。
- 项目、置顶、分组、搜索、拖拽排序、悬浮预览卡和 section menu。
- 聊天页颜色、字体、气泡、composer 和 Markdown 代码块对齐。
- 工具调用活动行、Shell 展开面板、命令 chip 和状态 badge。
- 对话标尺、hover 预览、当前轮高亮和滚动联动。
- 设置页、项目页、插件页和自动化页的统一页面结构。
- composer 的模型 chip 与四档推理强度滑杆，按轮写入 Core。

---

## 4. 已实现能力

### 4.1 Core / 协议

- Provider-neutral Agent Loop。
- 文件、搜索、Shell、Git、worktree、job、subagent、memory、todo、ask_user、web_fetch 等工具。
- 统一 Tool Pipeline：校验、Hook、Policy、审批、Sandbox、执行、结果边界和 Audit。
- 会话持久化、resume、fork、compaction、memory 和审批规则。
- 本地 server：attach/replay、seq 去重、断线重连、单 writer、多 observer。
- Windows AppContainer、Linux bubblewrap、macOS Seatbelt 沙箱边界。
- MCP server 通过 SandboxManager 启动。
- `skill.list` / `skill.get` 技能发现与正文读取。

### 4.2 GUI

- Core 会话：新建、恢复、列表、搜索、重命名、删除。
- 流式聊天：assistant/reasoning/tool/error 事件和 usage/cost。
- 审批三决定：`allow`、`allow_session`、`deny`。
- Codex 风格活动行：单工具内联，多工具按 `CallId` 分组，Chevron 动画，Shell 展开面板。
- Markdown：代码块、inline code、复制/换行操作和亮暗层级。
- 对话标尺：每轮标记、滚动当前轮高亮、hover 预览、跳转与固定标记。
- 侧栏：项目树、最近会话、置顶区、拖拽排序、预览卡和右键 section menu。
- composer：`/` 命令菜单、`@` 文件菜单、三档权限胶囊和推理强度滑杆。
- 页面：聊天、项目、插件、自动化、设置。
- 数据目录：`~/.kl/gui/state.json`、`~/.kl/plugins`、Core 管理的 `~/.kl/sessions`。

### 4.3 构建与数据

```text
缓存：D:\kl\kl\target、项目 bin/obj
输出：D:\kl\kl\bin\gui、D:\kl\kl\bin\tui
状态：%USERPROFILE%\.kl\gui\state.json
插件：%USERPROFILE%\.kl\plugins
会话：%USERPROFILE%\.kl\sessions（Core 管理）
密钥：%USERPROFILE%\.kl\credentials（GUI 不展示明文）
```

---

## 5. 当前优先级

### P0：发布与可验证性

1. release 流程接入主测试门禁：`release.yml` 目前直接打包，没有任何
   `cargo test` / `dotnet test` 步骤。
2. 决定 Linux 包是否纳入 GUI（目前只有 Windows 与 macOS 归档带 `--gui`）。
3. 有真实凭据时完成 Provider/MCP live smoke；无凭据时保持 pending，不伪造通过。

已完成：GUI 纳入 release archive、release bundle 带 `kl-sandbox-runner.exe`。

### P1：GUI 质量收口

1. transcript 虚拟化和大历史性能（标尺需先适配虚拟化容器）。
2. 标尺、活动行、拖拽和设置页的 UI 回归测试。
3. UI 方案第 8 节列出的交互缺口（技能启停、图片拖入、设置跨页搜索等）。

已完成：并发工具卡片按 `CallId` 配对、断线自动重连与 LastSeq 恢复、
Provider/模型/凭证编辑经 Core 协议、对话中切换模型与推理强度。

### P2：后续能力

- GUI/SDK/插件仓库拆分。
- Prompt caching、Git snapshot/rewind/revert。
- MCP resources/prompts/OAuth。
- 跨平台桌面发布与自动更新（方案见 `plan/up.md`）。

---

## 6. 验收

```powershell
cargo fmt --all -- --check
cargo clippy --workspace --all-targets -- -D warnings
cargo test --workspace
dotnet build kl.sln
dotnet test kl.sln --no-build
python scripts/check_protocol.py
python scripts/check_docs.py
python scripts/check_i18n.py
python scripts/build.py
```

GUI 单独验收：

```powershell
dotnet test D:\kl\kl\gui\Kl.Gui.Tests\Kl.Gui.Tests.csproj -c Release --nologo
D:\kl\kl\bin\gui\kl.exe
```

---

## 7. 文档入口

| 文档 | 用途 |
|---|---|
| `plan/KL方案.md` | 产品总方案、阶段、风险和验收 |
| `plan/UI方案.md` | GUI 视觉、布局、交互和 UI 验收 |
| `plan/up.md` | 热更新（增量下载、core 热重启、插件热重载）计划 |
| `README.md` | 仓库总览与开发入口 |
| `gui/Kl.Gui/README.md` | GUI 代码边界和功能说明 |
| `docs/gui/mvp-spec.md` | GUI 技术规格、事件契约和阶段状态 |
| `docs/architecture/overview.md` | Core/Client/TUI/GUI 系统架构 |
