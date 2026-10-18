# KL

KL is a provider-neutral coding agent with a Rust trusted core and a C#/.NET
terminal client. It does not use a Python runtime or Python build scripts.

Status: **stage 0, stage 1 MVP, stage 1.5 hardening, stage 2 sidecar
plugin-host MVP, provider routing, terminal chat page, provider configuration
commands, stage 2.5 Windows AppContainer sandbox + provider cancellation
cleanup, and stage 3 automated review + quality modes complete**. Live
external Provider smoke is pending until API keys are supplied; see
`tests/fixtures/providers/live-smoke.status.json`.

## Architecture

```text
kl (C#/.NET)  -- JSON-RPC 2.0 over stdio -->  kl-core (Rust)
                                              agent loop
                                              provider router
                                              tools + policy + approvals
                                              sessions + audit
                                              review pipeline
                                              quality-mode stages
```

- `crates/kl-protocol` — wire envelopes, version negotiation, events, stable
  error codes.
- `crates/kl-core` — stdio server, config, sessions, approvals, provider
  assembly.
- `crates/kl-agent` — provider-neutral agent loop.
- `crates/kl-provider` — OpenAI Chat Completions, Anthropic Messages, OpenAI
  Responses and a deterministic scripted provider for offline tests/demos.
- `crates/kl-tools` — `read_file`, `write_file`, `edit_file`, `grep`, `glob`,
  `shell`, `git`.
- `crates/kl-review` — deterministic diff context, local/security rules,
  secret and dependency scans, runtime-audit findings, optional LLM review,
  deduplication and confidence thresholds.
- `crates/kl-policy` — workspace containment, sensitive paths, dangerous
  command denial, approval effects.
- `crates/kl-audit` — rotated append-only JSONL audit plus SQLite query index.
- `crates/kl-store` — session metadata, message projection and event log.
- `crates/kl-sandbox` — unified sandbox boundary with Windows AppContainer +
  kill-on-close Job Object, strict-mode fail-closed, MXC probing and the
  `kl-sandbox-runner` clean-environment launcher.
- `crates/kl-plugin-host` — manifest v1, sidecar JSON-RPC supervisor, health
  checks, drain, hot swap and capability checks.
- `crates/kl-exec` — process execution with timeout, cancellation and bounded
  output.
- `cli/Kl.Cli` — command parsing, process lifecycle, streaming UI, diff and
  approval prompts.
- `schemas/protocol/v1` and `tests/fixtures/protocol` — shared contract.

## Prerequisites

- Rust 1.85+ (edition 2024; tested with 1.99)
- .NET SDK 10
- Node.js 22+ (for JS build scripts)
- Git
- No Python is required or used.

## Build and test

Cross-platform JavaScript:

```sh
node scripts/build.mjs
```

PowerShell wrapper:

```powershell
pwsh scripts/build.ps1
```

Manual:

```powershell
cargo fmt --all -- --check
cargo clippy --workspace --all-targets -- -D warnings
cargo test --workspace
cargo build --workspace
dotnet build kl.sln
dotnet test kl.sln --no-build
```

`dotnet test` includes a live test that launches `target/debug/kl-core.exe`, so
build the Rust core first.

## Run

```powershell
# handshake + core/environment diagnostics
.\cli\Kl.Cli\bin\Debug\net10.0\kl.exe doctor

# one task in the current workspace
.\cli\Kl.Cli\bin\Debug\net10.0\kl.exe run "fix the failing test" --workspace . --yes

# interactive session
.\cli\Kl.Cli\bin\Debug\net10.0\kl.exe chat --workspace . --yes

# list/resume sessions
.\cli\Kl.Cli\bin\Debug\net10.0\kl.exe resume
.\cli\Kl.Cli\bin\Debug\net10.0\kl.exe resume <session-id> --yes

# review the current diff; strict shows low-confidence findings
.\cli\Kl.Cli\bin\Debug\net10.0\kl.exe review --workspace . --strict

# deep quality mode
.\cli\Kl.Cli\bin\Debug\net10.0\kl.exe run "fix the failing test" --quality deep --workspace . --yes

# providers, audit, redacted config
.\cli\Kl.Cli\bin\Debug\net10.0\kl.exe provider list
.\cli\Kl.Cli\bin\Debug\net10.0\kl.exe audit 20
.\cli\Kl.Cli\bin\Debug\net10.0\kl.exe config
```

