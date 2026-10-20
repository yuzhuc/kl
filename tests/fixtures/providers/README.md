# Provider live smoke fixtures

`scripts/live-smoke.ps1` performs a minimal real call for each native protocol
and records successful responses as JSON fixtures.

- `openai-chat` uses `OPENAI_API_KEY`.
- `anthropic` uses `ANTHROPIC_API_KEY`.
- `openai-responses` uses `OPENAI_API_KEY`.

If a key is not set, the protocol is recorded as `pending` in
`live-smoke.status.json`; success is never fabricated. Live fixtures are written
only after a successful real call.

The same script performs a real MCP stdio handshake (`initialize` and
`tools/list`) for enabled servers in `~/.kl/mcp.toml`. With no server the MCP
entry is `pending`. Offline SSE conformance remains in
`crates/kl-provider/tests/http_stream.rs`.

Run:

```powershell
pwsh scripts/live-smoke.ps1
```
