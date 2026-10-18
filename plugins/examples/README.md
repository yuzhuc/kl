# Plugin examples

For the sidecar examples, build the fake sidecar first:

```powershell
cargo build -p kl-plugin-host
```

The manifests use the Windows binary path
`../../../target/debug/fake_plugin.exe`. On Unix, replace `.exe` with the
platform binary name.

## Fake tool

```powershell
kl plugin install plugins/examples/fake-tool --home D:\kl-home
kl plugin list --home D:\kl-home
```

A scripted or real agent can call the `fake_tool` tool once the plugin is
installed. The manifest grants `example.com` network access, workspace read
access and the `EXAMPLE_PLUGIN_TOKEN` environment variable only.

## WASM tool

`wasm-tool` is a minimal Component Model tool and needs no separate build step
for local use because Wasmtime loads its `.wat` fixture directly:

```powershell
kl plugin install plugins/examples/wasm-tool --home D:\kl-home
kl plugin list --home D:\kl-home
```

It imports only `host-log` and `host-time`; it has no WASI, filesystem,
network, environment or process capability. The `execute` JSON input and output
contract is documented in `docs/plugin-api/README.md`.

## Fake provider

```powershell
kl plugin install plugins/examples/fake-provider --home D:\kl-home
kl run "say hello" --provider fake-provider --model fake-model --home D:\kl-home
```

Plugin adapters resolve the active instance by plugin id, so `kl plugin install`
with a newer version hot-swaps the sidecar or WASM component without restarting
`kl-core`; a failed health check keeps the previous version active.
