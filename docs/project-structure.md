# KL project structure

## Top level

```text
crates/       Rust core and capability crates
tui/          C# Terminal.Gui client and tests
gui/          C# Avalonia desktop client and tests
sdk/          reusable C# client library
schemas/      protocol and plugin schemas
benchmarks/   acceptance tasks and fixtures
scripts/      Python build, validation and benchmark tooling
docs/         architecture, security, plugin and ADR docs
tests/        cross-language contract and provider fixtures
plugins/      plugin examples
```

Cargo caches stay in `target/`; .NET intermediates stay in project `bin/` and
`obj/` directories. `python scripts/build.py` stages final GUI/TUI files in the
sibling `bin/` directory, while benchmark workspaces are disposable.

## Rust crates

| Crate | Responsibility |
|---|---|
| `kl-protocol` | Stable protocol DTOs, methods, errors and event schema |
| `kl-core` | stdio/local-socket server, sessions, agent orchestration and extension wiring |
| `kl-agent` | Agent loop, tool dispatch, retries and context compaction |
| `kl-provider` | Provider adapters, catalog metadata and streaming/usage handling |
| `kl-tools` | Built-in tools, tool metadata and the unified execution contract |
| `kl-policy` | Action/resource policy and stage-scoped access |
| `kl-sandbox` | OS sandbox backends and the sandbox runner |
| `kl-store` | Session metadata, state and event persistence |
| `kl-audit` | Audit JSONL and SQLite index |
| `kl-review` | Local and optional model review pipeline |
| `kl-index` | Shallow repository index and test selection |
| `kl-plugin-host` | Sidecar/WASM plugin host and integrity checks |
| `kl-exec` | Process spawning, timeout, cancellation and bounded output |

## C# projects

| Project | Responsibility |
|---|---|
| `tui/Kl.Tui` | Terminal.Gui v2 UI, commands, approvals and terminal rendering |
| `tui/Kl.Tui.Tests` | TUI, protocol, server and core integration tests |
| `gui/Kl.Gui` | Avalonia desktop client over `Kl.Client` |
| `gui/Kl.Gui.Tests` | GUI view-model and service tests |
| `sdk/Kl.Client` | JSON-RPC connection, session client, DTOs and endpoint discovery |

The TUI and the GUI both use `Kl.Client` and do not own an agent loop.
`KlSessionClient` supports stdio, local-server connect, attach/replay, sequence
de-duplication, reconnect from `LastSeq`, protocol-backed rich history and
read-only observer sessions when another client holds the write lease. The GUI
adds `gui/Kl.Gui/Services/CoreSessionService.cs`, `CoreSessionPresenter.cs`,
`CoreProtocolModels.cs` and `ViewModels/MainWindowViewModel.Core.cs`; it renders
Core sessions and events but keeps provider/model/credential management and all
execution inside Core.

## Protocol and fixtures

- `schemas/protocol/v1/` — envelope, event and method schemas.
- `schemas/plugin/manifest-v1.schema.json` — plugin manifest.
- `tests/fixtures/protocol/` — shared Rust/.NET contract fixtures.
- `tests/fixtures/providers/` — live-smoke status and recorded fixtures.
- `benchmarks/fixtures/` — self-contained task workspaces.

## Build and release

- Rust: `cargo build --release -p kl-core`.
- TUI: `dotnet build tui/Kl.Tui/Kl.Tui.csproj`.
- GUI: `dotnet build gui/Kl.Gui/Kl.Gui.csproj`.
- Build: `python scripts/build.py` publishes final GUI/TUI files to the sibling
  `bin` directory and stages `kl-core.exe` next to both clients.
- Packaging: `python scripts/package.py` builds a self-contained release archive
  (`--gui` adds the Avalonia client under `gui/` on Windows and macOS RIDs).
- Release: only `main` pushes, tag `v<version>-rcN`, archives only.