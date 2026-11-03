# KL GUI

The active desktop client is `DeepSeekHarness.Gui`, an Avalonia client for
OpenAI-compatible streaming chat, workspaces, sessions, settings, local plugins,
and automation.

GUI state lives under `~/.kl/gui`; credentials and plugins use the shared `~/.kl/credentials` and `~/.kl/plugins` directories.

```powershell
dotnet build gui/DeepSeekHarness.Gui/DeepSeekHarness.Gui.csproj
dotnet test gui/DeepSeekHarness.Gui.Tests/DeepSeekHarness.Gui.Tests.csproj
```
