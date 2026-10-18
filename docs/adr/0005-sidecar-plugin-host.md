# ADR 0005: sidecar plugin host MVP

- Status: accepted
- Date: 2026-10-17

## Context

Stage 2 needs a real plugin boundary without destabilizing the kernel or
introducing Rust's unstable dynamic ABI.

## Decision

- Sidecar processes speak newline-delimited JSON-RPC 2.0 over stdio.
- Manifest API v1 declares kind, entrypoint, capabilities, permissions,
  health timeout and drain timeout.
- The host validates the manifest, starts a new version, performs a token
  handshake and health check, then switches the active instance pointer.
- Adapters (`SidecarProvider`, `SidecarTool`) resolve the active instance by id,
  so a successful hot upgrade does not restart `kl-core`.
- Failed startup/health keeps the previous active instance and version pointer.
- Old instances drain in-flight requests before stopping.
- Each instance receives a random capability token and a clean environment
  containing only safe platform variables plus manifest-granted variables.
- Network and filesystem arguments are checked by the host before execution.

## Consequences

- Plugin crashes are isolated to their process.
- Protocol-level permissions are not a complete OS sandbox; a malicious
  sidecar can still attempt direct I/O. Strict sandbox backends are the next
  hardening step.
- WASM tools are added separately by ADR 0008; no `dlopen`/`cdylib` plugin
  loading is used.
