# Plugin API

Status: **sidecar and WASM tool plugin MVPs implemented, with optional Ed25519
signatures and entrypoint integrity checks**. WASM providers are not supported,
and there is still no Rust dynamic-library ABI.

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
  "memory_limit_mb": 64,
  "integrity": { "sha256": "<64 hex characters>" },
  "signature": { "algorithm": "ed25519", "value": "<base64 signature>" }
}
```

`integrity` and `signature` are optional for development plugins. Production
hosts should set `require_signature`; see "Integrity and signatures" below.

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

## Integrity and signatures

The host checks the entrypoint file and, when present, its Ed25519 signature
before starting or replacing a sidecar or WASM instance.

- `integrity.sha256` is the SHA-256 of the entrypoint file bytes as 64 hex
  characters (lowercase or uppercase). When the field is present, a mismatch
  is always rejected.
- `signature` is optional. When present, `algorithm` must be `ed25519` and
  `value` is a standard-base64 Ed25519 signature.
- The signature covers the compact JSON returned by
  `PluginManifest::signing_payload()`: the manifest with the `signature`
  field removed, defaults materialized, and fields in this order:
  `id, version, api, kind, entrypoint, runtime, capabilities, permissions,
  tool_name, tool_parameters, models, config_path, health_timeout_ms,
  drain_timeout_ms, call_timeout_ms, memory_limit_mb, integrity`.
  `tool_parameters` is serialized as a normal JSON object.
- The public key is a standard-base64 encoded raw 32-byte Ed25519 public key.
  The host reads it from `security.json` in the plugin host root (the
  directory containing `installed/`) or from `KL_PLUGIN_PUBLIC_KEY`; the
  config file wins. `security.json` looks like:

```json
{
  "require_signature": true,
  "public_key": "<base64 public key>"
}
```

- With `require_signature: true`, both `integrity` and `signature` are
  mandatory and a public key must be configured. With `false` (the default),
  unsigned development plugins remain usable, but any hash or signature that
  is present is still enforced.
- A signature without `integrity` is rejected because it would not bind the
  signature to the entrypoint bytes.
- Verification runs in `PluginHost::start_instance`, so both runtimes and all
  start paths (install, reload, enable, startup scan) share the same gate.
  A failed check returns `PluginError::Integrity` before any process or WASM
  instance is started. Install and reload keep the previously active version.
- Private keys are never read, stored or logged by the host. Errors contain
  only the failure category, never key material.

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
- Only the entrypoint file is hashed. Mutable files such as `config.json`
  and ancillary resources are not covered by `integrity.sha256`.
- Signature verification has no key rotation, revocation or trust-store
  support yet. The signing payload is tied to the current manifest field set;
  adding manifest fields later requires a new signing-payload version.
- Signature checking is subject to a verify-then-execute race if an attacker
  can write the installed plugin directory between verification and process
  start. Installed plugin directories must be owned by the host user and not
  writable by untrusted users.
- Remote installation and a plugin marketplace remain out of scope.
