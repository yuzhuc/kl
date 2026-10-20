# Plugin API

KL supports sidecar and WASM **tool** plugins. WASM providers are not
supported. The trusted core (agent loop, policy, sessions, audit and transport)
cannot be replaced by a plugin.

## Manifest v1

JSON and TOML manifests use
`schemas/plugin/manifest-v1.schema.json`. Core fields:

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
component path. `tool_name` and `tool_parameters` are required for tool
plugins. `integrity` and `signature` are optional for development.

## Integrity and signatures

- `integrity.sha256` binds the manifest to the entrypoint bytes.
- `signature.algorithm` is `ed25519`; `signature.value` is standard base64.
- The signature covers the canonical manifest payload with `signature` removed
  and defaults materialized.
- The public key comes from `security.json` in the plugin host root.
- A failed integrity or signature check leaves the previous active instance
  running and fails closed for the new one.

## Sidecar runtime

Sidecars are separate processes speaking newline-delimited JSON-RPC 2.0 over
stdio. The host:

1. validates the manifest and integrity;
2. starts the new instance with a random capability token and clean environment;
3. performs a health check;
4. switches the active instance only after success;
5. drains and stops the old instance.

Adapters resolve the active instance by plugin id, so a hot upgrade does not
restart the core. Filesystem and network arguments are checked by the host
before execution.

## WASM tool runtime

WASM plugins use the Component Model and export JSON-string `health` and
`execute` functions. The host provides only `host-log` and `host-time`; WASI,
filesystem, network, environment and process APIs are not linked. Every call
uses a fresh store with a memory cap and epoch timeout. Traps, timeouts and
protocol failures become bounded tool errors.

## Core methods

- `plugin.list`
- `plugin.install`
- `plugin.reload`
- `plugin.enable`
- `plugin.disable`

## Examples

See [`../../plugins/examples/README.md`](../../plugins/examples/README.md) and
the fixture plugins under `crates/kl-plugin-host`.

## Limitations

- WASM providers are not supported.
- Plugin marketplace and remote installation are out of scope.
- Plugin capabilities cannot replace kernel policy, sandbox, audit or session
  behavior.
