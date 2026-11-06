# KL GUI specification

> Status: implemented Core client and Codex-style desktop shell
>
> Baseline: `dev` `450ef38`
>
> Stack: Avalonia 12.1.4, .NET 10
>
> Shared client: `sdk/Kl.Client`
>
> Verification: GUI test project `91 passed / 0 failed`

## 1. Goal

`gui/Kl.Gui` is a desktop client for local KL sessions. It uses
`Kl.Client` and renders protocol events; session state, provider calls, tools,
policy, sandboxing, approvals, audit and persistence stay inside `kl-core`.

## 2. Boundary

- No Agent, Policy, Sandbox, Tool or Audit implementation in `gui/`.
- No manual JSON-RPC framing in views or view-models.
- No direct provider API calls from the GUI.
- No direct reads of Core session/event/config files.
- GUI state lives under `~/.kl/gui`; Core session history comes from
  `session.history`.
- Python is build tooling only and is not a runtime dependency.

## 3. Transport and lifecycle

`CoreSessionService` owns the complete Core session lifecycle. `Kl.Client`
selects stdio or a discovered local server. The SDK provides attach/replay,
`session_id + seq` de-duplication, reconnect from LastSeq, writer lease and
read-only observer sessions.

When the transport drops, the GUI retries by itself: four attempts with
backoff. On the local-server transport it re-attaches and replays events after
`LastSeq`; on stdio the core is a child process, so the session is reopened
from core history and the transcript reloads. A failed loop leaves the banner
with a manual retry. A read-only observer gets a `接管写入` banner action that
re-takes the write lease without reopening the session.

## 4. Window layout

- 40px custom title bar with application menus and window controls.
- 52px rail: chat/home, plugins, automation, more, settings; project and code
  review can be pinned under the rail divider.
- Resizable session/project sidebar, approximately 280px by default, with
  collapse/expand animation.
- Main content: chat, projects, plugins, automation and settings pages.
- Settings is a full page with its own navigation column; it is not a modal.
- Project navigation opens an in-app project page. The system folder picker is
  only opened by the explicit “Add project” action.

## 5. Chat surface

- Header with active session title and message actions.
- Transcript with user bubbles, assistant Markdown, reasoning/tool/error
  activity rows and bounded tool output.
- Shell activity rows use `已运行 <command>` with status and command chips.
  Expanded Shell panels render command/output/diff without ANSI-to-HTML.
- Tool traffic is routed by `CallId`, so parallel calls write to their own
  cards; the newest card is only the fallback when the core omits an id.
- Activity rows rotate one chevron (no glyph swap) and the expanded panel fades
  in with a 3px rise; a running tool's status label pulses.
- Markdown code blocks use a header row, copy/wrap actions and a two-tone
  surface; inline code uses a dark chip.
- Composer includes project, mode/permission, provider/model, attachments,
  send and cancel controls.
- The sidebar header shows the product name (`kl`) with the Chat/Work switch in
  its menu, matching Codex's product switcher.
- The permission chip is a filled pill whose Codex-style popover lists the
  three modes 请求批准 / 帮我批准 / 完全访问 as icon + title + description rows
  (selected row and check in the warning orange) and applies through Core as
  ask / auto / dontAsk; the model chip opens a reasoning-effort menu
  (默认/低/中/高) as a Codex-style gradient slider backed by
  `agent.config.get`/`agent.config.set`, and the chip shows the active effort.
- Only the highest reasoning tier is purple in the chip and popover title;
  lower tiers stay grey.
- Hover transitions start from alpha-0 versions of their hover colour, never
  from `Transparent` (transparent white), so leaving a row does not flash.
- The command palette filters chats live and, for queries of two or more
  characters, shows matching file names plus text matches grouped by file
  (line number and snippet); clicking a result inserts an `@` reference.
- Typing `/` opens a command menu (`new`, `compact`, `fork`, `settings`,
  `plugins`, `library`, `doctor`); typing `@` opens a workspace file menu with
  prefix-ranked filtering. Both are anchored above the composer, support
  arrow/Enter/Escape and click selection, and replace the typed token.
- Approval surface exposes `allow`, `allow_session` and `deny`; Escape denies.
- Activity rows expand downward and must not force-scroll the transcript.

