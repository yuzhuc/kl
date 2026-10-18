# KL architecture

```text
C# terminal client (kl)
  command parsing, streaming renderer, diff, approval prompt
        |
        | JSON-RPC 2.0 over stdio (newline-delimited)
        v
Rust trusted core (kl-core)
  Emitter + JSON-RPC loop
  ProviderRouter (per-model: OpenAI Chat, Anthropic, OpenAI Responses;
                   scripted and sidecar plugins for offline/plugin routes)
  Agent loop (provider-neutral)
  ToolRegistry (built-ins + sidecar tools)
  PolicyEngine (path/command/git/network/env/plugin/approval)
  SandboxManager (policy/AppContainer backends, strict probing, MXC status)
  PluginHost (manifest v1, sidecar supervisor, health, drain, hot swap)
  AuditLog (rotated JSONL + SQLite index)
  SessionStore (session.json, state.json, events.jsonl)
  ReviewPipeline (git diff, local/security rules, optional LLM review)
  QualityModes (fast/balanced; deep: explorer -> planner -> implementer ->
                reviewer -> verifier)
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
5. Provider streaming runs on a detached worker; the agent polls it every
   100 ms. `turn.cancel` returns the turn promptly, and the provider sees the
   cancellation flag at its next stream event/read.
6. Every agent event is appended to `events.jsonl` and sent as an `event`
   notification with an increasing per-session `seq`.
7. Tool calls require an `allow`, `ask` or `deny` policy decision. `ask` blocks
   the worker until the client responds or the approval timeout expires.
8. One audit record is written for every tool call, including denied and
   invalid calls. AuditLog appends raw JSONL and inserts a SQLite index row.
9. At the end of a turn the projected message list is written to `state.json`.
10. `review.run` is a separate synchronous method: it collects the diff,
    runs deterministic rules and optional model review, converts runtime audit
    denies/sandbox violations into findings, then applies the selected
    confidence threshold.

## Quality modes

`turn.start` accepts an optional `quality` value. `fast` and `balanced` are one
ordinary agent turn; `deep` runs five ordinary turns in order: explorer,
planner, implementer, reviewer, verifier. Each stage uses the existing
`ProviderRouter` and shared session history. Deep mode stops on cancellation,
stage/tool/token/time limits, and keeps stage outputs as evidence in the final
turn text. Reviewer and verifier prompts require file:line evidence; the
verifier is instructed not to modify or expand the change. A workspace
fingerprint taken before review fails the turn if reviewer or verifier changes
the workspace.

## Automated review

`kl review` is intentionally separate from `kl audit`. The review pipeline
collects `git diff` plus untracked files, scans changed lines and full files,
and merges deterministic findings with runtime audit denies/sandbox violations
and optional structured LLM findings. `quick`, `standard` and `strict` control
the confidence threshold; strict includes low-confidence findings and marks
them. The same pipeline is exercised by deterministic fixture repositories.

## Agent loop

```text
turn.started
  -> model call (streamed text/reasoning, cancellation-aware)
  -> tool call(s) (policy -> approval -> execute -> audit)
  -> model call again with tool results
  -> turn.completed / turn.failed / cancelled
```

Each configured model is bound to one protocol adapter. The adapter is the
only place that knows its native wire format: OpenAI Chat Completions,
Anthropic Messages or OpenAI Responses. The configured URL is used exactly as
provided; KL never detects or converts between protocols and never appends a
fixed path prefix. The agent only sees `ChatRequest`, `ChatEvent` and
`ChatMessage`. Sidecar providers implement the same neutral trait.

The agent loop is an explicit `TurnState::{Model, Tools}` state machine.
Completion, cancellation, provider/tool failure and the maximum-iteration limit
are terminal transitions; no provider name is inspected by the loop.

## Plugin lifecycle

```text
install/reload
  -> validate manifest v1
  -> spawn new sidecar with clean env + capability token
  -> handshake (token + API version)
  -> health check
  -> switch active pointer (adapters resolve by plugin id)
  -> drain old in-flight requests
  -> stop old process
```

A failed handshake/health keeps the old active process. Plugin tool calls are
permission-checked by the host before the sidecar is invoked.

## Storage

`~/.kl/` by default, overridable with `KL_HOME` or `--home`:

```text
config.toml                          global provider switches/defaults
providers/<id>.toml                 protocol URLs and model catalog
credentials/<id>.key                 API key material
sessions/<session-id>/session.json   metadata
sessions/<session-id>/state.json     message projection
sessions/<session-id>/events.jsonl   append-only event log
audit/events.jsonl                   active raw audit log
audit/events-*.jsonl                 rotated raw audit archives
audit/audit.sqlite                   audit query index
plugins/installed/<id>/...           sidecar versions and current pointer
```

## Protocol

See `schemas/protocol/v1/`, `schemas/plugin/manifest-v1.schema.json` and
`tests/fixtures/protocol/`. The same fixtures are parsed by Rust
(`crates/kl-protocol/tests/contract.rs`) and .NET
(`cli/Kl.Cli.Tests/ProtocolContractTests.cs`).

## Deliberate gaps

- Windows has AppContainer filesystem and child-process isolation. Network
  isolation is not verified; the policy allowlist remains active. Linux and
  macOS report an unimplemented OS backend and strict mode fails closed.
- WASM plugin runtime is not implemented; sidecar is the current runtime.
- Deep mode is five ordinary agent turns, not a separate agent runtime.
  Repository symbol indexing is not implemented.
- Reviewer/verifier workspace edits are detected by a fingerprint guard and fail
  the turn; prompts also prohibit edits. Policy approvals remain the hard
  boundary.
- No UDS/Named Pipe transport; the transport boundary is ready for one.
- Live Provider smoke remains pending until API keys are supplied.
- `kl review` can run without a provider; in that case local findings remain
  and a `llm-review-unavailable` low-confidence finding is emitted.
