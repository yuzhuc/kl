# KL GUI

The active desktop client is `Kl.Gui`, an Avalonia client for local KL
sessions. It is a `Kl.Client` protocol client: the GUI opens/resumes sessions
through `KlSessionClient` and renders Core events. Sessions, provider calls,
tool execution, policy, sandboxing, approvals and audit stay inside Rust
`kl-core`; the GUI never calls a provider or executes a tool directly.

Features: session list and rich history, streaming assistant/reasoning events,
tool cards with output and diffs, `allow` / `allow_session` / `deny` approvals,
cancel, per-turn token usage, Core status with retry, read-only observer sessions
and automations that run as throwaway Core sessions. The GUI settings page shows
Core runtime information read-only; provider, model and API keys are configured
in `~/.kl/config.toml`, `~/.kl/providers/` and `~/.kl/credentials/`.

GUI state (workspaces, layout, automations) lives under `~/.kl/gui`; KL sessions
and events are owned by Core under `~/.kl/sessions`. The GUI binary looks for
`kl-core` next to itself, in `target/{debug,release}`, or at `KL_CORE_PATH`;
`python scripts/build.py` stages `kl-core.exe` into `bin/gui` automatically.

```powershell
dotnet build gui/Kl.Gui/Kl.Gui.csproj
dotnet test gui/Kl.Gui.Tests/Kl.Gui.Tests.csproj
python scripts/build.py     # publishes bin/gui and bin/tui with kl-core.exe
```

See `Kl.Gui/README.md` for the architecture and Chinese feature notes.