## 6. Conversation ruler

- One tick per user turn; no placeholder ticks on an empty chat.
- Current tick follows transcript scrolling.
- Hover shows a preview card positioned immediately near the tick.
- Clicking the tick jumps to the turn; the preview icon toggles a persistent
  mark for the turn.
- The current/selected mark keeps its square while hover growth is applied.

## 7. Sidebar interactions

- Project tree, recent sessions, pinned entries and search.
- Session drag/reorder inside projects; project folder reorder.
- Hover preview cards and section menus.
- Sticky new-chat row.
- Resizable splitter and collapsible sidebar with cancellable animation.
- Full-row hit targets; no empty click-only zones.

## 8. Event mapping

| Session event | GUI behavior |
|---|---|
| TurnStarted | Mark busy; enable cancel |
| AssistantDelta | Append to active assistant item |
| ReasoningDelta | Append to collapsed reasoning item |
| ToolStarted | Add/reuse activity row; group multiple tools |
| ToolOutput | Append bounded output |
| ApprovalRequested | Open approval surface and block the presenter |
| ApprovalResolved | Close approval and show decision |
| ToolCompleted | Set success/failure, summary, diff and output |
| TurnCompleted | Persist usage/cost and leave busy state |
| TurnFailed | Show failure and leave busy state |
| ContextCompacted | Add compact summary item |
| Error | Add error item; connection loss raises the Core banner |

Events arrive off the UI thread and are marshalled with
`Dispatcher.UIThread.Post`. Tool events carry `CallId` and are routed per call,
so overlapping tools keep independent cards, output and status.

## 9. Settings, plugins and automation

- General settings: permission mode through Core and content font size.
- Model settings: choose the default model and write a provider API key through
  Core RPCs (`config.set_default_model`, `provider.set_api_key`); the key is
  never displayed again. Runtime/provider/model status stays read-only below.
- Data settings: GUI state location and Core-managed history paths.
- About: version, privacy and shortcuts.
- Library: local prompt/note entries stored in GUI state, insertable into the
  composer. It is GUI-local by design; Core owns session data only.
- Plugins: shared `~/.kl/plugins` catalog and explicit import/open-directory
  actions.
- Automation: tasks run through temporary Core sessions; results stay bounded.

## 10. Current work status

| Work | Status |
|---|---|
| G1 Shell/window layout | Done |
| G2 Session/project sidebar | Done |
| G3 Chat streaming and history | Done through Core |
| G4 Tool activity rows and Shell panel | Done |
| G5 Approval/cancel/error | Done |
| G6 Settings/status | Done for current Core-backed scope |
| G7 Ruler, Markdown code, drag/drop and previews | Done |
| G8 Tests/local build | Done: 91 GUI tests pass, `bin/gui` build available |
| G9 Release archive and automatic reconnect UX | Done: auto reconnect with backoff, observer takeover, `--gui` release packaging on win-x64, win-arm64 and osx-arm64 |
| G10 Parallel tools, model/key settings, library and motion | Done: per-`CallId` tool routing, Core-backed model/API-key form, local library page, activity chevron rotation, panel/menu/dialog fades, page fades, pulsing run state, sidebar section height animation |
| G11 Composer `/` and `@` menus | Done: slash commands with Core actions, workspace file mentions with prefix ranking, keyboard and click selection |
| G12 Palette file/text search | Done: live chat filtering, file-name matches, content matches grouped by file with line numbers, shared cached file index |
| G13 Composer permission/model menus | Done: quick approval menu, reasoning-effort menu written through Core, effort shown in the chip |

## 11. Verification

```powershell
dotnet test D:\kl\kl\gui\Kl.Gui.Tests\Kl.Gui.Tests.csproj -c Release --nologo
python D:\kl\kl\scripts\build.py
D:\kl\bin\gui\kl.exe
```

Required checks before release:

- no failed GUI tests;
- no double-active or flash on rail/page changes;
- full-row menu hit zones and independent pin actions;
- ruler scroll/hover/jump behavior;
- Shell row expand without layout jump;
- Core disconnect/retry and observer state;
- Windows x64 package smoke;
- scripted-provider end-to-end smoke (send, tool row, expanded Shell panel).
