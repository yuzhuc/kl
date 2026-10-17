# KL

KL is a provider-neutral coding agent with a Rust trusted core and a C#/.NET
terminal client. It does not use a Python runtime or Python build scripts.

Status: **stage 0 complete, stage 1 MVP complete** for the scope below.

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
- `crates/kl-provider` — OpenAI-compatible Chat, Anthropic Messages and a
  deterministic scripted provider for offline tests/demos.
- `crates/kl-tools` — `read_file`, `write_file`, `edit_file`, `grep`, `glob`,
  `shell`, `git`.
- `crates/kl-policy` — workspace containment, sensitive paths, dangerous
  command denial, approval effects.
- `crates/kl-audit` — append-only JSONL audit with argument/output hashes.
- `crates/kl-store` — session metadata, message projection and event log.
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

## Configuration

Default path: `~/.kl/config.toml` (override with `--config` or `KL_CONFIG`).
Environment overrides: `KL_HOME` (sessions/audit) and `KL_CORE_PATH`.

```toml
[agent]
provider = "openai"          # switch providers without code changes
model = "gpt-4o-mini"
max_iterations = 12
max_tool_output_bytes = 32768
command_timeout_secs = 120
approval_timeout_secs = 180
max_output_tokens = 4096

[policy]
auto_approve_edits = false
auto_approve_shell = false
allow_outside_workspace = false
allow_sensitive_reads = false

[[providers]]
id = "openai"
kind = "openai-chat"
base_url = "https://api.openai.com/v1"
api_key_env = "OPENAI_API_KEY"
models = ["gpt-4o-mini"]

[[providers]]
id = "anthropic"
kind = "anthropic"
base_url = "https://api.anthropic.com"
api_key_env = "ANTHROPIC_API_KEY"
models = ["claude-sonnet-4-5"]

[[providers]]
id = "scripted"
kind = "scripted"
models = ["scripted"]
script_path = "script.json"   # relative to the config file
```

Keys are read from the named environment variable and are never written to
config, logs, audit records or `config.get`.

`openai-chat` means any OpenAI-compatible `/chat/completions` endpoint (OpenAI,
DeepSeek, vLLM, LM Studio, a company gateway). `anthropic` implements the
Messages streaming API.

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

## Packaging

```powershell
pwsh scripts/package.ps1 -Runtime win-x64
```

This publishes the CLI framework-dependently and copies the release `kl-core`
and schemas next to it into `dist/<rid>/`. See `docs/adr/0004-packaging.md`.

## Security model

- All file and command access goes through `kl-core`.
- `read` inside the workspace is allowed; edits, shell, mutating git and
  sensitive paths ask; dangerous commands and out-of-workspace paths are
  denied.
- `--yes` only auto-approves `ask` decisions; it cannot override `deny`.
- Every tool call writes exactly one JSONL audit record, including denied and
  invalid calls.
- See `docs/security/policy.md` for path resolution rules and residual risks.

## Protocol

`docs/architecture/overview.md` describes the flow. The wire contract is in
`schemas/protocol/v1/*.json`; the same fixtures are asserted by Rust contract
tests and .NET contract tests. Every envelope carries `kl_protocol: 1`, and the
handshake negotiates the supported version range.

Core methods: `kl.handshake`, `kl.ping`, `kl.doctor`, `session.create`,
`session.resume`, `session.list`, `turn.start`, `turn.cancel`,
`tool.approval.respond`, `provider.list`, `audit.tail`, `config.get`.

Events: `session.created`, `turn.started`, `turn.delta`, `turn.reasoning`,
`tool.started`, `tool.output`, `tool.approval.requested`,
`tool.approval.resolved`, `tool.completed`, `audit.recorded`,
`turn.completed`, `turn.failed`.

## Limitations / next stage

- No OS-level sandbox yet; policy is path/command/approval based.
- No sidecar/WASM plugin host yet. The interfaces and ADR are in place.
- No multi-agent quality modes, symbol index or SQLite audit index.
- Cancellation is checked between model calls and kills running shell tools;
  an in-flight HTTP stream completes or times out before the turn stops.
- Provider conformance uses recorded/unit fixtures; live provider calls need
  the corresponding API key and network access.

Next stage entry point: implement the sidecar plugin host, manifest
verification, health checks, traffic switching and the external provider/tool
examples described in `docs/plugin-api/README.md`.
