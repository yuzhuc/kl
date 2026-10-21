# KL GUI MVP Specification

> Status: active
> Branch: `feat/gui-mvp`
> Baseline: `dev`
> UI stack: Avalonia 12.1.4, .NET 10

## 1. Goal

Build a usable single-window desktop client for KL. The MVP must let a user
open or resume a session, send a prompt, watch the turn stream, inspect tool
activity and approve or deny sensitive actions.

The first GUI version uses the existing `sdk/Kl.Client` stdio client. It does
not wait for `kl serve`. The GUI talks to the client service, not to the core
process or protocol directly, so the transport can be replaced by local-server
attach later without rewriting the views.

## 2. Non-goals

- No Agent, Policy, Sandbox, Tool, Session or Audit implementation in `gui/`.
- No manual JSON-RPC handling or direct `Process.Start` of `kl-core`.
- No remote server, multi-user support, cloud sessions or web client.
- No TUI/SDK/protocol changes without a separate backend task.
- No plugin marketplace, LSP, browser control or multi-agent team UI.
- No 32-bit or non-Windows local build requirement in the GUI thread.

## 3. Architecture

```text
Avalonia Views
      |
ViewModels / GUI state
      |
GuiSessionService
      |
sdk/Kl.Client
      |
JSON-RPC over stdio -> kl-core
```

Rules:

- `GuiSessionService` owns the `KlSessionClient` lifecycle.
- A GUI presenter implements `ISessionPresenter` and
  `ISessionApprovalDecisionPresenter`.
- `KlSessionClient` events are mapped to GUI state; raw JSON stays inside the
  service boundary.
- Views and ViewModels never start the core or execute tools.
- No new abstraction unless it removes a concrete duplication or isolates an
  external boundary.

## 4. Window layout

- Left sidebar: sessions, search, new session and workspace label.
- Main area: transcript with user, assistant, reasoning, tool and error items.
- Composer: multiline prompt, send, cancel and current turn status.
- Top bar: provider, model, reasoning effort and connection state.
- Approval surface: action, resource, reason, args and three decisions.
- Diff surface: collapsible per tool result, with a readable text fallback.

Empty, loading, disconnected and failed states must be visible. The UI must
remain responsive while history, turns and tool events are processed.

## 5. Event mapping

| Session event | GUI behavior |
|---|---|
| `TurnStarted` | Mark the turn busy; enable cancel |
| `AssistantDelta` | Append to the active assistant message |
| `ReasoningDelta` | Append to a collapsed reasoning item |
| `ToolStarted` | Add a running tool card |
| `ToolOutput` | Append bounded output to that card |
| `ApprovalRequested` | Open the approval surface and wait |
| `ApprovalResolved` | Close the surface and show the decision |
| `ToolCompleted` | Mark success/failure; attach diff when present |
| `TurnCompleted` | Store token/cost data; leave busy state |
| `TurnFailed` | Show failure and leave busy state |
| `Error` | Add an error item without crashing the window |

Events arrive off the UI thread. Marshal UI mutations with
`Dispatcher.UIThread.Post` and do not block the event loop.

## 6. Approval contract

Approval decisions are exactly:

- `allow`
- `deny`
- `allow_session`

The GUI presenter must implement `ISessionApprovalDecisionPresenter` so
`allow_session` reaches the SDK. If the window closes or the turn is cancelled,
the pending approval resolves to `deny`. Never auto-approve unless the user
explicitly selected that behavior through an existing option.

## 7. Work slices

| ID | Work | Acceptance |
|---|---|---|
| G1 | Shell, layout and theme | Window opens, panes resize, no layout exceptions |
| G2 | Session list and history | Create/resume/list sessions; history is rendered |
| G3 | Chat streaming | Prompt, assistant and reasoning events update live |
| G4 | Tool activity | Started/output/completed events form readable cards |
| G5 | Approval, diff and cancel | All three decisions work; cancel leaves a valid state |
| G6 | Provider/model/settings status | Current session metadata is visible and refreshable |
| G7 | Error and reconnect states | Core missing, core exit and bad events do not crash the app |
| G8 | Tests and packaging | Windows x64 build, focused tests and bundle pass |

G1-G5 are the minimum usable GUI. G6-G8 complete the MVP release slice.

## 8. Testing and acceptance

- Unit-test view-model state transitions without a live provider.
- Use a fake `ISessionPresenter` or recorded event sequence for streaming,
  tool, approval and failure tests.
- A manual smoke run must create a session, stream a scripted/local response,
  show a tool event, resolve an approval and cancel a turn.
- Windows x64 is the required local build.
- Never commit API keys, credentials, raw secrets or full sensitive tool output.
- Do not claim live provider or MCP success without credentials and a real run.

Required checks for the GUI branch:

```powershell
dotnet build gui/Kl.Gui/Kl.Gui.csproj
dotnet test gui/Kl.Gui.Tests/Kl.Gui.Tests.csproj
node scripts/check-docs.mjs
```

If a GUI test project does not exist yet, create it with the smallest useful
set of state-transition tests; do not add a test framework just for snapshots.

## 9. Definition of done

- GUI is initialized only through `sdk/Kl.Client`.
- A user can create/resume a session and see its history.
- Assistant, reasoning, tool and approval events render correctly.
- Cancel and core failure produce a usable state.
- Approval supports allow, deny and allow-session.
- The app builds and runs on Windows x64 without unhandled exceptions.
- No core logic has been copied into `gui/`.
- The branch contains small, reviewable commits and a short completion report.

## 10. Backend dependency

`kl serve`, attach/replay/lease and cross-client synchronization are owned by
the `feat/server-attach` branch. The GUI must not implement them locally. When
that branch is ready, the transport swap happens in `sdk/Kl.Client` or the
session service boundary, not in Avalonia views.