# Plugin examples

This directory contains example plugin manifests and entrypoints used by the
plugin host tests and demos.

Supported plugin kinds are **tool** plugins using manifest v1:

- sidecar: executable process speaking newline-delimited JSON-RPC 2.0 over stdio;
- WASM: Component Model module exporting JSON-string `health` and `execute`.

See [`../docs/plugin-api/README.md`](../docs/plugin-api/README.md) for the
manifest, permissions, integrity/signature contract and host behavior.

Plugins are disabled by default. Enabling one is an explicit host action;
integrity and optional Ed25519 signatures are checked before activation.
