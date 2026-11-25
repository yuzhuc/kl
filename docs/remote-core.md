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

### TLS

Pass a certificate and private key to encrypt the TCP transport:

```powershell
python scripts/make-tls-cert.py --out-dir tls   # self-signed cert.pem + key.pem
kl-core --serve --listen 0.0.0.0:8787 --token <secret> `
  --tls-cert .\tls\cert.pem --tls-key .\tls\key.pem
```

- `--tls-cert` and `--tls-key` must be used together and only with `--listen`.
- The core prints the leaf certificate's SHA-256 fingerprint on startup and
  writes a `tls://host:port` endpoint into `server.json`.
- Clients pin that fingerprint; certificates are not checked against a CA, so a
  self-signed certificate is enough. Distribute the fingerprint out of band.
- A mismatching fingerprint or an untrusted certificate aborts the connection;
  clients never fall back to plain text.
- `--token` is still required beyond loopback: TLS protects the wire, the token
  authorizes the client.

The script needs `openssl` (Git for Windows ships one). The equivalent command
is:

```bash
openssl req -x509 -newkey rsa:2048 -nodes -keyout key.pem -out cert.pem   -days 3650 -subj "/CN=localhost"   -addext "subjectAltName=DNS:localhost,IP:127.0.0.1"
```

The private key must be PEM (PKCS#8, `BEGIN PRIVATE KEY`); the certificate may
be PEM or DER.

## Clients

Every `Kl.Client`-based client honours three environment variables:

```powershell
$env:KL_CORE_ENDPOINT = "tls://build-box:8787"
$env:KL_CORE_TOKEN = "<secret>"
$env:KL_CORE_TLS_FINGERPRINT = "9546C9C10A006F9C...DCBD"

kl chat --workspace .          # TUI
kl run "fix the failing test"  # TUI one-shot
D:\kl\bin\run-gui.bat          # GUI
```

`KL_CORE_ENDPOINT` may be `tcp://host:port`, `tls://host:port` or a bare
`host:port`. A `tls://` endpoint requires `KL_CORE_TLS_FINGERPRINT` (colons and
case are ignored). Without `KL_CORE_ENDPOINT`,
clients use `server.json`. A discovered `tcp://` or `tls://` endpoint is
treated as remote: connection failures (including a missing TLS fingerprint)
are reported instead of falling back to a local core. Only a stale local
pipe/socket `server.json` falls back to stdio, so a typo or an unreachable
build box cannot run the agent on the wrong machine.

The GUI also has a saved setting: 设置 → 连接 → 控制其他设备 → 远程 KL Core.
Fill in the address, token and (for `tls://`) the fingerprint and press 保存;
the endpoint and fingerprint are stored in `gui/state.json`, the token in
`credentials/core.token`. 测试连接 checks the draft without saving it, 保存
applies on the next launch, and 清除 returns the GUI to a local core.

Programmatic clients set the same values on `KlClientOptions`:

```csharp
var options = new KlClientOptions
{
    Endpoint = "tls://192.168.1.20:8787",
    Token = Environment.GetEnvironmentVariable("KL_CORE_TOKEN"),
    TlsFingerprint = Environment.GetEnvironmentVariable("KL_CORE_TLS_FINGERPRINT")
};
await using var client = await KlCoreClient.ConnectOrStartAsync(options);
```

## Security

- Without `--tls-cert`, the token is a shared secret sent in plain text in the
  handshake. Use TLS, a private network, a VPN, or an SSH tunnel
  (`ssh -L 8787:127.0.0.1:8787 me@build-box` then connect to
  `tcp://127.0.0.1:8787`) when the network is not trusted.
- TLS authenticates the server through a pinned SHA-256 fingerprint, not a CA
  chain. If the certificate is regenerated, update every client's fingerprint;
  a mismatch is a hard failure.
- The core runs tools with the account and sandbox policy of the server
  process. Treat remote access as shell access to that machine.
- Workspace paths are resolved on the server. A GUI running elsewhere can open
  and edit only workspaces that exist there; attachments are read by the GUI
  and sent inside the prompt, so local files still work.
- Keep the token out of shell history and logs; rotate it by restarting the
  server with a new value.

## Limits

- At most 64 concurrent TCP/TLS connections; extra connections are closed and
  logged with the peer address.
- Each client has a 1024-line outbound queue. A client that stops reading is
  removed from the event broadcast to bound memory; reconnect and attach from
  `LastSeq` to recover.
- TLS writes and flushes time out after 30 seconds. Plain TCP has no write
  timeout; use TLS for untrusted networks.
- One core per home directory: two servers sharing a home would race on the
  session store.
- The saved GUI endpoint applies after a restart; 重连 re-runs the saved
  connection. Environment variables still win for one-off sessions.
- The remote endpoint is not advertised to other machines; clients need the
  address out of band.
