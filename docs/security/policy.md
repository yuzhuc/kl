# Policy and audit

## Decision model

Every tool request is evaluated as `action + resource -> effect`:

| Action | Resource | Default |
|---|---|---|
| read | resolved path | allow inside workspace |
| edit | resolved path | ask (configurable auto-approve) |
| shell | command text | ask |
| git_read | git arguments | allow for read-only subcommands |
| git_write | git arguments | ask |
| deny | dangerous command/path | always deny |

`ask` is resolved by the client. `deny` cannot be overridden with `--yes`.

## Path safety

- Paths are joined to the canonical workspace; the nearest existing ancestor
  is canonicalized, then the remaining components are appended.
- `..` components are normalized before containment checks.
- Symlinks are resolved by canonicalization; paths that leave the workspace are
  denied unless `allow_outside_workspace` is explicitly enabled.
- Sensitive names such as `.env*`, `.ssh`, `.aws`, `.gnupg`, `.git`, `id_rsa`
  and credential files require approval (or `allow_sensitive_reads` for reads).

## Command safety

The policy denies clear destructive patterns before asking for approval,
including recursive deletion of filesystem roots/homes, formatting, disk
partitioning, shutdown/reboot, force push, hard reset, encoded PowerShell
execution and download-pipe-to-shell patterns.

Shell command text is not a complete OS sandbox. A command that does not match
the deny list is still shown to the user for approval, but the MVP does not
parse every argument for out-of-workspace paths. OS sandboxing is a later
stage.

## Audit

`~/.kl/audit/events.jsonl` is append-only. Each record contains:

- session, turn and call ids;
- tool, action, resource and policy decision;
- whether approval was granted;
- ok/failure, duration and error;
- SHA-256 hashes of arguments and output.

The CLI reads the tail with `kl audit [limit]`. Tests use the same
`AuditSink` trait with an in-memory sink.

## Secrets

- Config stores only the name of the environment variable holding a key
  (`api_key_env`), never the key value.
- `config.get` returns a redacted view.
- Audit records store hashes, not raw tool arguments/output.
