# ADR 0003: Minimal kernel, sidecar/WASM plugins, no Rust dylib ABI

- Status: accepted
- Date: 2026-10-17

## Context

The design calls for pluggable providers, tools, policies and audit rules, but
Rust has no stable dynamic-library ABI. Allowing the agent loop, event log or
plugin host to be replaced at runtime would make the trusted boundary and
debugging model unstable.

## Decision

The kernel is not pluginized. The following stay inside the trusted core:

- event routing and session persistence;
- the agent loop;
- policy evaluation and approval enforcement;
- audit writing;
- the JSON-RPC transport.

Capabilities are exposed through stable Rust traits and, in a later stage, a
sidecar JSON-RPC protocol:

- provider adapters (`kl-provider::Provider`);
- tools (`kl-tools::Tool`);
- policy inputs (`kl-policy`);
- audit sinks (`kl-audit::AuditSink`).

External plugins will run as separate processes (sidecar) or as WASM
components. Plugins never receive a `dlopen`-style Rust ABI. Hot-swapping will
start a new plugin process, health-check it, switch traffic, drain the old
process and then stop it.

## Consequences

- Core upgrades are explicit and type-checked.
- A plugin crash cannot take down the core process.
- Built-in providers and tools follow the same interfaces as future external
  plugins, so behavior can be tested uniformly.
- Stage 2 must add manifest verification, capability tokens and a sidecar
  supervisor; the MVP intentionally does not ship those.
