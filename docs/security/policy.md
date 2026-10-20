# Policy, sandbox and audit

## Decision model

Every tool request is evaluated as `action + resource -> effect`:

| Action | Resource | Default |
|---|---|---|
| read | resolved path | allow inside workspace |
| edit | resolved path | ask (configurable auto-approve) |
| shell | command text | ask |
| git_read | git arguments | allow for read-only subcommands |
| git_write | git arguments | ask |
| plugin | plugin id | ask (configurable auto-approve) |
| deny | dangerous command/path | always deny |

`ask` is resolved by the client. `deny` cannot be overridden with `--yes`.
The client can answer `allow`, `allow_session` or `deny`; `allow_session`
persists a tool+resource rule in session state so the same request is
auto-allowed after reload.

## Stage-scoped tool access

Deep review stages do not rely on prompts for isolation:

| Stage | Access | Allowed tools/actions |
|---|---|---|
| explorer, planner, implementer | `Full` | normal policy and approvals |
| reviewer | `ReadOnly` | read/grep/glob, read-only git |
| verifier | `VerifyOnly` | read/grep/glob, read-only git, bounded verification shell |

`ReadOnly` denies edit/write/patch, all shell, git writes and plugin calls
before the tool executes. `VerifyOnly` additionally allows only recognized
verification commands such as `cargo test`, `cargo check`, `cargo clippy`,
`cargo build`, `dotnet test`, `dotnet build`, `cargo fmt --all -- --check`, and
read-only git inspection. Shell metacharacters and git output/ext-diff options
are denied. The verifier cannot use git commit/push/reset or arbitrary shell
redirection. A workspace fingerprint is still checked after review as a second
defense against provider or future-tool bypasses.

## Path safety

- Paths are joined to the canonical workspace; the nearest existing ancestor
  is canonicalized, then the remaining components are appended.
- `..` components are normalized before containment checks.
- Symlinks are resolved by canonicalization; paths that leave the workspace are
  denied unless `allow_outside_workspace` is explicitly enabled.
- Sensitive names such as `.env*`, `.ssh`, `.aws`, `.gnupg`, `.git`, `id_rsa`
  and credential files require approval (or `allow_sensitive_reads` for reads).

## Command, network and environment policy

The policy denies clear destructive patterns before asking for approval,
including recursive deletion of filesystem roots/homes, formatting, disk
partitioning, shutdown/reboot, force push, hard reset, encoded PowerShell
execution and download-pipe-to-shell patterns.

`policy.network_allowlist` checks URLs and SSH/Git hosts found in shell command
text. A host outside the list is denied before execution. An empty list keeps
the previous behavior: the shell still requires approval, but URLs are not
checked.

`policy.env_allowlist` switches shell/git to a clean child environment. Safe
platform variables such as `PATH`, `SystemRoot`, `ComSpec`, `TEMP`, `HOME` and
the listed variables are passed; all others are removed. An empty list inherits
the parent environment.

Policy checks are a trust boundary, not a substitute for OS isolation.

## Sandbox backend status

`sandbox.mode = "policy"` is the default and uses the policy backend. `strict`
requires a real OS backend and fails closed if the selected backend is
unavailable.

On Windows, the `appcontainer` backend launches shell, git and sidecar
processes with an AppContainer token and a kill-on-close Job Object. A
`kl-sandbox-runner` helper clears the inherited environment before launching
the target. Workspace roots are granted explicitly; sensitive paths are
protected by removing the AppContainer ACE and setting a protected DACL.
Loopback network isolation is verified by a probe run both outside and inside
AppContainer; the policy network allowlist remains active. The runner binary
must be shipped next to `kl-core`.

Linux strict mode uses `bubblewrap` with a read-only host, rebound writable
roots, fresh user/PID/IPC/UTS namespaces and an isolated network namespace when
the policy denies network. macOS strict mode uses a deny-by-default Seatbelt
profile through `/usr/bin/sandbox-exec`. MCP server processes and shell
`tty=true` commands also start through the active sandbox; AppContainer PTY is
rejected rather than running unsandboxed.

The MXC probe detects the external `MXC launcher: enumerate MXC volume Z:`
failure. In non-strict mode KL reports an explicit policy-only fallback; in
strict mode a selected MXC backend fails closed. `kl doctor` exposes
`sandbox_backend`, `sandbox_strict`, `sandbox_probe`, `sandbox_capabilities`,
`sandbox_mxc` and `sandbox_z_isolation`.

Every policy or OS sandbox denial emits a persisted `sandbox.violation` event
and a normal failing audit record. Sensitive-path ACL hardening changes the
DACL of the affected path. If the git installation root cannot be granted to
the AppContainer SID, git fails closed with an explicit sandbox error.

## Plugin permissions

Sidecar manifests declare:

- `network`: allowed hosts for plugin tool URL arguments;
- `env`: variables copied into the plugin process;
- `fs_read` / `fs_write`: `workspace` or workspace-relative path prefixes.

The host rejects unauthorized network/path requests before invoking the
plugin. Plugin processes get a clean environment plus safe variables and the
granted variables. The per-instance capability token is checked at handshake
and on every request.

## Audit

`~/.kl/audit/events.jsonl` is the raw append-only JSONL log. It rotates at
`audit.rotate_bytes` and keeps `audit.keep_files` archives. SQLite
`~/.kl/audit/audit.sqlite` is the query index used by `kl audit`; the raw
rotated JSONL files remain the source of truth. `kl review` reads the audit tail
separately and surfaces denied tool calls and sandbox violations as review
findings; it does not replace or rewrite runtime audit records. The optional
review-model call is bounded by `review_timeout_secs`; cancellation and timeout
produce an `llm-review-unavailable` finding and never block indefinitely.

Each record contains:

- session, turn and call ids;
- tool, action, resource and policy decision;
- whether approval was granted;
- ok/failure, duration and error;
- SHA-256 hashes of arguments and output.

## Secrets

- Provider keys live in `~/.kl/credentials/<id>.key`. Provider TOML may set
  `api_key_env` as an override; new write commands never place a key in
  provider TOML. `config.get` returns a redacted view and never emits the key
  value; audit records store hashes rather than raw tool arguments/output.
- Plugin environment variables are allowlisted in the manifest.
