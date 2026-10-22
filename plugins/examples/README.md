# Plugin examples

This directory contains manifests and entrypoints exercised by the plugin host
tests and demos.

Supported kinds:

- sidecar: executable speaking newline-delimited JSON-RPC 2.0 over stdio;
- WASM: Component Model module exporting JSON-string `health` and `execute`.

See [`../docs/plugin-api/README.md`](../docs/plugin-api/README.md) for the
manifest, permissions, integrity and signature contract.

Plugins are disabled by default. Activation requires an explicit host action
and passes integrity/signature checks first.