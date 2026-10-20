# Provider live smoke fixtures

`scripts/live-smoke.ps1` performs a real, minimal call for each native protocol
and records successful responses as JSON fixtures.

- `openai-chat` uses `OPENAI_API_KEY`.
- `anthropic` uses `ANTHROPIC_API_KEY`.
- `openai-responses` uses `OPENAI_API_KEY`.

If a key is not set, that protocol is marked `pending` in
`live-smoke.status.json`. Pending protocols never receive a fabricated success
fixture. Live fixtures are written only after a successful real call.

The same script exercises MCP. When `~/.kl/mcp.toml` configures an enabled
server, the real stdio handshake (`initialize` plus `tools/list`) runs against
it and the discovered tool names are recorded under `mcp` in
`live-smoke.status.json`. With no configured server the MCP entry is
`pending`.

Offline conformance uses local mock SSE servers in
`crates/kl-provider/tests/http_stream.rs`, including full-URL checks for all
three adapters.

Run:

```powershell
pwsh scripts/live-smoke.ps1
```
