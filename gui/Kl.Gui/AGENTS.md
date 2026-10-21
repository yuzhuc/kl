# AGENTS.md - KL GUI

Read `docs/gui/mvp-spec.md` before making changes.

## Boundary

- Work only in the `feat/gui-mvp` worktree and branch.
- Modify `gui/Kl.Gui` and, when needed, add `gui/Kl.Gui.Tests`.
- Do not edit `crates/`, `tui/`, `sdk/`, `schemas/` or release workflows.
- If the GUI needs an SDK or protocol change, stop and report the exact need.
- Use `sdk/Kl.Client` only. Do not start `kl-core` directly, hand-write JSON-RPC
  or copy Agent, Policy, Sandbox, Session, Audit or Tool logic into the GUI.

## UI rules

- Avalonia 12.1.4 and .NET 10.
- Keep the existing minimal project style and avoid speculative abstractions.
- Use `KlSessionClient`, `ISessionPresenter` and
  `ISessionApprovalDecisionPresenter` for session behavior.
- Marshal event-driven UI updates through `Dispatcher.UIThread.Post`.
- Do not block the UI thread with synchronous I/O or `Task.Wait`.
- Approval decisions must be `allow`, `deny` or `allow_session`.
- Never log API keys, credentials, secrets or full sensitive tool output.
- Preserve keyboard access and visible focus states.
- Do not add a dependency unless it removes more code than it adds and the need
  is concrete.

## Verification

Run the smallest relevant checks after each slice:

```powershell
dotnet build gui/Kl.Gui/Kl.Gui.csproj
dotnet test gui/Kl.Gui.Tests/Kl.Gui.Tests.csproj
node scripts/check-docs.mjs
```

Local verification is Windows x64 only. Do not run cross-platform builds or
probe GitHub Actions from this branch. Do not modify `release.yml`.

## Commits

- Keep commits small and slice-oriented, for example `feat(gui): add session shell`.
- Do not merge to `dev` or `main`; the integration owner reviews and merges.
- Report changed files, tests run, known gaps and any backend dependency before
  stopping.