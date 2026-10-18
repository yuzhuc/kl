# Plugin API

Status: **sidecar and WASM tool plugin MVPs implemented**. WASM providers are
not supported, and there is still no Rust dynamic-library ABI.

## Runtime boundaries

- The kernel, agent loop, policy engine and audit writer are not plugins.
- Sidecar plugins run as separate processes and speak newline-delimited
  JSON-RPC 2.0 over stdio.
- WASM tool plugins run in Wasmtime's Component Model runtime. They receive no
  WASI, filesystem, network, environment or process capability. The only host
  imports are `host-log` and `host-time`.
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
  "drain_timeout_ms": 2000,
  "call_timeout_ms": 30000,
  "memory_limit_mb": 64
}
```

For provider plugins, use `"kind": "provider"` and declare `models`.

A WASM tool uses the same manifest shape with `runtime` set to `wasm`; its
`entrypoint` is the component path (`.wasm`, or `.wat` for development
fixtures):

```json
{
  "id": "wasm-tool",
  "version": "0.1.0",
  "api": 1,
  "kind": "tool",
  "entrypoint": "tool.wat",
  "runtime": "wasm",
  "tool_name": "wasm_tool",
  "tool_parameters": { "type": "object" },
  "call_timeout_ms": 5000,
  "memory_limit_mb": 64
}
```

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

## WASM tool ABI

WASM components export two functions:

```wit
world tool {
  import host-log: func(message: string);
  import host-time: func() -> u64;

  export health: func() -> string;
  export execute: func(input: string) -> string;
}
```

- `health` returns a JSON string such as `{"ok":true}`.
- `execute` receives a JSON string containing `tool`, `args` and `workspace`.
  It returns a JSON string with `success`, `summary`, `output` and optional
  `diff` fields.
- `host-time` returns Unix time in milliseconds. `host-log` accepts a message;
  logs are retained only in the per-call host state.
- The component is instantiated in a fresh store for each call. `memory_limit_mb`
  caps linear memory and `call_timeout_ms` interrupts overlong calls with an
  epoch tick. Guest traps and timeouts are returned as normalized host errors;
  they do not unwind or terminate `kl-core`.
- Component imports outside `host-log` and `host-time` fail instantiation.

The component interface uses the WebAssembly Component Model, not WASI. There is
deliberately no filesystem, network, environment variable or process import.

Host-side permission checks:

- URL/host arguments must match the manifest `network` allowlist.
- Path arguments are resolved inside the workspace and must match
  `fs_read` or `fs_write`. These checks run before either runtime is invoked.
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

`plugins/examples/wasm-tool` is the minimal Component Model tool. Its `.wat`
fixture is loaded directly by Wasmtime for development; production plugins can
ship a compiled `.wasm` component.

## Limitations

- On Windows, strict mode runs sidecars through AppContainer and a
  kill-on-close Job Object. A malicious sidecar that attempts to read outside
  its granted roots is denied by the OS. Network isolation is not claimed as
  verified; the manifest host allowlist remains active.
- WASM providers are not implemented; `runtime = "wasm"` is restricted to tool
  plugins. WASM plugins cannot directly read or write workspace files, so any
  future file access needs a new, explicitly designed host capability.
- `call_timeout_ms` interrupts executing guest code. It does not impose a
  separate CPU instruction budget for untrusted code.
- Plugin signatures and a remote marketplace are not implemented yet.
