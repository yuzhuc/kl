# KL project structure

## Top level

```text
crates/       Rust core and capability crates
tui/          C# Terminal.Gui client and tests
gui/          C# Avalonia GUI shell
sdk/          reusable C# client library
schemas/      protocol and plugin schemas
benchmarks/   acceptance tasks and fixtures
scripts/      package, benchmark and live-smoke scripts
docs/         architecture, security, plugin and ADR docs
tests/        cross-language contract and provider fixtures
plugins/      plugin examples
```

Generated output such as `target/`, `bin/`, `obj/`, `dist/` and benchmark
workspaces is ignored or treated as disposable.

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
| `gui/Kl.Gui` | Avalonia 12.1.4 static shell; client integration paused |
| `sdk/Kl.Client` | JSON-RPC connection, session client, DTOs and endpoint discovery |

The TUI and GUI share `Kl.Client`; neither owns an agent loop. `KlSessionClient`
supports stdio, local-server connect, attach/replay, sequence de-duplication,
reconnect from `LastSeq`, protocol-backed rich history and read-only observer
sessions when another client holds the write lease.

## Protocol and fixtures

- `schemas/protocol/v1/` — envelope, event and method schemas.
- `schemas/plugin/manifest-v1.schema.json` — plugin manifest.
- `tests/fixtures/protocol/` — shared Rust/.NET contract fixtures.
- `tests/fixtures/providers/` — live-smoke status and recorded fixtures.
- `benchmarks/fixtures/` — self-contained task workspaces.

## Build and release

- Rust: `cargo build --release -p kl-core`.
- TUI: `dotnet build tui/Kl.Tui/Kl.Tui.csproj`.
- GUI shell: `dotnet build gui/Kl.Gui/Kl.Gui.csproj`.
- Packaging: `scripts/package.mjs` builds a self-contained TUI bundle.
- Release: only `main` pushes, tag `v<version>-rcN`, archives only.