The CLI locates `kl-core` next to itself, via `KL_CORE_PATH`, or by walking up
to `target/debug` / `target/release`.

## Terminal chat page

`kl` and `kl chat` open a full-screen chat page when stdin and stdout are
interactive. The page keeps user/assistant messages, tool calls, approvals,
diffs and errors in a scrollable transcript. Enter sends, Ctrl+C cancels the
current turn (or exits when idle), Left on an empty input opens history, Up/Down
scrolls, and `?` shows shortcuts. The history page groups sessions by workspace
and supports Up/Down, Enter to resume, `n` for a new session and Esc to return.
When a session is resumed, directly or from the history page, the chat page
replays the most recent visible user, assistant and tool summaries before
accepting input. Live turn events continue from the new turn and are not
replayed twice.

If no provider is configured, `kl` still opens the chat page and shows a hint to use `/settings`. After a provider and default model are saved, the chat page creates a session automatically without restarting.

From the chat input, enter `/settings` or `/设置` to open the settings page.
The page uses a two-column layout: Model, Agent, Plugins and Skills on the
left, and the selected group on the right. Model and Plugins are implemented;
Agent and Skills are placeholders. Use Up/Down to move, Tab/Shift+Tab to switch
columns, Enter to edit or confirm, Esc to return, and Ctrl+S to save.

The Model group lists providers with enabled, key, model-count and protocol
status, plus model metadata. It can add or edit providers, toggle a provider
with Space, add or edit models, and set the default provider/model with Enter
on a model row. Base address mode expands the default
`https://opencode.ai/zen/go/v1` (or the entered base) into
`/chat/completions`, `/messages` and `/responses` before writing the provider
TOML. Full mode writes the three complete URLs exactly as entered. API keys use
masked input and `provider.set_api_key` to write
`credentials/<id>.key`; they are never written to provider TOML or echoed.
Disabled providers hide their models until they are enabled again.

The Plugins group reads `plugin.list` and shows each plugin's id, version, kind,
enabled state, health and detail. Selecting a plugin shows its basic metadata
and the most recent host-provided error detail; raw manifest parameters and
credentials are not rendered. Use `e` to enable, `d` to disable, `r` to reload,
or Space to toggle, then press the same key again or Enter to confirm. Esc
cancels a pending confirmation. The list is reloaded after every successful
action, so a disabled plugin immediately shows its new state. Installation stays
in the existing command: `kl plugin install <path>`.

Redirected or non-interactive runs automatically fall back to the plain-text
prompt. `/settings` in that mode prints a hint instead of being sent to the
model. `kl run` always uses the existing non-interactive output.

## Configuration

Default path: `~/.kl/config.toml` (override with `--config` or `KL_CONFIG`).
Environment overrides: `KL_HOME` (sessions/audit) and `KL_CORE_PATH`.

The global file selects the default provider/model and switches each provider
on or off. Each enabled provider is loaded from its own file; a disabled
provider file is never read.

`~/.kl/config.toml`:

```toml
[agent]
provider = "opencode"
model = "gpt-5-responses"
quality = "balanced"          # fast | balanced | deep
max_iterations = 12
max_tool_output_bytes = 32768
command_timeout_secs = 120
approval_timeout_secs = 180
deep_max_stages = 5
deep_max_tool_calls = 48
deep_token_budget = 120000
deep_timeout_secs = 900

# Optional role-specific overrides for deep mode. Unset values inherit the
# default provider/model; a failed override falls back to the default route.
[agent.roles.explorer]
provider = "opencode"
model = "gpt-5-responses"

[agent.roles.reviewer]
provider = "opencode"
model = "gpt-5-responses"

[policy]
auto_approve_edits = false
auto_approve_shell = false
auto_approve_plugins = false
allow_outside_workspace = false
allow_sensitive_reads = false
network_allowlist = []       # empty = no shell URL restriction
env_allowlist = []           # empty = inherit shell environment

[sandbox]
mode = "policy"              # policy | strict | off
backend = "auto"             # auto | appcontainer | mxc | policy | off
probe_timeout_ms = 1500

[audit]
rotate_bytes = 10485760
keep_files = 5

[providers.opencode]
enabled = true
path = "providers/opencode.toml"

[providers.disabled-example]
enabled = false
path = "providers/does-not-matter.toml"
```

