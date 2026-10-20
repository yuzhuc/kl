# ADR 0004: bundled release layout

- Status: accepted
- Date: 2026-10-17

## Context

The client and core are separate processes with separate toolchains. Shipping
them independently makes version skew likely.

## Decision

Ship a bundle containing:

```text
kl(.exe)            .NET 10 TUI, self-contained
kl-core(.exe)       Rust release binary for the same runtime identifier
schemas/            protocol schemas for tooling/diagnostics
```

Build steps:

1. `cargo build --release -p kl-core`
2. `dotnet publish tui/Kl.Tui -c Release -r <rid> --self-contained true`
3. copy `target/<rust-target>/release/kl-core(.exe)` next to the published TUI
4. GUI packages will be added to the same release once the Avalonia MVP is ready

The TUI prefers `kl-core` next to itself and verifies the protocol version with
the handshake. A mismatched bundle fails early with a clear error.

## Consequences

- No shared native library and no Rust dynamic ABI.
- The core can be upgraded independently of the TUI as long as the protocol
  versions intersect.
- NativeAOT for the TUI remains an optional later optimization; it does not
  change the bundle contract.
