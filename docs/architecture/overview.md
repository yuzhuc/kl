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
  AgentPreset (build/plan/explore/review/verify/deep)
  SkillCatalog + SkillTool
  McpClient + MCP tools
  HookRunner + HookedTool
  SubagentTool (read-only, sequential)
  ToolRegistry (built-ins + sidecar/WASM + MCP + worktree)
  PolicyEngine (path/command/git/network/env/plugin/approval)
  SandboxManager (policy/AppContainer backends, strict probing, MXC status)
  PluginHost (manifest v1, sidecar supervisor, health, drain, hot swap)
  AuditLog (rotated JSONL + SQLite index)
  SessionStore (session.json, state.json, events.jsonl)
  ReviewPipeline (git diff, local/security rules, optional LLM review)
  QualityModes (fast/balanced; deep: explorer -> planner -> implementer ->
                reviewer -> verifier)
  RepoIndex (gitignore-aware files, shallow symbols, diff query, test selection)
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

## Agent presets

`turn.start` accepts an optional `agent` value. `build`, `plan`, `explore`,
`review`, `verify` and `deep` map to preset tool access. `plan`, `explore` and
`review` are read-only; `verify` is verify-only; `deep` keeps the five-stage
quality pipeline. A global read-only/verify-only policy cannot be escalated by
a preset.

## Skills

Skills are discovered from `~/.kl/skills/<name>/SKILL.md` and
`<workspace>/.kl/skills/<name>/SKILL.md`. Only name and description enter the
prompt; the `skill` tool loads the body on demand. Skill bodies are untrusted
context and never execute commands automatically. Home skills remain subject
to the outside-workspace read policy.

## MCP

`~/.kl/mcp.toml` configures stdio MCP servers. KL performs `initialize`,
`tools/list` and `tools/call`, registers discovered tools as
`mcp__<server>__<tool>`, and routes them through Policy, Approval, Audit and
the ToolPipeline. Startup failures are isolated per server.

## Hooks

`~/.kl/hooks.toml` contains trusted local shell hooks for `pre_tool` and
`post_tool`. Hooks can deny or fail a call but cannot grant permissions.
Hook output is bounded and timed out; hooks are trusted configuration, not a
sandbox boundary.

## Subagent and worktree

The read-only `subagent` tool runs a sequential child agent with a copied tool
set that excludes `subagent`, preventing recursion. It uses the default
provider/model and records child tool calls in the shared audit log.

The `worktree` tool creates, lists and removes detached Git worktrees under
`.kl/worktrees/<name>`. It uses GitRead/GitWrite policy actions and the
sandboxed command runner.
## Quality modes

`turn.start` accepts an optional `quality` value. `fast` and `balanced` are one
ordinary agent turn; `deep` runs five ordinary turns in order: explorer,
planner, implementer, reviewer, verifier. Each stage uses the existing
`ProviderRouter` and shared session history. Deep mode stops on cancellation,
stage/tool/token/time limits, and keeps stage outputs as evidence in the final
turn text. Reviewer and verifier prompts require file:line evidence; the verifier is
instructed not to modify or expand the change. Reviewer stages use
`ToolAccess::ReadOnly` and verifier stages use `ToolAccess::VerifyOnly`. The
existing `PolicyEngine` denies edits, writes, shell writes, git writes and
plugins before tool execution; verifier shell is restricted to a test/build/
lint and read-only git allowlist. A workspace fingerprint taken before review
is retained as a second defense against out-of-band changes.

## Repository index

deep mode builds a lightweight repository index before the explorer stage.
kl-index discovers files through git ls-files --cached --others
--exclude-standard, so ignored directories such as target and node_modules are
excluded. It records repo-relative paths, canonical language names, line counts,
modification times, and shallow symbols for Rust, C# and TypeScript; all other
languages are file-level only. Queries support path, symbol, language, recent
modification, and git-changed filters. Changed files are mapped to
same-directory or same-module tests when possible; otherwise the selected
project test command is reported as a fallback.

The index is cached as versioned JSON under ~/.kl/cache/repo-index (or
KL_INDEX_CACHE_DIR), never in the workspace. A path/size/modification-time
manifest invalidates stale entries and rebuilds them. Cache failures do not
block deep mode. Only explorer and planner prompts receive the bounded index
summary; reviewer and verifier prompts and their read-only tool accesses are
unchanged. The index is an orientation hint, not a semantic model or LSP.

## Automated review

`kl review` is intentionally separate from `kl audit`. The review pipeline
collects `git diff` plus untracked files, scans changed lines and full files,
and merges deterministic findings with runtime audit denies/sandbox violations
and optional structured LLM findings. `quick`, `standard` and `strict` control
the confidence threshold; strict includes low-confidence findings and marks
them. The optional review-model call is bounded by `review_timeout_secs` and a
cancellation token; timeout or cancellation returns an
`llm-review-unavailable` finding instead of blocking the request. The same
pipeline is exercised by deterministic fixture repositories.

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
- WASM tool plugins are implemented; WASM providers are not.
- PTY and background job management are not implemented.
- Deep mode is five ordinary agent turns, not a separate agent runtime.
  Repository symbol indexing is not implemented.
- Reviewer/verifier edits are denied at the tool-policy boundary; a workspace
  fingerprint remains as a second defense. Policy approvals remain the hard
  boundary for `Full` stages.
- No UDS/Named Pipe transport; the transport boundary is ready for one.
- Live Provider and real MCP server smoke remain pending until credentials or
  local servers are supplied.
- The graphical desktop client is not in this repository; `cli/Kl.Client` is
  the reusable C# client boundary for it.
- `kl review` can run without a provider; in that case local findings remain
  and a `llm-review-unavailable` low-confidence finding is emitted.