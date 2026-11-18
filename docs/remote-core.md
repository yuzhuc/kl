# Remote Core over TCP

KL normally lets the GUI/TUI start a private `kl-core` child over stdio, or
connect to a local named pipe / Unix socket. `kl-core --serve --listen` serves
the same JSON-RPC protocol over TCP, so the agent can run on one machine while
the GUI, TUI or CLI run somewhere else.

## Server

```powershell
kl-core --serve --listen 0.0.0.0:8787 --token <secret> `
  --home C:\kl-home --config C:\kl-home\config.toml
```

- `--listen 127.0.0.1:8787` stays on loopback and needs no token.
- Any address beyond loopback **requires** `--token`; the core refuses to start
  without it.
- `--listen 127.0.0.1:0` binds a free port; the actual address is written to
  `<home>/server.json` and printed on stderr.
- The protocol, sessions, leases, events and tools are identical to the local
  server transport. Multiple clients share one core; only the lease holder may
  run turns, other clients attach as read-only observers.

## Clients

Every `Kl.Client`-based client honours two environment variables:

```powershell
$env:KL_CORE_ENDPOINT = "tcp://build-box:8787"
$env:KL_CORE_TOKEN = "<secret>"

kl chat --workspace .          # TUI
kl run "fix the failing test"  # TUI one-shot
D:\kl\bin\run-gui.bat          # GUI
```

`KL_CORE_ENDPOINT` may be `tcp://host:port` or a bare `host:port`. Without it,
clients keep using `server.json`, stdio, or the local endpoint as before. When
an explicit endpoint is set, a connection failure is reported instead of
silently starting a local core, so a typo cannot run the agent on the wrong
machine.

The GUI also has a saved setting: 设置 → 连接 → 控制其他设备 → 远程 KL Core.
Fill in the address and token and press 保存; the endpoint and token are stored
in `gui/state.json` and `credentials/core.token`. The connection changes on the
next launch, and 清除 returns the GUI to a local core.

Programmatic clients set the same values on `KlClientOptions`:

```csharp
var options = new KlClientOptions
{
    Endpoint = "tcp://192.168.1.20:8787",
    Token = Environment.GetEnvironmentVariable("KL_CORE_TOKEN")
};
await using var client = await KlCoreClient.ConnectOrStartAsync(options);
```

## Security

- The token is a shared secret sent in plain text in the handshake. There is no
  TLS yet. Use a private network, a VPN, or an SSH tunnel
  (`ssh -L 8787:127.0.0.1:8787 me@build-box` then connect to
  `tcp://127.0.0.1:8787`) when the network is not trusted.
- The core runs tools with the account and sandbox policy of the server
  process. Treat remote access as shell access to that machine.
- Workspace paths are resolved on the server. A GUI running elsewhere can open
  and edit only workspaces that exist there; attachments are read by the GUI
  and sent inside the prompt, so local files still work.
- Keep the token out of shell history and logs; rotate it by restarting the
  server with a new value.

## Limits

- One core per home directory: two servers sharing a home would race on the
  session store.
- The saved GUI endpoint applies after a restart; there is no in-place
  reconnect yet. Environment variables still win for one-off sessions.
- The remote endpoint is not advertised to other machines; clients need the
  address out of band.
