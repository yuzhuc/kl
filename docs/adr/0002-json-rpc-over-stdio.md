# ADR 0002: JSON-RPC 2.0 over stdio for the MVP

- Status: accepted
- Date: 2026-10-17

## Context

The MVP needs a simple, debuggable process boundary between the TUI and the
core. A future daemon may use Unix domain sockets or Named Pipes.

## Decision

- One JSON-RPC 2.0 message per line, UTF-8 encoded.
- Requests use numeric or string ids; responses preserve the id.
- Core events are notifications with `method: "event"` and a per-session,
  monotonically increasing `seq`.
- `kl.handshake` negotiates a protocol version before session or turn methods
  are allowed. `kl.ping` and `kl.doctor` work before the handshake so a broken
  client can still diagnose the core.
- Stable error codes are defined in `kl-protocol`; transport errors are
  JSON-RPC parse/invalid-request errors.
- The transport is isolated behind the `Emitter`/client connection so a later
  UDS/Named Pipe transport does not change agent or tool code.

## Consequences

- No framing ambiguity: newline-delimited JSON is easy to test and inspect.
- Large tool output is chunked into `tool.output` events and bounded by the
  configured output limit.
- A dropped connection is not resumable in the MVP; session state is persisted
  in the session event log and can be resumed with a new connection.
