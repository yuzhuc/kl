# KL GUI

Avalonia-based graphical client for KL.

This is currently an initialization scaffold: it opens a minimal window and
does not yet implement chat, history, diff, approvals or settings. The full GUI
MVP will reuse `tui/Kl.Client` and the same JSON-RPC session/event contract as
the TUI.

Run:

```powershell
dotnet run --project gui/Kl.Gui
```

Project: `gui/Kl.Gui/Kl.Gui.csproj` (assembly/namespace `Kl.Gui`).
