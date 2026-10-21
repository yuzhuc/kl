# KL architecture

## Process model

```text
TUI (tui/Kl.Tui)     GUI (gui/Kl.Gui)     Headless/CI
        \                  |                  /
         \             Kl.Client             /
          \                |                /
           +-- JSON-RPC / stdio or local socket --+
                            |
                     Rust core (kl-core)
              Emitter + JSON-RPC loop
              ProviderRouter + Agent runtime
              SessionStore + EventHub
              PolicyEngine + SandboxManager
              ToolRegistry + AuditLog
              Skills / Hooks / MCP / Plugins
```

The Rust core is the only process that executes tools, calls providers, writes
sessions or evaluates policy. The TUI, GUI and headless clients are protocol
clients.

## Repository boundaries

| Layer | Responsibility |
|---|---|
| `kl-protocol` | Wire envelopes, events, errors, DTOs and stable constants |
| `kl-core` | Server, sessions, agent orchestration, RPC and extension wiring |
| `kl-agent` | Provider-neutral turn loop, tool dispatch and compaction |
| `kl-provider` | OpenAI Chat, Anthropic, Responses and scripted/plugin routes |
| `kl-tools` | Built-in tools and the unified tool contract |
| `kl-policy` | Action/resource evaluation and stage-scoped access |
| `kl-sandbox` | OS sandbox backends, probing and launch helpers |
| `kl-store` | Session metadata, state projection and event log |
| `kl-audit` | Rotated JSONL audit plus SQLite query index |
| `kl-review` | Diff, security and optional LLM review pipeline |
| `kl-index` | Shallow repository symbols and test selection hints |
| `kl-plugin-host` | Sidecar/WASM plugin supervision and integrity checks |
| `Kl.Client` | C# JSON-RPC/session client used by TUI and GUI |
| `Kl.Tui` | Terminal.Gui v2 terminal UI, rendering, input and approval presentation |
| `Kl.Gui` | Avalonia shell; currently a scaffold |

## Protocol

- One JSON-RPC 2.0 message per line over stdio or a local socket.
- `kl serve` uses a Windows named pipe or a Unix/macOS filesystem UDS.
- `kl.handshake` negotiates the protocol version before session/turn methods.
- `kl.ping` and `kl.doctor` work before the handshake for diagnostics.
- Core events are notifications with `method: "event"` and a per-session
  monotonically increasing `seq`.
- `schemas/protocol/v1` is the contract; Rust and .NET tests consume the same
  fixtures under `tests/fixtures/protocol`.

- `server.info` exposes the endpoint and transport. `session.attach` replays
  stored events and subscribes to live events; `session.lease` enforces one
  writer per session; `session.detach` releases both.

Representative methods: `session.create`, `session.resume`, `session.fork`,
`session.history`, `session.compact`, `turn.start`, `turn.cancel`,
`tool.approval.respond`, `provider.*`, `skill.*`, `mcp.list`, `plugin.*`,
`review.run`, `audit.tail`.

## Tool pipeline

Every tool call follows the same path:

```text
validate -> pre-hook -> policy -> approval -> sandbox
  -> execute -> post-hook -> bounded result -> audit -> event
```

Tool metadata includes read-only/destructive flags, concurrency safety,
validation, result limits and source. Concurrency-safe calls may run in
parallel; mutating calls remain ordered. Result bodies are bounded before they
enter session history.

## Sessions and events

- `session.json` stores metadata.
- `state.json` stores the message projection.
- `events.jsonl` is the append-only source of truth.
- Missing or empty state can be reconstructed from events.
- Sessions support resume, fork, rename, delete and history queries.
- Local-server attach replays `events.jsonl` by sequence and rejects a second
  writer with `SESSION_BUSY`. Disconnected clients release their lease.
- Approval `allow_session` rules are stored with the session and survive reload.
- Layered compaction first prunes large tool results, then drops older rounds.

## Sandbox

`sandbox.mode = "policy"` is the default. `strict` requires a real OS backend
and fails closed if it is unavailable.

| Platform | Backend |
|---|---|
| Windows | AppContainer token, filesystem grants, kill-on-close Job Object, loopback network verification |
| Linux | bubblewrap, read-only host, writable roots, user/PID/IPC/UTS and optional network namespaces |
| macOS | deny-by-default Seatbelt profile via `/usr/bin/sandbox-exec` |

Shell PTY is implemented through portable-pty where an external PTY can wrap
the backend; Windows AppContainer fails closed. MCP stdio servers and shell
commands use the same sandbox manager.

## Extensions

- **Skills:** discovered from trusted home/workspace roots; summaries are
  injected, bodies load on demand.
- **Hooks:** trusted pre/post tool hooks can deny and write audit records.
- **MCP:** stdio servers, tool registration, `isError` mapping and sandboxed
  server processes.
- **Plugins:** manifest v1 sidecar and WASM tools, optional Ed25519 integrity
  checks and hot reload.
- **Review/deep:** reviewer and verifier use stage-scoped tool access and a
  workspace fingerprint guard.

## GUI/TUI reuse

`Kl.Client` owns the C# protocol connection and session API. The TUI adds
terminal rendering and input. The GUI must reuse `Kl.Client` and the same
session/events, not copy the agent loop or tool execution.

## Release

Releases are built only from pushes to `main` and are tagged `v<version>-rcN`.
The runtime identifier matrix is final:

| Platform | RIDs |
|---|---|
| Windows | win-x64, win-arm64 |
| Linux | linux-x64, linux-arm64 |
| macOS | osx-arm64 |

`scripts/package-format.mjs` accepts only these RIDs and verifies that the
packaged binaries really match them. Adding or removing a RID requires an ADR
([`../adr/0011-release-rid-matrix.md`](../adr/0011-release-rid-matrix.md)).

## Known gaps

- Full GUI MVP and GUI release packaging.
- Real provider/MCP live smoke with credentials.
- Provider prompt caching.
- Session rewind/revert and MCP resources/OAuth.
- Per-session MCP workspace.
- Repository split.
