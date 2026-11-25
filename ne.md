# KL 项目介绍

> 给新对话 / 新参与者快速建立项目全貌。最后更新：2026-11-25。
> 本文是交接说明，不是产品文档；细节以 `README.md` 和 `docs/` 为准。

## 一句话

KL 是一个**本地优先的 AI 编码代理**：Rust 核心（`kl-core`）负责会话、Agent 循环、
策略/批准、沙箱、工具执行、provider 调用、审计和持久化；C# 的 Avalonia GUI、
Terminal.Gui TUI 和可复用 SDK 都只是 JSON-RPC 客户端，不自己执行工具或调用模型。

## 设计原则

- 本地执行、显式批准、上下文有界、工具调用可审计、provider 中立。
- 产品二进制和运行时**不依赖 Python**；Python 只用于构建、校验、打包、基准和开发演示/冒烟脚本。
- Core 是唯一执行工具、调用 provider、写会话、评估策略的进程。
- API key 由 Core 管理；GUI 只显示“是否已配置”，从不读取 `credentials/*.key`。
- 未知模型价格不臆造；高风险动作需要批准，批准可只对当前会话生效。

## 进程架构

```text
TUI (tui/Kl.Tui)     Headless/CI          GUI (gui/Kl.Gui)
        \                  |                  /
         \                 |                 /
          +----------- Kl.Client -----------+
                             |
            JSON-RPC 2.0 / stdio 或本地 socket
                             |
                      Rust Core (kl-core)
              protocol / sessions / events / audit
              agent / tools / policy / sandbox
              providers / skills / hooks / MCP / plugins
                             |
             Provider APIs (OpenAI 兼容、Anthropic ...)
```

- 协议：一行一个 JSON-RPC 2.0 消息；`kl.handshake` 协商版本，事件带每会话递增 `seq`。
- 传输：stdio 子进程，或 `kl serve` 的 Windows named pipe / Unix UDS；
  远程 Core 支持 TCP / TLS（`--token`、证书指纹固定）。
- 会话：`session.attach` 回放 + 实时订阅；`session.lease` 保证一个写者，
  其他客户端是只读观察者；支持 resume、fork、compact、rewind。
- 客户端按 `session_id + seq` 去重，重连从 `LastSeq` 继续。

## 仓库地图

| 路径 | 作用 |
|---|---|
| `crates/kl-protocol` | 协议 DTO、方法、错误、事件 schema |
| `crates/kl-core` | 核心服务器、会话、Agent 编排、扩展接线 |
| `crates/kl-agent` | provider 中立的回合循环、工具分发、压缩、重试 |
| `crates/kl-provider` | OpenAI Chat / Responses、Anthropic 适配器与模型元数据 |
| `crates/kl-tools` | 内置工具（文件、搜索、shell、git、worktree、job、subagent、memory、todo、ask_user、web_fetch） |
| `crates/kl-policy` | 动作/资源策略、阶段权限 |
| `crates/kl-sandbox` | Windows AppContainer、Linux bubblewrap、macOS Seatbelt |
| `crates/kl-store` | 会话元数据、状态投影、事件日志 |
| `crates/kl-audit` | JSONL 审计 + SQLite 查询索引 |
| `crates/kl-review` | diff/安全审查、可选模型审查 |
| `crates/kl-index` | 浅层仓库索引、测试选择提示 |
| `crates/kl-plugin-host` | sidecar/WASM 插件宿主、完整性校验 |
| `crates/kl-exec` | 进程启动、超时、取消、有界输出 |
| `sdk/Kl.Client` | C# JSON-RPC / 会话客户端（TUI 和 GUI 共用） |
| `tui/Kl.Tui` | Terminal.Gui v2 终端客户端 |
| `gui/Kl.Gui` | Avalonia 12.1.4 桌面客户端 |
| `gui/Kl.Gui.Tests` | GUI ViewModel / 服务测试 |
| `gui/Kl.Gui.HeadlessTests` | Avalonia headless 布局测试 |
| `schemas/` | 协议与插件 JSON schema |
| `tests/fixtures/` | Rust/.NET 共享协议夹具、provider 夹具 |
| `benchmarks/` | 验收任务与工作区 |
| `scripts/` | 构建、打包、协议/i18n 校验、基准、演示/冒烟 |
| `docs/` | 架构、安全、插件、GUI、ADR 文档 |

