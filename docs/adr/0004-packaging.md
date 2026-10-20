# ADR 0004: bundled release layout

- Status: accepted
- Date: 2026-10-17

## Context

The client and core are separate processes with separate toolchains. Shipping
them independently makes version skew likely.

## Decision

Ship a bundle containing:

```text
kl(.exe)            .NET 10 CLI, framework-dependent by default
kl-core(.exe)       Rust release binary for the same runtime identifier
schemas/            protocol schemas for tooling/diagnostics
```

Build steps:

1. `cargo build --release -p kl-core`
2. `dotnet publish tui/Kl.Tui -c Release -r <rid> --self-contained false`
3. copy `target/release/kl-core(.exe)` next to the published CLI
4. optionally publish self-contained/single-file for users without .NET 10

The CLI prefers `kl-core` next to itself and verifies the protocol version with
the handshake. A mismatched bundle fails early with a clear error.

## Consequences

- No shared native library and no Rust dynamic ABI.
- The core can be upgraded independently of the CLI as long as the protocol
  versions intersect.
- NativeAOT for the CLI remains an optional later optimization; it does not
  change the bundle contract.
