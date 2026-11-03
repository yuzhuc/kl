# KL

KL is a local-first AI coding agent with a Rust trusted core and C# clients.

- **Core:** Rust, JSON-RPC 2.0 over stdio or a local named-pipe/UDS server.
- **TUI:** C# Terminal.Gui v2 client in `tui/Kl.Tui`.
- **GUI:** Avalonia 12.1.4 desktop client in `gui/Kl.Gui`.
- **Shared client:** `sdk/Kl.Client`, used by the TUI and available for future GUI integration.

The project is designed around local execution, explicit approvals, bounded
context, auditable tool calls and provider neutrality. Python is allowed for
development tooling, but product binaries and runtime do not depend on Python.

## Status

Core, TUI, security, context/memory tools, local multi-client transport and
release packaging are implemented. The GUI provides streaming chat, sessions,
settings, plugins and automation through an OpenAI-compatible provider. Its
`Kl.Client` integration remains a separate future step. Real provider/MCP live
smoke still needs credentials or a configured local server.

## Architecture

```text
TUI (tui/Kl.Tui)     Headless/CI       kl GUI
        \                  /            (standalone)
         \             Kl.Client              |
          \                |                  v
           +-- JSON-RPC / stdio or local socket --+
                            |
                     Rust Core (kl-core)     OpenAI-compatible API
              Protocol / Session / Events / Audit
              Agent / Tools / Policy / Sandbox
              Skills / Hooks / MCP / Plugins
```

The Core owns sessions, events, policy, sandboxing, audit and tool execution.
TUI and headless clients access it through `Kl.Client` and do not execute tools
directly. See [`docs/architecture/overview.md`](docs/architecture/overview.md).

## Repository layout

| Path | Purpose |
|---|---|
| `crates/` | Rust core and capability crates |
| `tui/Kl.Tui` | C# terminal client |
| `tui/Kl.Tui.Tests` | TUI, protocol and core integration tests |
| `gui/Kl.Gui` | Avalonia desktop chat client |
| `sdk/Kl.Client` | reusable C# JSON-RPC/session client |
| `schemas/` | protocol and plugin JSON schemas |
| `benchmarks/` | acceptance tasks and fixtures |
| `scripts/` | Python build, package, validation and benchmark scripts |
| `docs/` | architecture, security, ADR and API docs |

Cargo and .NET intermediates remain in repository cache directories. Final local builds are staged to `D:\kl\bin`.

## Prerequisites

- Rust stable (see `rust-toolchain.toml`)
- .NET 10 SDK
- Python 3.11+ for development tooling only
- Git

Release bundles are self-contained or use the bundled runtime; users do not
need a local Rust toolchain. Source builds need Rust and .NET.

## Build and run

```powershell
python scripts/build.py
```

The build script keeps Cargo and .NET caches inside the repository and publishes
the final GUI/TUI files to the sibling `bin` directory:

```text
Caches:        D:\kl\kl\target, project bin/obj directories
Final outputs: D:\kl\bin\gui, D:\kl\bin\tui
```

Local builds are intentionally framework-dependent and require the .NET 10
Runtime on the target machine (on Windows, the .NET 10 Desktop Runtime includes
it). This removes the bundled `System.*` runtime DLLs and keeps only application
dependencies. The script detects the current OS/architecture automatically and
creates `.bat` launchers on Windows or `.sh` launchers on Linux/macOS.
Self-contained bundles remain the responsibility of the release packaging
workflow, not local development builds.

For component-level development:

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
dotnet run --project tui/Kl.Tui -- serve
```

After `python scripts/build.py`, the desktop GUI can be started with:

```powershell
D:\kl\bin\run-gui.bat
```

## Main capabilities

- Provider-neutral agent loop with `build`, `plan`, `explore`, `review`,
  `verify` and `deep` presets.
- Built-in file, search, shell, git, worktree, job, subagent, memory, todo,
  ask_user and allowlisted web_fetch tools.
- Unified tool pipeline: validation, hooks, policy, approval, sandbox,
  execution, result bounds, audit and events.
- Windows AppContainer, Linux `bubblewrap` and macOS Seatbelt sandboxes.
- MCP server processes start through the active `SandboxManager`.
- Persistent sessions with event projection, resume, fork and approval rules.
- Optional local server with attach/replay, sequence de-duplication, reconnect,
  one-writer session leases and read-only observer clients.
- Automatic model-summary context compaction with mechanical fallback, explicit
  `/compact`, and a 23-task acceptance harness.
- Workspace and user memory injection with bounded prompt size and `/memory`.
- Shell commands support `tty=true`; `tty=true` uses portable-pty where the
  sandbox backend supports it.
- Transient provider retry with exponential backoff and cancellation.
- Built-in model catalog and per-turn/cumulative token usage with `/cost`.

## Configuration

KL reads user configuration from `~/.kl/`:

- `config.toml` — providers, models, agent, policy, sandbox and review settings.
- `credentials/<provider>.key` — provider API keys.
- `agents/*.toml` — custom agent presets.
- `skills/` — user-level skills.
- `mcp.toml` — enabled MCP stdio servers.
- `gui/state.json` — desktop GUI state and conversations.
- `plugins/` — shared local plugin directory used by the GUI and Core.

The GUI automatically migrates legacy `%LOCALAPPDATA%\DeepSeekHarnessGui`
data into `~/.kl/gui` and removes the old directory.

Workspace-level `AGENTS.md`, `.kl/memory/` and project skills are discovered.
See [`docs/security/policy.md`](docs/security/policy.md) for the permission
model. Permission modes include `ask`, `plan`, `acceptEdits`, `dontAsk`, `auto`
and `read-only`; unknown model prices are never invented.

## Sandbox and security

- `strict` mode requires a real OS backend and fails closed when unavailable.
- Windows uses AppContainer with filesystem, child-process and loopback network
  isolation.
- Linux uses `bubblewrap` with read-only host mounts, writable workspace roots
  and an isolated network namespace when network access is denied.
- macOS uses a deny-by-default Seatbelt profile through `sandbox-exec`.
- Shell commands and MCP servers run through the same sandbox boundary.
- High-risk actions require approval. A decision can be `allow`, `deny`, or
  `allow for this session` (`allow_session`); session rules are persisted in
  session state.
- `web_fetch` requires a configured allowlist, revalidates every redirect and
  rejects loopback, private, link-local, multicast and userinfo URLs.

## Release

Releases are built only from pushes to `main`. The workflow reads the workspace
version, creates `v<version>-rc1` and then `-rc2`, `-rc3`, and so on. It retains
only the current `v<major>.<minor>.` series and publishes archives without `.sha256` files.

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
- Windows release packaging still needs `kl-sandbox-runner.exe` included and a
  packaged `kl serve` smoke test.
- GUI uses an OpenAI-compatible provider directly; `Kl.Client` integration
  remains in progress.
- Provider prompt caching and session rewind/revert are not complete.
- MCP servers start in the Core startup workspace, not a per-session workspace.
- PTY under Windows AppContainer fails closed because that backend cannot be
  wrapped by an external PTY runner.
- The repository split into GUI/SDK/plugin repositories has not started.

## Documentation

- [`docs/README.md`](docs/README.md)
- [`docs/architecture/overview.md`](docs/architecture/overview.md)
- [`docs/project-structure.md`](docs/project-structure.md)
- [`docs/security/policy.md`](docs/security/policy.md)
- [`docs/plugin-api/README.md`](docs/plugin-api/README.md)
- [`docs/gui/mvp-spec.md`](docs/gui/mvp-spec.md)
- [`docs/adr/`](docs/adr/)