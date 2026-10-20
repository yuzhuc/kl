# KL

KL is a local-first AI coding agent with a Rust trusted core and C# clients.

- **Core:** Rust, JSON-RPC 2.0 over stdio, provider-neutral agent loop.
- **TUI:** C# terminal client in `tui/Kl.Tui`.
- **GUI:** Avalonia scaffold in `gui/Kl.Gui` (MVP still under construction).
- **Shared client:** `sdk/Kl.Client`, used by the TUI and intended for the GUI.

The project does not use Python and is designed around local execution,
explicit approvals and auditable tool calls.

## Status

The core, TUI, security model, extension layer and release pipeline are
implemented. The GUI is initialized but not yet a complete client. Real
provider/MCP live smoke still needs credentials or a configured local server.

## Architecture

```text
TUI (tui/Kl.Tui)     GUI (gui/Kl.Gui)     Headless/CI
        \                  |                  /
         \             Kl.Client             /
          \                |                /
           +------- JSON-RPC / stdio -------+
                            |
                     Rust Core (kl-core)
              Protocol / Session / Events / Audit
              Agent / Tools / Policy / Sandbox
              Skills / Hooks / MCP / Plugins
```

The Core owns sessions, events, policy, sandboxing, audit and tool execution.
Clients never call a model or execute a tool directly. See
[`docs/architecture/overview.md`](docs/architecture/overview.md).

## Repository layout

| Path | Purpose |
|---|---|
| `crates/` | Rust core and capability crates |
| `tui/Kl.Tui` | C# terminal client |
| `tui/Kl.Tui.Tests` | TUI and protocol contract tests |
| `gui/Kl.Gui` | Avalonia GUI scaffold |
| `sdk/Kl.Client` | reusable C# JSON-RPC/session client |
| `schemas/` | protocol and plugin JSON schemas |
| `benchmarks/` | acceptance tasks and fixtures |
| `scripts/` | package, benchmark and live-smoke scripts |
| `docs/` | architecture, security, ADR and API docs |

## Prerequisites

- Rust stable (see `rust-toolchain.toml`)
- .NET 10 SDK
- Node.js 22+ for repository scripts
- Git

Release bundles are self-contained: users need neither the .NET runtime nor
the Rust toolchain. Source builds need both SDKs.

## Build and run

```powershell
cargo build --release -p kl-core
dotnet build tui/Kl.Tui/Kl.Tui.csproj
```

Common TUI commands:

```powershell
dotnet run --project tui/Kl.Tui -- doctor
dotnet run --project tui/Kl.Tui -- run "fix the failing test" --workspace . --yes
dotnet run --project tui/Kl.Tui -- chat --workspace .
dotnet run --project tui/Kl.Tui -- resume
dotnet run --project tui/Kl.Tui -- review --workspace .
```

The GUI scaffold can be started with:

```powershell
dotnet run --project gui/Kl.Gui
```

It currently opens a minimal Avalonia window; the full GUI MVP is future work.

## Main capabilities

- Provider-neutral agent loop with `build`, `plan`, `explore`, `review`,
  `verify` and `deep` presets.
- Built-in file, search, shell, git, worktree, job, subagent and workspace
  memory tools.
- Unified tool pipeline: validation, hooks, policy, approval, sandbox,
  execution, result bounds, audit and events.
- Windows AppContainer, Linux bubblewrap and macOS Seatbelt sandboxes.
- MCP server processes start through the active `SandboxManager`.
- Persistent sessions with event projection, resume, fork and approval rules.
- Layered context compaction and a 23-task acceptance harness.
- Skills, hooks, sidecar/WASM plugins and Ed25519 plugin integrity checks.
- Shell commands support `tty=true`; `tty=true` uses portable-pty where the sandbox backend supports it.
- Transient provider retry with exponential backoff and cancellation.

## Configuration

KL reads user configuration from `~/.kl/`:

- `config.toml` — providers, models, agent, policy, sandbox and review settings.
- `credentials/<provider>.key` — provider API keys.
- `agents/*.toml` — custom agent presets.
- `skills/` — user-level skills.
- `mcp.toml` — enabled MCP stdio servers.

Workspace-level `AGENTS.md`, `.kl/memory/` and project skills are also
discovered. See [`docs/security/policy.md`](docs/security/policy.md) for the
permission model.

## Sandbox and security

- `strict` mode requires a real OS backend and fails closed when unavailable.
- Windows uses AppContainer with filesystem, child-process and loopback network
  isolation.
- Linux uses `bubblewrap` with read-only host mounts, writable workspace roots and
  an isolated network namespace when network access is denied.
- macOS uses a deny-by-default Seatbelt profile through `sandbox-exec`.
- Shell commands and MCP servers run through the same sandbox boundary.
- High-risk actions require approval. A decision can be `allow`, `deny`, or
  `allow for this session` (`allow_session`); session rules are persisted in
  session state.

## Release

Releases are built only from pushes to `main`. The workflow reads the workspace
version, creates `v<version>-rel1` and then `-rel2`, `-rel3`, and so on. It
retains only the current `v<major>.<minor>.` series and publishes archives
without `.sha256` files.

The current release matrix is:

```text
Windows: win-x64, win-arm64
Linux:   linux-x64, linux-arm64
macOS:   osx-arm64
```

The RID list is final: adding or removing a RID requires an ADR. See
[`docs/adr/0011-release-rid-matrix.md`](docs/adr/0011-release-rid-matrix.md)
and the release matrix in
[`docs/architecture/overview.md`](docs/architecture/overview.md).

## Known gaps

- Real Provider/MCP live smoke requires credentials or a local MCP server.
- GUI MVP, provider cost/cache, model-summary compaction, web tools, memory
  prompt injection and session rewind/revert are not complete.
- MCP servers start in the Core startup workspace, not a per-session workspace.
- PTY under Windows AppContainer fails closed because that backend cannot be
  wrapped by an external PTY runner.
- The repository split into GUI/SDK/plugin repositories has not started.

## Documentation

- [`docs/architecture/overview.md`](docs/architecture/overview.md)
- [`docs/project-structure.md`](docs/project-structure.md)
- [`docs/security/policy.md`](docs/security/policy.md)
- [`docs/plugin-api/README.md`](docs/plugin-api/README.md)
- [`docs/adr/`](docs/adr/)
