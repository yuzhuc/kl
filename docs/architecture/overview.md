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
              Emitter + JSON-RPC request handling
              ProviderRouter + Agent runtime
              SessionStore + EventHub
              PolicyEngine + SandboxManager
              ToolRegistry + AuditLog
              Skills / Hooks / MCP / Plugins
```

The Rust Core is the only process that executes tools, calls providers, writes
sessions or evaluates policy. TUI, GUI and headless clients are protocol
clients. `Kl.Client` owns the C# transport and session API.

## Repository boundaries

| Layer | Responsibility |
|---|---|
| `kl-protocol` | Wire envelopes, events, errors, DTOs and stable constants |
| `kl-core` | stdio/local server, sessions, agent orchestration and extension wiring |
| `kl-agent` | Provider-neutral turn loop, tool dispatch, compaction and retries |
| `kl-provider` | OpenAI Chat, Anthropic, Responses and scripted/plugin routes |
| `kl-tools` | Built-in tools, metadata, policies and the unified tool pipeline |
| `kl-policy` | Action/resource evaluation and stage-scoped access |
| `kl-sandbox` | OS sandbox backends, probing and launch helpers |
| `kl-store` | Session metadata, state projection and event log |
| `kl-audit` | Rotated JSONL audit plus SQLite query index |
| `kl-review` | Diff, security and optional LLM review pipeline |
| `kl-index` | Shallow repository symbols and test selection hints |
| `kl-plugin-host` | Sidecar/WASM plugin supervision and integrity checks |
| `Kl.Client` | C# JSON-RPC/session client used by the TUI and GUI |
| `Kl.Tui` | Terminal.Gui v2 terminal client and approval presentation |
| `Kl.Gui` | Avalonia shell; chat integration is paused |

## Protocol and transports

- One JSON-RPC 2.0 message per line over stdio or a local socket.
- `kl.handshake` negotiates the protocol version before session/turn methods.
- `kl.ping` and `kl.doctor` work before the handshake for diagnostics.
- `kl serve` uses a Windows named pipe or a Unix/macOS filesystem UDS.
- `server.info` exposes the endpoint and transport.
- `session.attach` replays stored events after `after_seq` and subscribes the
  connection to live events. Clients that load history through the protocol can
  skip the initial replay while still recording `last_seq`.
- `session.lease` enforces one writer per session; a second client remains
  attached as a read-only observer. `session.detach` releases the subscription
  and lease.
- Core events are notifications with `method: "event"` and a per-session,
  monotonically increasing `seq`. Clients de-duplicate by `session_id + seq`.
- `schemas/protocol/v1` is the contract; Rust, schema and C# constants are
  checked together by `scripts/check-protocol.mjs`.
- Shared fixtures live under `tests/fixtures/protocol`.

Representative methods include `session.create`, `session.resume`,
`session.history`, `session.attach`, `session.lease`, `turn.start`,
`turn.cancel`, `tool.approval.respond`, `memory.*`, `provider.*`, `skill.*`,
`mcp.list`, `plugin.*`, `review.run` and `audit.tail`.

## Sessions, replay and reconnect

- `session.json` stores metadata.
- `state.json` stores the current message projection used for exact resume.
- `events.jsonl` is the append-only event log used for replay, audit and a
  best-effort projection when state is missing or empty.
- The event projection rebuilds valid assistant tool calls and paired tool
  results, and restores compaction summaries. Exact compacted tails still rely
  on `state.json`.
- Sessions support resume, fork, rename, delete and history queries.
- Local-server attach replays `events.jsonl` by sequence, permits multiple
  subscribers and enforces a single write lease.
- `KlSessionClient` consumes replay, drops stale sequences, records `LastSeq`
  and can reconnect from that sequence. `session.history` carries reasoning,
  tool output and diff metadata so clients do not read Core files directly.
- Approval `allow_session` rules are stored with the session and survive reload.

## Context and memory

- Automatic turn compaction and `/compact` both ask the active provider for a
  bounded summary when old rounds must be dropped, with a mechanical fallback
  on failure.
- Mechanical compaction prunes large tool results and old rounds while
  preserving complete tool-call/result pairs.
- `context.compacted` records `turn.auto` or `session.compact`, the omitted
  count, estimated tokens, summary status and summary content in the event log.
- Workspace memory lives under `.kl/memory`; user memory under `~/.kl/memory`.
- Memory injection is bounded, workspace-first and audited. `/memory` supports
  list, read, write and delete through protocol methods.

## Tool pipeline

Every tool call follows the same path:

```text
validate -> pre-hook -> policy -> approval
  -> sandbox -> execute -> post-hook
  -> bounded result -> audit -> event
```

Tool metadata includes read-only/destructive flags, concurrency safety,
interrupt behavior, result limits, defer hints and source. `should_defer`,
`destructive` and concurrency hints are internal scheduling metadata; they are
not serialized into provider-visible tool descriptions. Read-only calls may run
in parallel; mutating calls remain ordered. Result bodies are bounded before
they enter session history.

Built-in tools include file operations, grep/glob, shell, git, worktree, jobs,
memory, todo, `ask_user` and allowlisted `web_fetch`. `ask_user` uses the normal
approval channel. `web_fetch` requires a non-empty policy allowlist, revalidates
every redirect and rejects private or special network destinations.

## Sandbox

`sandbox.mode = "policy"` is the default. `strict` requires a real OS backend
and fails closed if it is unavailable.

| Platform | Backend | Main controls |
|---|---|---|
| Windows | AppContainer | workspace grants, child-process Job Object, loopback network isolation |
| Linux | bubblewrap | read-only host, writable roots, user/PID/IPC/UTS and optional network namespaces |
| macOS | Seatbelt | deny-by-default profile, granted roots, network policy |

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
session/events; it must not copy the agent loop, policy or tool execution.
The GUI currently has a static Avalonia shell only.

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

- Full GUI integration and GUI release packaging.
- Windows release bundle must include `kl-sandbox-runner.exe`; packaged
  `kl serve` smoke coverage is still pending.
- Real provider/MCP live smoke with credentials.
- Provider prompt caching.
- Session rewind/revert and MCP resources/OAuth.
- Per-session MCP workspace.
- Repository split.