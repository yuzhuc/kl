# KL

KL is a provider-neutral coding agent with a Rust trusted core and a C#/.NET
terminal client. It does not use a Python runtime or Python build scripts.

Status: **stage 0, stage 1 MVP, stage 1.5 hardening, stage 2 sidecar
plugin-host MVP, provider routing, terminal chat page, and provider
configuration commands complete**. Live external Provider smoke is pending
until API keys are supplied; see `tests/fixtures/providers/live-smoke.status.json`.

## Architecture

```text
kl (C#/.NET)  -- JSON-RPC 2.0 over stdio -->  kl-core (Rust)
                                              agent loop
                                              provider router
                                              tools + policy + approvals
                                              sessions + audit
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
- `crates/kl-policy` — workspace containment, sensitive paths, dangerous
  command denial, approval effects.
- `crates/kl-audit` — rotated append-only JSONL audit plus SQLite query index.
- `crates/kl-store` — session metadata, message projection and event log.
- `crates/kl-sandbox` — sandbox backend probing, strict-mode fail-closed and
  MXC Z: failure isolation.
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
- Git
- No Python is required or used.

## Build and test

PowerShell:

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

Redirected or non-interactive runs automatically fall back to the plain-text
prompt. `kl run` always uses the existing non-interactive output.

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
max_iterations = 12
max_tool_output_bytes = 32768
command_timeout_secs = 120
approval_timeout_secs = 180

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
backend = "auto"             # auto | mxc | policy | off
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
```

`policy.env_allowlist` switches shell/git execution to a clean environment plus
only the listed variables. `policy.network_allowlist` validates URLs found in
shell commands and denies hosts outside the list. Empty lists preserve the
previous inherit/no-URL-check behavior.

`[sandbox]` controls backend probing. `strict` fails closed if no real OS
sandbox is available. A detected MXC `Z:` enumeration failure is isolated: in
non-strict mode KL falls back to policy-only; in strict mode shell/plugin
process execution is denied. `kl doctor` shows `sandbox_backend`,
`sandbox_strict` and `sandbox_z_isolation`.

`[audit]` controls raw JSONL rotation. `kl audit` queries the SQLite index while
the rotated `events*.jsonl` files remain the source of truth.

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
`session.resume`, `session.list`, `turn.start`, `turn.cancel`,
`tool.approval.respond`, `provider.list`, `provider.upsert`,\n`provider.set_api_key`, `provider.set_enabled`, `config.set_default_model`,\n`plugin.list`, `plugin.install`,
`plugin.reload`, `plugin.enable`, `plugin.disable`, `audit.tail`, `config.get`.

Events: `session.created`, `turn.started`, `turn.delta`, `turn.reasoning`,
`tool.started`, `tool.output`, `tool.approval.requested`,
`tool.approval.resolved`, `tool.completed`, `audit.recorded`,
`turn.completed`, `turn.failed`.

## Limitations / next stage

- No OS-level file/network sandbox yet; sandbox probing and strict fail-closed
  behavior are implemented, but enforcement remains policy/protocol based.
- WASM plugin hosting is not implemented; sidecar is the current plugin runtime.
- No multi-agent quality modes or repository symbol index.
- Cancellation returns the turn promptly and detaches the provider stream; a
  stalled network read may remain in its worker until the provider timeout.
- Live OpenAI Chat, Anthropic and OpenAI Responses smoke is pending because no
  API keys were available in the implementation environment. The offline mock
  SSE conformance tests pass for all three protocols.

Next stage entry point: add WASM plugin runtime, stronger OS sandbox backends,
and live provider conformance/replay fixtures.
