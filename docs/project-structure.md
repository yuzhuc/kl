# KL 项目目录说明

KL 是一个本地 AI 编程代理：Rust 负责可信核心，C#/.NET 负责终端 CLI，
两者通过 stdio 上的 JSON-RPC 2.0 通信。项目不使用 Python。

```text
kl.exe (C#/.NET, 终端 UI / 命令解析 / 审批交互)
   │  JSON-RPC 2.0，逐行 JSON，stdin/stdout
   ▼
kl-core.exe (Rust, 真正的 Agent 运行时)
   ├─ ProviderRouter        模型协议适配与路由
   ├─ Agent Loop            模型调用 ↔ 工具调用状态机
   ├─ ToolRegistry          内置工具 + 插件工具
   ├─ PolicyEngine          路径/命令/Git/网络/审批策略
   ├─ SandboxManager        AppContainer 等 OS 级隔离
   ├─ AuditLog              审计 JSONL + SQLite 索引
   ├─ SessionStore          session.json / state.json / events.jsonl
   ├─ ReviewPipeline        代码审查与质量模式
   └─ RepoIndex             仓库轻量索引与测试选择
```

## 仓库根目录

| 路径 | 作用 |
| --- | --- |
| `Cargo.toml` / `Cargo.lock` | Rust workspace 定义，当前包含 13 个 crate；edition 2024、resolver 3。 |
| `kl.sln` | .NET 解决方案，包含 `tui/Kl.Tui` 与 `tui/Kl.Tui.Tests`。 |
| `README.md` | 项目总览、架构图、构建与运行方式。 |
| `rust-toolchain.toml` | Rust 工具链版本约束。 |
| `rustfmt.toml` | Rust 格式化配置。 |
| `.github/workflows/ci.yml` | GitHub Actions：Windows/Ubuntu 上跑 fmt、clippy、测试、构建和 Windows Demo。 |
| `.gitignore` | 忽略 `target/`、`dist/`、`bin/`、`obj/`、`.kl/`、`.env`、密钥文件等。 |
| `target/` | Cargo 编译缓存和产物，删除后可重新生成。 |
| `dist/` | 打包产物；`dist/win-x64/` 是 Windows 自包含发布目录。 |
| `kl.toml`（运行时，可选） | 通过 `--config` 或 `KL_CONFIG` 指定的主配置；默认位于 `~/.kl/config.toml`。 |

## Rust Crates（`crates/`）

### `kl-protocol`

协议契约层。定义 JSON-RPC 请求/响应/通知信封、协议版本协商、事件结构、
稳定错误码和握手信息。C# 侧的 `tui/Kl.Tui/Protocol.cs` 与
`schemas/protocol/v1/` 必须与它保持一致。

适合修改：新增 RPC 方法、事件字段、错误码、协议版本。

### `kl-core`

核心可执行程序（`kl-core.exe`）。负责：

- stdio JSON-RPC 服务循环与 `kl.handshake`；
- 全局配置加载、Provider 配置装配、`doctor` 诊断；
- 会话创建/恢复/列表、审批请求与响应；
- 组装 Agent、工具、策略、沙箱、审计、插件、Review 和索引；
- 暴露 `turn.start`、`turn.cancel`、`session.history`、`review.run` 等方法。

这是改动影响面最大的 crate，新增跨模块能力通常从这里接线。

### `kl-agent`

与 Provider 无关的 Agent 循环。维护 `Model → Tools → Model` 状态机、
消息历史、工具调用编排、取消、最大迭代数、token/时间预算，以及
`fast/balanced/deep` 质量模式的阶段调度。它只认识中立的
`ChatRequest` / `ChatEvent` / `ChatMessage`，不关心具体厂商协议。

### `kl-provider`

模型接入层。包含三类原生协议适配器：

- `openai-chat`：OpenAI Chat Completions；
- `anthropic`：Anthropic Messages；
- `openai-responses`：OpenAI Responses。

同时包含用于离线测试的 `scripted` Provider 和 sidecar Provider。
每个模型独立绑定协议与完整 URL，KL 不做协议转换，也不自动拼接固定路径。
SSE 流解析、取消、usage 和模型元数据（`context_window`、
`compact_threshold`、`max_output_tokens`）都在这里。

### `kl-tools`

内置工具实现：`read_file`、`write_file`、`edit_file`、`grep`、`glob`、
`shell`、`git`。工具本身只负责执行；是否允许执行由 `kl-policy` 决定，
进程启动由 `kl-exec` / `kl-sandbox` 负责。

### `kl-policy`

策略引擎。决定每次工具调用是 `allow`、`ask` 还是 `deny`，覆盖：