`~/.kl/providers/opencode.toml`:

```toml
# Optional environment override. The normal key location is
# ~/.kl/credentials/<id>.key; provider TOML never stores the key.
api_key_env = "OPENCODE_API_KEY"

[protocols]
openai-chat = "https://gateway.example/v1/chat/completions"
anthropic = "https://gateway.example/v1/messages"
openai-responses = "https://gateway.example/v1/responses"

[[models]]
id = "gpt-5-chat"
protocol = "openai-chat"
context_window = 128000
compact_threshold = 0.8
max_output_tokens = 4096

[[models]]
id = "claude-native"
protocol = "anthropic"
context_window = 200000
compact_threshold = 0.75
max_output_tokens = 8192

[[models]]
id = "gpt-5-responses"
protocol = "openai-responses"
context_window = 256000
compact_threshold = 0.9
max_output_tokens = 16384
url = "https://gateway.example/custom/responses" # optional exact override
```

Keys live in `~/.kl/credentials/<id>.key`. `api_key_env` remains an optional
environment override; when set and present it wins over the credential file.
New configuration commands never write a key into provider TOML. Legacy inline
`api_key` values are still read for compatibility and are always redacted.

Configure once, then start with `kl chat`:

```powershell
.\\cli\\Kl.Cli\\bin\\Debug\\net10.0\\kl.exe provider add
.\\cli\\Kl.Cli\\bin\\Debug\\net10.0\\kl.exe auth set <provider>
.\\cli\\Kl.Cli\\bin\\Debug\\net10.0\\kl.exe provider enable <id>
.\\cli\\Kl.Cli\\bin\\Debug\\net10.0\\kl.exe provider disable <id>
.\\cli\\Kl.Cli\\bin\\Debug\\net10.0\\kl.exe model list
.\\cli\\Kl.Cli\\bin\\Debug\\net10.0\\kl.exe model select
```

Provider and model writes are performed by the Rust core. Unknown TOML fields
are preserved, files are replaced atomically, and failed writes do not leave
half-written configuration. `provider.list` shows `enabled`, `key_configured`,
availability and the complete URL. A disabled provider contributes no models;
selecting it or using it as the disabled default returns a clear error rather
than silently switching.

Each model is bound to one native protocol adapter. The configured URL (model
override or `[protocols]` entry) is used exactly as the complete request URL.
KL does not probe protocols, convert between OpenAI Chat, Anthropic and
OpenAI Responses, or append fixed path prefixes. `--provider` and `--model`
select exactly one route.

`context_window`, `compact_threshold` (0.0-1.0) and `max_output_tokens` are
validated and passed to the agent. `config.get`, logs and audit records never
include the key value.

For offline tests and demos, the deterministic scripted provider is enabled
only through the agent settings:

```toml
[agent]
provider = "scripted"
model = "scripted"
scripted_path = "script.json"
scripted_sequential = true      # keep the script cursor across deep stages
```

`policy.env_allowlist` switches shell/git execution to a clean environment plus
only the listed variables. `policy.network_allowlist` validates URLs found in
shell commands and denies hosts outside the list. Empty lists preserve the
previous inherit/no-URL-check behavior.

