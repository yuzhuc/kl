# ADR 0001: Rust core and C#/.NET terminal client

- Status: accepted
- Date: 2026-10-17

## Context

KL needs one trusted execution core that owns agent state, tools, policy and
audit, plus a terminal client that is good at interactive rendering and
approval workflows. The project must not depend on a Python runtime or Python
build scripts.

## Decision

- The Rust workspace under `crates/` is the only execution core.
- The `kl-core` binary speaks JSON-RPC 2.0 over stdio.
- The C#/.NET project under `tui/Kl.Tui` is a thin terminal client. It starts
  `kl-core`, performs the version handshake, streams events and sends approval
  decisions. It never calls a model or executes a tool directly.
- The shared wire types live in `crates/kl-protocol`; both Rust and .NET tests
  consume the same fixtures under `tests/fixtures/protocol`.

## Consequences

- Tool and policy changes are centralized in the trusted core.
- The CLI can later be replaced by another client without changing the core.
- Packaging must ship `kl-core` next to the CLI; `kl doctor` diagnoses a missing
  or incompatible binary.
- A protocol version handshake is mandatory, so the two sides can evolve
  independently.