- 工作区路径包含与越界路径；
- 敏感文件（如 `.env`、密钥）；
- 危险 shell 命令；
- Git 读写分类；
- reviewer 的 `ReadOnly` 与 verifier 的 `VerifyOnly` 工具访问级别；
- 审批效果与超时。

### `kl-sandbox`

OS 级沙箱边界。提供统一的 `SandboxBackend` 接口（probe/spawn/
capabilities/enforce），Windows 上实现 AppContainer token、kill-on-close
Job Object、工作区 ACL 和 `kl-sandbox-runner` 清理环境启动器。
strict 模式下探测失败会 fail-closed。Linux 使用 bubblewrap，macOS 使用 Seatbelt；Windows 使用 AppContainer。

### `kl-exec`

进程执行基础设施：启动子进程、超时、取消、输出上限、退出码和
stdout/stderr 收集。被 shell 工具、Git 工具和部分测试辅助代码复用。

### `kl-audit`

运行时审计。每次工具调用（包括被拒绝和无效调用）写入追加式 JSONL，
并建立 SQLite 查询索引；支持轮转、查询和按会话/工具/结果过滤。
`kl review` 会把审计中的 deny / 沙箱违规转换成审查发现。

### `kl-store`

会话持久化。目录结构通常为：

```text
<KL_HOME>/sessions/<session_id>/
  session.json    会话元数据
  state.json      消息投影（用于恢复）
  events.jsonl    增量事件日志（turn/tool/approval 等）
```

负责创建会话、写入事件、投影消息、恢复上下文和提供最近历史。

### `kl-review`

代码审查流水线：收集 `git diff` 与未跟踪文件，运行确定性规则、
安全/密钥/依赖扫描，合并运行时审计发现，可选调用模型做结构化审查，
最后去重并按 `quick/standard/strict` 置信度阈值过滤。

### `kl-index`

仓库轻量索引。通过 `git ls-files --cached --others --exclude-standard`
发现文件，记录语言、行数、mtime 和浅层符号（Rust/C#/TypeScript），
支持路径/符号/语言/最近修改/Git 变更查询，并为变更文件选择相关测试。
缓存位于 `~/.kl/cache/repo-index/`（可用 `KL_INDEX_CACHE_DIR` 覆盖），
不写入工作区。deep 模式只把索引摘要提供给 explorer/planner。

### `kl-plugin-host`

插件宿主。支持 manifest v1、Sidecar JSON-RPC 子进程和 WASM Component
工具插件；负责启动、健康检查、热重载、流量切换、排空、能力检查、
entrypoint SHA-256 完整性校验与 Ed25519 签名校验。

## C# 终端（`tui/`）

### `tui/Kl.Tui`

终端客户端，负责所有用户交互：

| 文件 | 作用 |
| --- | --- |
| `Program.cs` | 入口，分发 `chat`、`run`、`provider`、`plugin`、`review`、`doctor` 等命令。 |
| `CommandLine.cs` | 命令行参数解析、`TuiOptions`。 |
| `CoreLocator.cs` | 查找并启动 `kl-core` 可执行文件。 |
| `JsonRpcConnection.cs` / `JsonLineCodec.cs` | stdio JSON-RPC 连接与逐行编解码。 |
| `KlCoreClient.cs` | 通用 Core RPC 调用。 |
| `KlSessionClient.cs` | 会话、turn、审批、历史等会话级 RPC。 |
| `SessionUi.cs` | 非 TUI 的流式输出、Diff、审批提示。 |
| `ChatTuiState.cs` | 聊天页状态、输入编辑、按键映射、历史/快捷键页面状态。 |
| `ChatTuiRenderer.cs` | 聊天页 ANSI 渲染、滚动、状态栏和底部输入框。 |
| `ChatTuiRunner.cs` | 聊天页事件循环、键盘读取、turn 生命周期、页面切换。 |
| `SettingsTui.cs` | 设置页状态机（Model / Plugins / Config 等分组）。 |
| `SettingsTuiRenderer.cs` | 设置页左右两栏渲染。 |
| `SettingsTuiRunner.cs` | 设置页启动、保存、语言切换与 Core 同步。 |
| `SettingsTexts.cs` | 设置页中英文文案。 |
| `ProviderCli.cs` | `kl provider add/list/...` 非交互命令。 |
| `ReviewOutput.cs` | `kl review` 结果输出。 |
| `ConsoleUi.cs` | 通用控制台输出辅助。 |
| `Protocol.cs` | 与 Rust 协议层对应的 C# DTO。 |

### `tui/Kl.Tui.Tests`