`[sandbox]` controls process isolation. `policy` uses the policy engine only.
`strict` requires a real OS backend and fails closed if it is unavailable. On
Windows, `backend = "appcontainer"` (or `auto` in strict mode) runs shell, git
and sidecar processes through an AppContainer token and a kill-on-close Job
Object. A `kl-sandbox-runner` helper clears the inherited environment before
starting the target process, so plugin environment allowlists remain effective.
MXC is probed explicitly; an unavailable launcher is reported and never
silently treated as an OS sandbox. `kl doctor` shows the backend, strict mode,
probe result, capability matrix and MXC status.

`[audit]` controls raw JSONL rotation. `kl audit` queries the SQLite index while
the rotated `events*.jsonl` files remain the source of truth.

## Automated review

`kl review` is separate from `kl audit`. `kl audit` remains the runtime tool
audit log. `kl review` inspects code changes and supports:

```powershell
kl review                         # standard review of the working-tree diff
kl review --base origin/main     # review changes since a git ref
kl review --strict               # keep low-confidence findings
kl review --mode quick           # quick | standard | strict
```

The pipeline collects `git diff`, scans dangerous patterns, secrets, wildcard
dependencies and missing tests, performs cross-file risk checks, proposes
Rust/.NET verification commands, converts runtime audit denies and sandbox
violations into findings, and optionally calls the configured provider for a
structured diff review. Findings are deduplicated and sorted by severity.
`--strict` never silently hides low-confidence items; they are marked
`LOW-CONFIDENCE`. The deterministic fixture
`tests/fixtures/review-repo` is used by Rust tests and the CLI demo:

```powershell
pwsh scripts/demo-review.ps1
```

## Quality modes

`fast`, `balanced` and `deep` are selected with `--quality` or
`[agent].quality`. The default is `balanced`, which preserves the existing
single-turn behavior. `deep` runs explicit ordinary agent turns:

```
explorer -> planner -> implementer -> reviewer -> verifier
```

All stages share session history and use the existing `ProviderRouter`; there
is no new provider protocol or cross-protocol conversion. Each stage inherits
the default provider/model, with optional `[agent.roles.<stage>]` overrides.
Deep mode is bounded by stage count, total tool calls, token usage reported by
the provider, and a wall-clock watchdog. Cancellation is honored between and
during stages. Explorer/planner/reviewer/verifier failures are recorded and do
not discard a successful implementation; implementer failure stops the turn.
Reviewer and verifier prompts require file:line evidence, and the verifier is
instructed to verify only and not expand the change. The deep turn also
fingerprints the workspace before review and fails if reviewer/verifier changes
it.

## Offline end-to-end demo

This proves a real read → edit → test loop without network or API keys:

```powershell
pwsh scripts/demo-e2e.ps1
```

It copies `examples/e2e-demo-template` to `target/e2e-demo`, runs the scripted
provider through the real core and CLI, verifies that `src/lib.rs` changed and
that `cargo test` exited 0, then prints audit records.

The policy demo shows deny behavior:

```powershell
pwsh scripts/demo-policy.ps1
```

It asks for `rm -rf /` and a path outside the workspace; both are denied even
with `--yes`, and both produce audit records with `decision: "deny"`.

The sidecar plugin demo installs v1, executes it, hot-upgrades to v2, executes
again, lists plugins and prints the audit tail:

```powershell
pwsh scripts/demo-plugin.ps1
```

Live Provider smoke (requires real keys):

```powershell
pwsh scripts/live-provider-smoke.ps1
```

The script exercises the three native protocols (`openai-chat`, `anthropic`
and `openai-responses`). Without keys it writes
`tests/fixtures/providers/live-smoke.status.json` as `pending`; it never
fabricates a live success fixture.

## Packaging

```powershell
pwsh scripts/package.ps1 -Runtime win-x64
```

This publishes the CLI framework-dependently and copies the release `kl-core`
and schemas next to it into `dist/<rid>/`. See `docs/adr/0004-packaging.md`.

## Sidecar plugins

Build the fake sidecar and install an example:

```powershell
cargo build -p kl-plugin-host
kl plugin install plugins/examples/fake-tool --home D:\kl-home
kl plugin list --home D:\kl-home
```