## 主要能力

- Agent 预设：`build` / `plan` / `explore` / `review` / `verify` / `deep`。
- 统一工具流水线：校验 → hooks → 策略 → 批准 → 沙箱 → 执行 → 结果裁剪 → 审计 → 事件。
- 会话持久化、resume、fork、审批规则、历史回放。
- 自动上下文压缩（模型摘要 + 机械回退）、`/compact`、23 个验收任务。
- 工作区/用户记忆注入、`/memory`、项目与用户 skills。
- MCP stdio 服务器、本地 sidecar/WASM 插件、插件完整性校验。
- 自动 provider 重试（指数退避、可取消）、按回合/累计 token 用量。
- 本地多客户端、远程 Core（TCP/TLS）、只读观察者。
- GUI：会话列表与富历史、流式回复/推理、工具卡片与 diff、批准、取消、
  审计、skills、插件重载、设置页；远程 Core 下禁用本地 Git/worktree/import。

## 构建、测试、运行

前置：Rust stable（见 `rust-toolchain.toml`）、.NET 10 SDK、Python 3.11+（仅工具）、Git。

```powershell
# 完整本地构建：cargo fmt/clippy/test + dotnet test + 发布
python scripts/build.py

# 跳过校验只发布（开发中用）
python scripts/build.py --skip-checks

# 组件构建
cargo build --release -p kl-core
dotnet build gui/Kl.Gui/Kl.Gui.csproj

# 测试
cargo test --workspace
dotnet test kl.sln
python scripts/check_i18n.py
python scripts/check_protocol.py
```

- 输出：`D:\kl\bin\gui`、`D:\kl\bin\tui`，`kl-core.exe` 会放在两个客户端旁边。
- 本地构建是 framework-dependent，目标机需要 .NET 10 Runtime（Windows 为 Desktop Runtime）。
- GUI 启动：`D:\kl\bin\run-gui.bat`（或直接 `D:\kl\bin\gui\kl.exe`）。
- 组件运行时可用 `KL_CORE_PATH` 指向自编译的 `kl-core`。

## 配置与数据

用户目录 `~/.kl/`：

- `config.toml` — providers、models、agent、policy、sandbox、review。
- `credentials/<provider>.key` — provider key，由 Core 管理，GUI 不读。
- `agents/*.toml` — 自定义 Agent 预设。
- `skills/` — 用户 skills。
- `mcp.toml` — MCP stdio 服务器。
- `gui/state.json` — GUI UI 状态（工作区、布局、automations、按模型的推理强度选择等）。
- `sessions/` — Core 拥有的会话、消息、事件。
- `plugins/` — GUI/Core 共享插件目录。

工作区级：`AGENTS.md`、`.kl/memory/`、项目 skills 会被发现。
权限模式：`ask`、`plan`、`acceptEdits`、`dontAsk`、`auto`、`read-only`。
远程：`KL_CORE_ENDPOINT` / `KL_CORE_TOKEN` / `KL_CORE_TLS_FINGERPRINT`。
GUI 状态路径可被 `KL_GUI_STATE` 覆盖；`KL_HOME` 可覆盖整个 home。

## GUI 开发要点

- 栈：Avalonia 12.1.4 / .NET 10，编译 XAML。
- 外壳：`gui/Kl.Gui/MainWindow.axaml` + `MainWindow.*.cs` 分部类
  （Chrome、Layout、Sessions、Settings、Appearance 等）。
- 设置页拆在 `gui/Kl.Gui/Views/*.axaml`；ViewModel 按领域拆分在
  `gui/Kl.Gui/ViewModels/MainWindowViewModel.*.cs`。
