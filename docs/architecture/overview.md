# KL architecture (MVP)

```text
C# terminal client (kl)
  command parsing, streaming renderer, diff, approval prompt
        |
        | JSON-RPC 2.0 over stdio (newline-delimited)
        v
Rust trusted core (kl-core)
  Emitter + JSON-RPC loop
  ProviderRouter (OpenAI-compatible Chat, Anthropic, scripted)
  Agent loop (provider-neutral)
  ToolRegistry (read/write/edit/grep/glob/shell/git)
  PolicyEngine (path/command/git/approval)
  JsonlAudit
  SessionStore (session.json, state.json, events.jsonl)
```

## Request lifecycle

1. The client launches `kl-core` and sends `kl.handshake` with its supported
   protocol versions.
2. The core picks the highest common version and rejects incompatible clients
   with `-32001`.
3. `session.create` validates the workspace, resolves the provider and model,
   creates a session directory and emits `session.created`.
4. `turn.start` returns a `turn_id` immediately and runs the agent on a worker
   thread so the request loop can still accept `turn.cancel` and
   `tool.approval.respond`.
5. Every agent event is appended to `events.jsonl` and sent as an `event`
   notification with an increasing per-session `seq`.
6. Tool calls require an `allow`, `ask` or `deny` policy decision. `ask` blocks
   the worker until the client responds or the approval timeout expires.
7. One audit record is written for every tool call, including denied and
   invalid calls. The record includes argument/output hashes, the decision,
   approval result, duration and error.
8. At the end of a turn the projected message list is written to `state.json`.

## Agent loop

The loop is explicit and observable:

```text
turn.started
  -> model call (streamed text/reasoning)
  -> tool call(s) (policy -> approval -> execute -> audit)
  -> model call again with tool results
  -> turn.completed / turn.failed / cancelled
```

The provider adapter is the only place that knows OpenAI or Anthropic wire
formats. The agent only sees `ChatRequest`, `ChatEvent` and `ChatMessage`.

## Storage

`~/.kl/` by default, overridable with `KL_HOME` or `--home`:

```text
sessions/<session-id>/session.json   metadata
sessions/<session-id>/state.json     message projection
sessions/<session-id>/events.jsonl   append-only event log
audit/events.jsonl                   append-only tool audit
```

SQLite indexing is planned for a later stage; the MVP uses JSONL and a small
JSON projection so it has no native database build dependency.

## Protocol

See `schemas/protocol/v1/` and `tests/fixtures/protocol/`. The same fixtures
are parsed by Rust (`crates/kl-protocol/tests/contract.rs`) and .NET
(`cli/Kl.Cli.Tests/ProtocolContractTests.cs`).

## Deliberate MVP gaps

- No OS-level sandbox (path, command, network and approval policy only).
- No plugin host, sidecar supervisor or WASM runtime yet.
- No multi-agent quality modes, repository symbol index or SQLite audit index.
- No UDS/Named Pipe transport; the transport boundary is ready for one.
