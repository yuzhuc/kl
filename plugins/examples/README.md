# Sidecar plugin examples

Build the fake sidecar first:

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

## Fake provider

```powershell
kl plugin install plugins/examples/fake-provider --home D:\kl-home
kl run "say hello" --provider fake-provider --model fake-model --home D:\kl-home
```

The provider adapter resolves the active sidecar instance by plugin id, so
`kl plugin install` with a newer version hot-swaps the sidecar without
restarting `kl-core`; a failed health check keeps the previous version active.
