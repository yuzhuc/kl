# KL project structure

## Top level

```text
crates/       Rust core and capability crates
tui/          C# terminal client and tests
gui/          C# Avalonia GUI scaffold
sdk/          reusable C# client library
schemas/      protocol and plugin schemas
benchmarks/   acceptance tasks and fixtures
scripts/      package, benchmark and live-smoke scripts
docs/         architecture, security, plugin and ADR docs
tests/        cross-language contract and provider fixtures
plugins/      plugin examples
```

Generated output such as `target/`, `bin/`, `obj/` and `dist/` is ignored.

## Rust crates

| Crate | Responsibility |
|---|---|
| `kl-protocol` | Stable protocol DTOs, methods, errors and event schema |
| `kl-core` | JSON-RPC server, sessions, agent orchestration and extension wiring |
| `kl-agent` | Agent loop, tool dispatch, retries and context compaction |
| `kl-provider` | Provider adapters and streaming/usage handling |
| `kl-tools` | Built-in tools and tool metadata |
| `kl-policy` | Action/resource policy and stage-scoped access |
| `kl-sandbox` | OS sandbox backends and sandbox runner |
| `kl-store` | Session/state/event persistence |
| `kl-audit` | Audit JSONL and SQLite index |
| `kl-review` | Local and optional model review pipeline |
| `kl-index` | Shallow repository index and test selection |
| `kl-plugin-host` | Sidecar/WASM plugin host and integrity checks |
| `kl-exec` | Process spawning, timeout, cancellation and bounded output |

## C# projects

| Project | Responsibility |
|---|---|
| `tui/Kl.Tui` | Terminal UI, command parsing, rendering and approvals; assembly name `kl` |
| `tui/Kl.Tui.Tests` | TUI, protocol contract and core integration tests |
| `gui/Kl.Gui` | Avalonia GUI scaffold; not yet an MVP |
| `sdk/Kl.Client` | JSON-RPC connection, session client, DTOs and core locator |

The TUI and GUI should share `Kl.Client`; neither owns an agent loop.

## Schemas and fixtures

- `schemas/protocol/v1/` — envelope and method schema.
- `schemas/plugin/manifest-v1.schema.json` — plugin manifest.
- `tests/fixtures/protocol/` — shared Rust/.NET contract fixtures.
- `tests/fixtures/providers/` — live smoke status and recorded fixtures.
- `benchmarks/fixtures/` — self-contained task workspaces.

## Build and release

- Rust: `cargo build --release -p kl-core`.
- TUI: `dotnet build tui/Kl.Tui/Kl.Tui.csproj`.
- GUI: `dotnet build gui/Kl.Gui/Kl.Gui.csproj` once the scaffold is evolved.
- Packaging: `scripts/package.mjs` builds a self-contained TUI bundle.
- Release: only `main` pushes, tag `v<version>-rcN`, archives only.
