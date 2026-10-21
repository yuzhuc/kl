# ADR 0012: Local server transport

## Status

Accepted. Implementation is in progress on `feat/server-attach`.

## Context

KL clients currently start one `kl-core` child process and communicate over
JSON-RPC on stdio. That is sufficient for a single client, but it cannot let a
TUI and GUI observe the same running session, share approvals or reconnect
after the first client exits.

The transport decision must be made before GUI session attach is implemented,
otherwise the GUI will encode a one-client-per-process assumption.

## Decision

KL will use a local server in addition to the existing stdio mode.

- The server is single-user, single-machine and local-only.
- Windows uses a named pipe.
- Unix and macOS use a filesystem Unix-domain socket under the KL home
  directory.
- The server writes `server.json` under the KL home directory containing the
  endpoint, process id and start time. It never writes credentials.
- Clients discover the endpoint from that file or receive an explicit
  `--socket`/endpoint option.
- The legacy stdio mode remains available for headless and compatibility use.

The server is not a remote service. It does not listen on TCP, does not support
multiple users, TLS, remote clients or cross-machine sessions.

## Session semantics

- `session.attach` replays stored session events after the requested sequence
  and subscribes the connection to subsequent events.
- A session has one writer at a time. A client acquires a lease before starting,
  cancelling or resolving an approval for a turn.
- A conflicting lease returns `SESSION_BUSY` (`-32008`).
- Missing or expired writer authority returns `LEASE_REQUIRED` (`-32009`).
- A disconnected client releases its leases automatically.
- Lease lifetime is bounded; renewal or reconnect is explicit.
- `session.detach` releases the connection's subscription and lease.

Protocol additions:

```text
server.info
session.attach
session.detach
session.lease
```

## Security

- The endpoint is local and contains no remote authentication surface.
- The server is one shared core instance; clients still cannot execute tools
  directly.
- All tool calls, approvals, policy and audit continue to go through the core.
- The server must not expose session data to a remote interface.

## Consequences

- GUI and TUI can eventually share live events and approval state.
- The stdio client remains the fallback when no server endpoint is available.
- Multi-client concurrency, replay tests and lease expiry become required
  integration tests.
- The GUI transport swap belongs behind `sdk/Kl.Client`, not in Avalonia views.