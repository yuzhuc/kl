# KL GUI

The active desktop client is `Kl.Gui`, an Avalonia client for
OpenAI-compatible streaming chat, workspaces, sessions, settings, local plugins,
and automation.

GUI state lives under `~/.kl/gui`; credentials and plugins use the shared `~/.kl/credentials` and `~/.kl/plugins` directories.

```powershell
dotnet build gui/Kl.Gui/Kl.Gui.csproj
dotnet test gui/Kl.Gui.Tests/Kl.Gui.Tests.csproj
```
