# Plugin API

Status: **sidecar plugin host MVP implemented**. WASM components remain a
future runtime; there is still no Rust dynamic-library ABI.

## Runtime boundaries

- The kernel, agent loop, policy engine and audit writer are not plugins.
- External plugins run as separate processes and speak newline-delimited
  JSON-RPC 2.0 over stdio.
- A plugin gets a fresh environment plus only variables granted by its
  manifest, plus a per-instance capability token.
- The host health-checks a new process before switching traffic; failed
  startup/health keeps the previous active version.
- Hot upgrade starts a new process, switches active traffic, drains in-flight
  requests, then stops the old process.

## Manifest v1

JSON and TOML manifests are supported. The schema is
`schemas/plugin/manifest-v1.schema.json`.

```json
{
  "id": "fake-tool",
  "version": "1.0.0",
  "api": 1,
  "kind": "tool",
  "entrypoint": "../../../target/debug/fake_plugin.exe",
  "runtime": "sidecar",
  "capabilities": ["tool"],
  "permissions": {
    "network": ["example.com"],
    "env": ["EXAMPLE_PLUGIN_TOKEN"],
    "fs_read": ["workspace"],
    "fs_write": []
  },
  "tool_name": "fake_tool",
  "tool_parameters": { "type": "object" },
  "config_path": "config.json",
  "health_timeout_ms": 3000,
  "drain_timeout_ms": 2000
}
```

For provider plugins, use `"kind": "provider"` and declare `models`.

## Sidecar protocol

Host -> plugin handshake:

```json
{"jsonrpc":"2.0","id":1,"method":"plugin.handshake",
 "params":{"api":1,"token":"...","id":"fake-tool","version":"1.0.0","capabilities":["tool"]}}
```

The plugin must return `{ok:true, api:1, id:..., token:...}`. Every subsequent
request includes the token. A mismatch is rejected before installation.

Methods:

- `plugin.health` -> `{ok:true}`
- `provider.chat` with `{token, request}` -> neutral `ChatOutcome`
- `tool.execute` with `{token, tool, args, workspace}` ->
  `{success, summary, output, diff}`

Host-side permission checks:

- URL/host arguments must match the manifest `network` allowlist.
- Path arguments are resolved inside the workspace and must match
  `fs_read` or `fs_write`.
- Shell/git/plugin process execution must pass the sandbox guard. Strict
  sandbox mode fails closed if the backend is unavailable.
- Plugin environment is cleared and rebuilt from safe variables plus
  `permissions.env`.

## Core protocol methods

- `plugin.list`
- `plugin.install` with a local plugin directory path
- `plugin.reload` with a plugin id
- `plugin.enable` with a plugin id
- `plugin.disable` with a plugin id

## Examples

`plugins/examples/fake-tool` and `plugins/examples/fake-provider` use the
`fake_plugin` sidecar built by `cargo build -p kl-plugin-host`.

## Limitations

- On Windows, strict mode runs sidecars through AppContainer and a
  kill-on-close Job Object. A malicious sidecar that attempts to read outside
  its granted roots is denied by the OS. Network isolation is not claimed as
  verified; the manifest host allowlist remains active.
- WASM components are not implemented yet.
- Plugin signatures and a remote marketplace are not implemented yet.
