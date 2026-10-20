# Policy, sandbox and audit

## Decision model

Every tool request is evaluated as `action + resource -> effect`:

| Action | Resource | Default |
|---|---|---|
| read | resolved path | allow inside workspace |
| edit | resolved path | ask |
| shell | command text | ask |
| git_read | git arguments | allow for read-only subcommands |
| git_write | git arguments | ask |
| plugin | plugin id | ask |
| deny | dangerous command/path | always deny |

`deny` cannot be overridden by auto-approval. Approvals accept `allow`,
`allow_session` or `deny`. `allow_session` stores a tool+resource rule in
session state and reloads with the session.

## Stage-scoped access

Deep stages do not rely on prompt text for isolation:

| Stage | Access | Allowed |
|---|---|---|
| explorer, planner, implementer | `Full` | normal policy and approvals |
| reviewer | `ReadOnly` | read/grep/glob and read-only git |
| verifier | `VerifyOnly` | read-only tools plus recognized test/build/lint commands |

`ReadOnly` denies edits, shell, git writes and plugin calls. `VerifyOnly` also
denies shell metacharacters and arbitrary redirection. A workspace fingerprint
remains as a second defense.

## Path safety

- Paths are resolved against the canonical workspace and must remain inside
  the policy roots unless explicitly granted.
- Sensitive paths (`.env`, keys, credentials, `.ssh`, `.aws`, `.gnupg`, `.git`)
  are denied or require approval.
- Memory entries are limited to safe `.md` names under `.kl/memory`; traversal,
  symlinked entries and Windows reserved names are rejected.

## Shell, git and network

- Shell commands pass command-pattern checks before approval.
- `policy.env_allowlist` switches to a clean environment with only listed
  variables.
- `policy.network_allowlist` rejects URLs outside the configured hosts.
- Git writes require approval; read-only inspection is recognized separately.
- Shell and git run through the active sandbox.

## Sandbox backends

`sandbox.mode = "policy"` uses the policy engine only. `strict` requires a real
OS backend and fails closed when the selected backend is unavailable.

| Platform | Backend | Main controls |
|---|---|---|
| Windows | AppContainer | workspace grants, child-process Job Object, loopback network isolation |
| Linux | bubblewrap | read-only host, writable roots, user/PID/IPC/UTS namespaces, network namespace |
| macOS | Seatbelt | deny-by-default profile, granted roots, network policy |

MCP server processes start through the active sandbox (`SandboxManager`). Shell `tty=true`
uses a PTY where the backend can be wrapped; Windows AppContainer rejects PTY
rather than running unsandboxed. Linux sandbox read isolation is policy-level:
the host is mounted read-only, not reduced to a workspace-only filesystem.

## Plugins

- Sidecar and WASM tools use manifest v1 and host-side permission checks.
- Sidecars receive a clean environment and a per-instance capability token.
- WASM receives only `host-log` and `host-time` imports.
- Integrity and optional Ed25519 signatures are checked before activation.
- External plugins are disabled by default.

## Audit

Every tool request produces a normal audit record and persisted session events.
Policy denials emit `sandbox.violation`; approvals emit
`tool.approval.requested` and `tool.approval.resolved`. Audit JSONL is the
source of truth; SQLite is a query index.

## Known limits

- Real provider/MCP live smoke still requires credentials or a local server.
- MCP servers use the Core startup workspace, not a per-session workspace.
- The final release RID list is not yet fixed.
