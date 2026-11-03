# KL GUI

The active desktop client is `DeepSeekHarness.Gui`, an Avalonia client for
OpenAI-compatible streaming chat, workspaces, sessions, settings, local plugins,
and automation.

GUI data, sessions, settings, and local plugins are stored under `~/.kl/gui`.

```powershell
dotnet build gui/DeepSeekHarness.Gui/DeepSeekHarness.Gui.csproj
dotnet test gui/DeepSeekHarness.Gui.Tests/DeepSeekHarness.Gui.Tests.csproj
```
