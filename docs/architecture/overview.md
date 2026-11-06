# KL architecture

## Process model

```text
TUI (tui/Kl.Tui)     Headless/CI          kl GUI (gui/Kl.Gui)
        \                  |                  /
         \                 |                 /
          +----------- Kl.Client -----------+
                             |
            JSON-RPC 2.0 / stdio or local socket
                             |
                      Rust Core (kl-core)
              Emitter + JSON-RPC request handling
              ProviderRouter + Agent runtime
              SessionStore + EventHub
              PolicyEngine + SandboxManager
              ToolRegistry + AuditLog
              Provider adapters / Skills / Hooks / MCP / Plugins
                             |
             Provider APIs (OpenAI-compatible, Anthropic, ...)
```

The Rust Core is the only process that executes tools, calls providers, writes
sessions or evaluates policy for protocol clients. TUI, GUI and headless clients
use `Kl.Client`. The GUI is a protocol client only: it starts or resumes
`kl-core` through `KlSessionClient` (stdio when no local server is available,
otherwise the discovered local server) and renders events; it never executes a
tool, calls a provider or evaluates policy itself.

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
| `Kl.Client` | C# JSON-RPC/session client used by the TUI and the GUI |
| `Kl.Tui` | Terminal.Gui v2 terminal client and approval presentation |
| `Kl.Gui` | Avalonia desktop client over `Kl.Client` |

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
  checked together by `scripts/check_protocol.py`.
- Shared fixtures live under `tests/fixtures/protocol`.
- The TUI and GUI consume the same session/event contract through `Kl.Client`;
  transport selection (stdio child process or discovered local server) happens
  inside `KlSessionClient`, not in client views.

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
- Reconnect re-sends `session.resume` before `session.attach`, so a client can
  recover a session from disk after the Core process restarted; `turn.start`
  also loads a persisted session on demand.
- Presenter events expose ordering and correlation fields (`Seq`, `TurnId`,
  `CallId`) plus bounded tool output. A dropped Core connection completes the
  session with an `Error` event and fails the active turn instead of hanging.
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
terminal rendering and input; the GUI adds Avalonia views and view-models. Both
reuse the same `KlSessionClient`, session/event contract and approval protocol,
and neither owns an agent loop, policy engine, sandbox or tool execution.

The GUI keeps this boundary in `gui/Kl.Gui/Services/CoreSessionService.cs`
(session lifecycle and protocol requests), `CoreSessionPresenter.cs`
(event/approval bridge), `CoreProtocolModels.cs` (protocol response parsing) and
`ViewModels/MainWindowViewModel.Core.cs` (event-to-UI mapping). The GUI settings
page shows Core runtime information read-only (version, protocol, provider and
model, API-key configured flag, Core path and KL home); provider, model and
credential changes belong to Core. Automation runs each task in a throwaway Core
session and stores only the result. A GUI window that attaches to a session
whose write lease is held elsewhere becomes a read-only observer and is rendered
as such.

## Release

Releases are built only from pushes to `main` and are tagged `v<version>-rcN`.
The runtime identifier matrix is final:

| Platform | RIDs |
|---|---|
| Windows | win-x64, win-arm64 |
| Linux | linux-x64, linux-arm64 |
| macOS | osx-arm64 |

`scripts/package_format.py` accepts only these RIDs and verifies that the
packaged binaries really match them. Adding or removing a RID requires an ADR
([`../adr/0011-release-rid-matrix.md`](../adr/0011-release-rid-matrix.md)).

## Known gaps

- GUI packaging smoke on real Windows/macOS release runners: the bundle now
  includes the GUI with `kl-core` under `gui/`, but the packaged app has not
  been launched in CI.
- Windows release bundle must include `kl-sandbox-runner.exe`; packaged
  `kl serve` smoke coverage is still pending.
- Real provider/MCP live smoke with credentials.
- Provider prompt caching.
- Session rewind/revert and MCP resources/OAuth.
- Per-session MCP workspace.
- Repository split.