- Core 接入：`Services/CoreSessionService.cs`、`CoreSessionPresenter.cs`、
  `CoreProtocolModels.cs`、`ViewModels/MainWindowViewModel.Core.cs`。
- 主题/样式：`gui/Kl.Gui/Styles/`（`ThemeDark.axaml`、`ThemeLight.axaml`、`Controls.axaml`）。
- i18n：
  1. XAML 里写中文 `Text="..."`；
  2. `python scripts/i18n_extract.py` 生成 `S.<sha8>` 并改写 XAML；
  3. 在 `gui/Kl.Gui/Resources/i18n/catalog.json` 补 `en`；
  4. `python scripts/check_i18n.py` 必须通过。
  当前目录 1033 条。
- 测试：`dotnet test gui/Kl.Gui.Tests`（ViewModel/服务）、
  `dotnet test gui/Kl.Gui.HeadlessTests`（真实 Avalonia headless 布局/交互）。

## 当前状态（2026-11-25）

- 分支：`dev`；最近提交：见 `git log`（历史已于 2026-11-25 压缩）。
- **工作区有未提交改动**（约 25 个已修改文件，另有本文件未跟踪）；新对话开始前先
  `git status` / `git diff` 确认，不要覆盖。
- 测试：`dotnet test kl.sln` = 260 GUI + 7 headless + 55 TUI 全绿；
  `cargo test --workspace` 全绿。
- 最近完成（未提交）：
  - `turn.start` 新增 `reasoning_effort`，按模型 `reasoning_efforts` 校验，
    只影响本回合，不写配置；SDK / `CoreSessionService` / Composer 全链路。
  - 输入框强度滑条按当前模型的支持级别动态生成档位；选择按 `provider/model`
    存到 `~/.kl/gui/state.json` 的 `ModelReasoningEfforts`；回落链：
    已保存 → 模型默认 → 更低的最近档 → 第一档。
  - 强度标签统一显示原始 id（`low`/`medium`/`max` 等），颜色按档位从强调蓝渐变到紫色。
  - 胶囊布局：模型名自然宽度 + 48px 强度槽，整体居中；打开浮层时显示
    「选择模型与强度」；模型列表弹层宽度按最长模型名自适应（最小 254px）。
  - 设置页模型编辑表单补上可用强度勾选 + 默认强度。

## 已知缺口（来自 README）

- 真实 Provider / MCP live smoke 需要凭据或本地 MCP 服务器。
- 打包后的 `kl serve` smoke 未完成；多窗口自动重连 UX 仍是 Phase 2。
- Provider prompt caching 未完成；会话 rewind 不撤销工具副作用；
  fork 会话不带事件，rewind/regenerate 不可用。
- MCP 服务器在 Core 启动工作区启动，不是每会话工作区。
- Windows AppContainer 下 PTY 会 fail closed。
- GUI/SDK/插件拆仓尚未开始。

## 文档与外部计划

- `README.md`、`docs/README.md`
- `docs/architecture/overview.md`
- `docs/project-structure.md`
- `docs/security/policy.md`
- `docs/plugin-api/README.md`
- `docs/gui/mvp-spec.md`
- `docs/remote-core.md`
- `docs/adr/`
- 产品/UI 计划在仓库外：`D:\kl\KL方案.md`、`D:\kl\UI方案.md`。

## 开发约定

- 未经明确要求不要 commit / push。
- 不打印、缓存或提交 `.env`、`credentials/*.key`、auth/session 内容。
- 改 provider 配置必须通过 Core RPC 或明确的配置编辑，并保留用户已有设置。
- 新依赖需先确认；能用现有实现就不要加抽象。
- 非平凡逻辑要有测试；改完跑对应测试和 `check_i18n.py` / `check_protocol.py`。
- GUI 的体验类改动优先在真实应用里验证（`D:\kl\bin\gui\kl.exe`）。
