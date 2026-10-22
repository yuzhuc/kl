# KL GUI

Avalonia 12.1.4 graphical client for KL.

## Status

The current GUI is a static shell with the planned layout, theme and window
chrome. It does not yet reference `sdk/Kl.Client` and does not implement chat,
history, streaming events, tool cards, approvals, diff or settings.

GUI integration is intentionally paused. The TUI and local multi-client Core
are the current priority.

## Build

```powershell
dotnet build gui/Kl.Gui/Kl.Gui.csproj
dotnet run --project gui/Kl.Gui
```

## Next integration step

Add a project reference to `sdk/Kl.Client` and build the shell incrementally
from the slices in [`../docs/gui/mvp-spec.md`](../docs/gui/mvp-spec.md). Do not
start a separate Core process from Avalonia code.