xUnit 测试。覆盖参数解析、聊天状态机、设置页渲染、Provider 配置、
协议 DTO、非交互输出等。当前 .NET 测试是 CLI 行为的主要回归保障。

## 其他目录

### `schemas/`

- `schemas/protocol/v1/envelope.schema.json`：JSON-RPC 信封；
- `schemas/protocol/v1/methods.schema.json`：各 RPC 方法与事件结构；
- `schemas/plugin/manifest-v1.schema.json`：插件 manifest。

修改协议或插件格式时必须同步 schema、Rust 类型、C# DTO 和 fixture。

### `tests/`

- `tests/fixtures/protocol/`：协议请求/响应/事件样例，Rust contract 测试使用；
- `tests/fixtures/providers/`：Provider 状态与 live smoke 记录；
- `tests/fixtures/review-repo/`：`kl review` 的确定性 fixture 仓库。

### `examples/`

可运行的本地 Demo 模板：

- `examples/e2e-demo-template/`：端到端读写与 `cargo test` 演示；
- `examples/policy-demo-template/`：危险命令与越界路径拒绝演示。

### `plugins/examples/`

插件示例：

- `fake-provider/`：sidecar Provider；
- `fake-tool/`：sidecar Tool；
- `wasm-tool/`：WASM Component Tool（`tool.wat`）。

### `scripts/`

| 脚本 | 作用 |
| --- | --- |
| `build.sh` / `build.ps1` | 跨平台构建入口。 |
| `build.mjs` | Node.js 构建脚本（不使用 Python）。 |
| `build-local.ps1` | 本机构建并输出到 `E:\kl`，已验证可直接运行 `E:\kl\kl.exe`。 |
| `package.ps1` | 打包发布，可传 `-OutputDir`。 |
| `demo-e2e.ps1` | 端到端读写 + 测试演示。 |
| `demo-policy.ps1` | 策略拒绝演示。 |
| `demo-plugin.ps1` | 插件安装/热升级演示。 |
| `demo-review.ps1` | 审查流水线演示。 |
| `live-smoke.ps1` | 三种协议的真实 Provider 联调与真实 MCP stdio 联调（需要 API Key / 已配置 MCP 服务）。 |

### `docs/`

- `docs/adr/`：0001–0010 架构决策记录（Rust/C# 分工、stdio RPC、
  插件边界、打包、sidecar、review、WASM、签名、索引等）；
- `docs/architecture/overview.md`：请求生命周期、质量模式、索引、审查；
- `docs/security/policy.md`：策略与沙箱安全模型；
- `docs/plugin-api/`：插件 ABI 与 manifest 说明；
- `docs/ui/`：设置页参考与 UI 设计记录；
- `docs/project-structure.md`：本文档。

## 运行时数据目录

默认 `KL_HOME` 为 `~/.kl`（Windows 为 `C:\Users\<用户>\.kl`，
可用 `KL_HOME` 覆盖）：

```text
~/.kl/
  config.toml              主配置（默认 Provider/Model 等）
  providers/<id>.toml      每个 Provider 一个配置文件
  credentials/<id>.key     Provider API Key（不写入 Provider TOML）
  sessions/<id>/           会话元数据、消息投影、事件日志
  audit/                   审计 JSONL 与 SQLite 索引
  ui.json                  终端 UI 语言等偏好
  cache/repo-index/        仓库索引缓存
```

工作区内的 `.kl/` 被 `.gitignore` 忽略，通常用于测试或本地临时数据。

## 修改入口速查

| 想做的事情 | 优先查看 |
| --- | --- |
| 新增模型协议 | `crates/kl-provider`、`kl-protocol`、`schemas/protocol` |
| 新增 RPC 方法 | `crates/kl-protocol`、`crates/kl-core/src/lib.rs`、`tui/Kl.Tui/KlCoreClient.cs` |
| 新增内置工具 | `crates/kl-tools`、`kl-policy`、`kl-protocol` |
| 调整审批/安全策略 | `crates/kl-policy`、`crates/kl-sandbox` |
| 改聊天页 UI | `tui/Kl.Tui/ChatTui*.cs` |
| 改设置页 UI | `tui/Kl.Tui/SettingsTui*.cs`、`SettingsTexts.cs` |
| 改 Agent 行为 | `crates/kl-agent`、`crates/kl-core` |
| 改审查规则 | `crates/kl-review` |
| 改插件能力 | `crates/kl-plugin-host`、`schemas/plugin`、`docs/plugin-api` |
| 改构建/打包 | `scripts/`、`Cargo.toml`、`kl.sln` |
