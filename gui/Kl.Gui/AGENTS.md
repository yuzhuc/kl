# AGENTS.md - KL GUI

Read `docs/gui/mvp-spec.md` before making changes.

## Boundary

- Work only in the GUI branch/worktree.
- Modify `gui/Kl.Gui` and `gui/Kl.Gui.Tests` only.
- Do not edit `crates/`, `tui/`, `sdk/`, `schemas/` or release workflows.
- If a client or protocol change is needed, stop and report it.
- Use `sdk/Kl.Client`; do not start `kl-core` directly, hand-write JSON-RPC or
  copy Agent, Policy, Sandbox, Session, Audit or Tool logic into the GUI.

## UI rules

- Avalonia 12.1.4 and .NET 10.
- Keep the existing project style and avoid speculative abstractions.
- Use `KlSessionClient` and `ISessionPresenter`.
- Marshal event-driven updates through `Dispatcher.UIThread.Post`.
- Do not block the UI thread with synchronous I/O.
- Approval decisions are `allow`, `deny` and `allow_session`.
- Never log API keys, credentials, secrets or sensitive tool output.
- Preserve keyboard access and visible focus states.

## Verification

```powershell
dotnet build gui/Kl.Gui/Kl.Gui.csproj
dotnet test gui/Kl.Gui.Tests/Kl.Gui.Tests.csproj
node scripts/check-docs.mjs
```

Local verification is Windows x64 only. Do not edit `release.yml` or probe
GitHub Actions from the GUI branch.

## Commits

- Keep commits small and slice-oriented.
- Do not merge to `dev` or `main`; the integration owner reviews.
- Report changed files, tests, known gaps and backend dependencies.