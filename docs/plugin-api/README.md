# Plugin API

KL supports sidecar and WASM **tool** plugins. WASM providers are not
supported. Plugins cannot replace the trusted Core, Agent Loop, Policy, Sandbox,
Session or Audit layers.

## Manifest v1

JSON and TOML manifests use `schemas/plugin/manifest-v1.schema.json`.

```json
{
  "id": "example-tool",
  "version": "0.1.0",
  "api": 1,
  "kind": "tool",
  "entrypoint": "plugin",
  "runtime": "sidecar",
  "capabilities": ["tool"],
  "permissions": {
    "fs_read": [],
    "fs_write": [],
    "network": [],
    "env": []
  },
  "tool_name": "example_tool",
  "tool_parameters": { "type": "object" }
}
```

`runtime` is `sidecar` or `wasm`. `entrypoint` is the executable or `.wasm`
component path. Tool plugins require `tool_name` and `tool_parameters`.
`integrity` and `signature` are optional during development.

## Integrity and signatures

- `integrity.sha256` binds the manifest to the entrypoint bytes.
- `signature.algorithm` is `ed25519`; `signature.value` is standard base64.
- The signature covers the canonical manifest with the signature field removed
  and defaults materialized.
- The public key comes from `security.json` in the plugin host root.
- Failed validation leaves the previous active instance running and fails
  closed for the new one.

## Sidecar runtime

Sidecars speak newline-delimited JSON-RPC 2.0 over stdio. The host validates the
manifest, starts the process with a random capability token and clean
environment, checks health, switches the active instance atomically, then
drains and stops the old instance.

## WASM runtime

WASM plugins use the Component Model and export JSON-string `health` and
`execute` functions. Only `host-log` and `host-time` are linked. Filesystem,
network, environment, process and WASI APIs are unavailable. Each call uses a
fresh store with memory and timeout limits.

## Core methods

- `plugin.list`
- `plugin.install`
- `plugin.reload`
- `plugin.enable`
- `plugin.disable`

## Limitations

- WASM providers are not supported.
- Plugin marketplace and remote installation are out of scope.
- Plugins cannot replace kernel policy, sandbox, audit or session behavior.