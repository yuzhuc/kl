# ADR 0008: WASM tool plugin runtime

- Status: accepted
- Date: 2026-10-18

## Context

Sidecar plugins need OS-level isolation and a separate process, but many tools
only need deterministic input/output computation. The plugin boundary already
uses manifest v1, host-side argument permission checks and active-instance hot
reload. WASM should add another runtime without changing those policy controls
or exposing host capabilities to the guest.

## Decision

- Manifest v1 accepts `runtime = "wasm"` for tool plugins. `entrypoint` points
  to a Component Model binary (or WAT for local examples and tests).
- WASM tools export `health` and `execute`. Both use JSON strings at the
  component boundary.
- The host provides only `host-log` and `host-time` imports. WASI, filesystem,
  network, environment and process APIs are not linked.
- Every call uses a fresh store with a manifest-configured linear-memory cap and
  an epoch-based wall-clock timeout.
- Traps, timeouts and component/protocol failures are converted into
  `PluginError` values. They cannot unwind the host process.
- Installation and reload instantiate the new component and require a
  successful `health` response before replacing the active instance. Replaced
  instances remain alive until their in-flight counter reaches zero or the
  drain timeout expires.
- Existing host-side network/path checks run before dispatching either a
  sidecar or WASM tool. WASM providers are rejected.

## Consequences

- WASM tools are safer by default than sidecars for pure computation because
  they have no ambient capability, but they cannot access files directly.
- A timeout interrupts guest execution; it is not a CPU-fuel scheduler or a
  proof of termination.
- Each active WASM plugin owns an engine and component. Calls to one active
  instance are serialized so an epoch interrupt cannot affect another call in
  the same store.
- Sidecar behavior and its strict OS sandbox path remain unchanged.
