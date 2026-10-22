# Provider live smoke

`scripts/live-smoke.ps1` performs a minimal real call for each configured
native provider and records successful output as a fixture.

- `openai-chat` uses `OPENAI_API_KEY`.
- `anthropic` uses `ANTHROPIC_API_KEY`.
- `openai-responses` uses `OPENAI_API_KEY`.

If a key is absent or a call fails, record it as `pending` or failed; never
fabricate success. The script also performs a real MCP stdio handshake for
enabled servers in `~/.kl/mcp.toml`.

Run:

```powershell
pwsh scripts/live-smoke.ps1
```