# Security and policy

KL runs locally and treats the Core as the trusted boundary. Clients can
request actions, but only the Core evaluates policy, obtains approvals, invokes
the sandbox and writes audit records.

## Permission modes

| Mode | Behavior |
|---|---|
| `ask` | Sensitive actions request approval |
| `plan` | Writes and shell actions are denied |
| `acceptEdits` | Edits are auto-approved; shell and plugins still ask |
| `dontAsk` | Non-denied actions proceed without an interactive prompt |
| `auto` | Uses the configured policy approvals |
| `read-only` | Only read actions are allowed |

Approval decisions are `allow`, `deny` and `allow_session`; `allow_session`
remembers the rule for the current session. Unknown or unsupported decisions
fail safely.

## Path safety

- Paths are resolved against the canonical workspace and must remain inside the
  policy roots unless explicitly granted.
- Sensitive paths (`.env`, keys, credentials, `.ssh`, `.aws`, `.gnupg`, `.git`)
  are denied or require approval.
- Memory entries are limited to safe `.md` names under `.kl/memory`; traversal,
  symlinked entries and Windows reserved names are rejected.
- Todo state is stored under `.kl/todos.json` and is bounded in size and entry
  count.

## Shell, git and network

- Shell commands pass command-pattern checks before approval.
- `policy.env_allowlist` switches to a clean environment with only listed
  variables.
- `policy.network_allowlist` rejects URLs outside the configured hosts.
- `web_fetch` additionally rejects loopback, private, link-local, multicast,
  userinfo, non-http(s) and non-standard-port destinations, and revalidates
  every redirect.
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

MCP server processes start through the active sandbox. Shell `tty=true` uses a
PTY where the backend can be wrapped; Windows AppContainer rejects PTY rather
than running unsandboxed.

## Memory and context

- Workspace memory is under `.kl/memory`; user memory is under `~/.kl/memory`.
- Memory injection is bounded in characters/tokens and recorded in audit.
- Writes and deletes use the normal policy and approval channel.
- `/memory` exposes list, read, write and delete operations through protocol
  methods; clients do not access memory files directly.
- Compaction preserves complete tool-call/result pairs and the most recent
  rounds. A model summary is bounded and falls back to mechanical trimming on
  provider failure.
- `context.compacted` records the trigger, omitted count, estimated tokens and
  summary status.

## Plugins

- Sidecar and WASM tools use manifest v1 and host-side permission checks.
- Sidecars receive a clean environment and a per-instance capability token.
- WASM receives only `host-log` and `host-time` imports.
- Integrity and optional Ed25519 signatures are checked before activation.
- External plugins are disabled by default.

## Audit

Every tool request produces an audit record and persisted session events.
Policy denials emit `sandbox.violation`; approvals emit
`tool.approval.requested` and `tool.approval.resolved`. Audit JSONL is the
source of truth; SQLite is a query index.

## Known limits

- Real provider/MCP live smoke still requires credentials or a local server.
- MCP servers use the Core startup workspace, not a per-session workspace.
- DNS rebinding protection is based on pre-request resolution; pinning the
  connection address would be a later hardening step.
- The release RID list is fixed; adding a RID requires an ADR.