Plugin manifests are v1 sidecar manifests. The host verifies the capability
token, health-checks a new process before traffic switching, drains in-flight
requests, and keeps the old version on health/startup failure. The new process
is isolated from the core process and only inherits explicitly granted
environment variables. See `docs/plugin-api/README.md` and
`schemas/plugin/manifest-v1.schema.json`.

Protocol methods: `plugin.list`, `plugin.install`, `plugin.reload`,
`plugin.enable`, `plugin.disable`.

## Security model

- All file and command access goes through `kl-core`.
- `read` inside the workspace is allowed; edits, shell, mutating git and
  sensitive paths ask; dangerous commands and out-of-workspace paths are
  denied.
- `--yes` only auto-approves `ask` decisions; it cannot override `deny`.
- Shell/git can be confined to an environment allowlist; shell URLs are checked
  against a network allowlist when one is configured.
- Every tool call writes one raw JSONL audit record and one SQLite index row.
- Plugin permissions are checked before tool execution; plugin processes get a
  clean environment plus only granted variables. See
  `docs/security/policy.md` for protocol-level and OS-level limitations.

## Protocol

`docs/architecture/overview.md` describes the flow. The wire contract is in
`schemas/protocol/v1/*.json`; the same fixtures are asserted by Rust contract
tests and .NET contract tests. Every envelope carries `kl_protocol: 1`, and the
handshake negotiates the supported version range.

Core methods: `kl.handshake`, `kl.ping`, `kl.doctor`, `session.create`,
`session.resume`, `session.history`, `session.list`, `turn.start`,
`turn.cancel`, `tool.approval.respond`, `provider.list`, `provider.upsert`,
`provider.set_api_key`, `provider.set_enabled`, `config.set_default_model`,
`plugin.list`, `plugin.install`, `plugin.reload`, `plugin.enable`,
`plugin.disable`, `audit.tail`, `review.run`, `config.get`. `turn.start`
accepts an optional `quality` value (`fast`, `balanced`, `deep`).

`session.history` takes `session_id` and an optional `limit` (default 20) and
returns the last N visible entries in chronological order. Each entry has
`role`, `text`, optional `timestamp`/`seq`, and an optional sanitized `tool`
summary. Raw tool arguments, tool output/diff bodies, audit internals and
provider credentials are not returned.

Events: `session.created`, `turn.started`, `turn.delta`, `turn.reasoning`,
`tool.started`, `tool.output`, `tool.approval.requested`,
`tool.approval.resolved`, `tool.completed`, `sandbox.violation`, `audit.recorded`,
`turn.completed`, `turn.failed`.

## Limitations / next stage

- Windows AppContainer enforces workspace filesystem isolation and child-process
  containment. Network isolation is not claimed as verified; the policy
  allowlist remains active. Linux and macOS expose an explicit unimplemented
  backend rather than pretending policy checks are OS isolation.
- Protecting sensitive paths on Windows removes the AppContainer ACE and makes
  that DACL protected. This is persistent ACL hardening of those paths.
- Strict git execution requires the git installation root to be ACL-grantable.
  If it is on a volume whose root cannot be granted, the git tool fails closed
  with an explicit sandbox error.
- WASM plugin hosting is not implemented; sidecar is the current plugin runtime.
- Deep quality mode is explicit orchestration of five ordinary agent turns, not
  a separate multi-agent runtime. Repository symbol indexing is not implemented.
- Reviewer/verifier workspace edits are detected by a fingerprint guard and fail
  the deep turn; prompts also prohibit edits. Shell/edit approvals remain the
  hard policy boundary.
- Provider cancellation uses a `CancellationToken` that interrupts the streaming
  read loop; the worker is joined within two seconds and the connection is
  dropped.
- Live OpenAI Chat, Anthropic and OpenAI Responses smoke is pending because no
  API keys were available in the implementation environment. The offline mock
  SSE conformance tests pass for all three protocols.

Next stage entry point: add Linux/macOS OS backends, WASM plugin runtime,
verified network isolation, repository symbol indexing, and live provider
conformance/replay fixtures.
