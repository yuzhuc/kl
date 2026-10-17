# Provider live smoke fixtures

`scripts/live-provider-smoke.ps1` performs a real, minimal call against the
configured OpenAI-compatible and Anthropic APIs and records the normalized
response as a JSON fixture.

- If `OPENAI_API_KEY` or `ANTHROPIC_API_KEY` is not set, that provider is
  marked `pending` in `live-smoke.status.json`.
- A pending provider is **not** represented by a fabricated success fixture.
- Live fixtures are written only after a successful real call.
- Offline conformance uses local mock SSE servers in
  `crates/kl-provider/tests/http_stream.rs`.

Run:

```powershell
pwsh scripts/live-provider-smoke.ps1
```
