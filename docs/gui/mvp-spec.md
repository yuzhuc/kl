# KL GUI MVP specification

> Status: Phase 1 implemented — `gui/Kl.Gui` is a `Kl.Client` protocol client
> over `kl-core` (stdio, with local-server discovery inside `Kl.Client`).
> Phase 2 (GUI release packaging and local-server-first multi-window UX) is open.
> Stack: Avalonia 12.1.4, .NET 10
> Shared client: `sdk/Kl.Client`

## 1. Goal

The GUI is a desktop client for local KL sessions. It uses the shared
`Kl.Client` transport and session event contract, not a separate agent loop or
direct core integration.

Implemented: `gui/Kl.Gui` opens, resumes and drives sessions through
`KlSessionClient`. Session state, provider calls, tool execution, policy,
sandboxing, approvals and audit all stay inside `kl-core`; the GUI only renders
protocol events and sends protocol requests.

## 2. Transport

`KlSessionClient` supports:

- stdio child-process transport;
- local named-pipe/UDS discovery through `server.json`;
- `session.attach` replay;
- `session_id + seq` de-duplication;
- reconnect from `LastSeq` (`session.resume` + `session.attach` + lease);
- one-writer lease acquisition and renewal;
- read-only observer sessions when another client holds the lease.

Implementation rules:

- `gui/Kl.Gui/Services/CoreSessionService.cs` is the only place that starts,
  resumes, observes and disposes Core sessions.
- The transport choice (stdio child process or discovered local server) stays
  inside `Kl.Client`; no Avalonia view or view-model opens a socket, speaks
  JSON-RPC or starts `kl-core` directly.
- Phase 1 runs on stdio when no local server is discoverable. When a server is
  present, the client attaches, takes the write lease, or becomes a read-only
  observer (shown as such in the UI).
- A dropped Core connection is reported by the SDK as a presenter `Error` event;
  the GUI shows a Core banner with a retry action and fails the active turn.
  Automatic `LastSeq` reconnect and observer promotion are Phase 2 UX.

## 3. Non-goals

- No Agent, Policy, Sandbox, Tool, Session or Audit implementation in `gui/`.
- No manual JSON-RPC framing or direct `kl-core` process management in views or
  view-models (the SDK owns the stdio child process).
- No direct provider API calls from the GUI; provider, model and credentials are
  Core configuration.
- No reading Core session/event files directly; history comes from
  `session.history`.
- No remote server, multi-user support or web client.
- No 32-bit local build requirement.

## 4. Window layout

- Left sidebar: sessions, search, new session and workspace.
- Main area: transcript with user, assistant, reasoning, tool and error items;
  tool cards show arguments, bounded output, summary and diff.
- Composer: multiline prompt, send, cancel and turn status.
- Top bar: Core connection state, provider/model reported by Core, and
  per-turn/cumulative token usage (cost when Core reports it).
- Approval surface: action, resource, reason, args and three decisions
  (`allow`, `allow_session`, `deny`); Escape denies.
- Core banner: connection error/retry and the read-only observer notice.
- Settings → Models: read-only Core runtime information — connection status,
  Core/protocol version, provider/model, API-key configured flag, Core path and
  KL home.
- Settings → General: permission mode (mapped to Core approval mode through
  `permissions.set`) and content font size.

## 5. Event mapping

| Session event | GUI behavior |
|---|---|
| `TurnStarted` | Mark busy; enable cancel |
| `AssistantDelta` | Append to active assistant message |
| `ReasoningDelta` | Append to collapsed reasoning |
| `ToolStarted` | Add running tool card (`CallId`/`Seq` available) |
| `ToolOutput` | Append bounded output (UI cap 12k chars) |
| `ApprovalRequested` | Open approval surface; presenter waits for the decision |
| `ApprovalResolved` | Close the surface and show the decision |
| `ToolCompleted` | Mark success/failure and attach summary/diff/output |
| `TurnCompleted` | Store usage/cost and leave busy state; `cancelled` shows stopped |
| `TurnFailed` | Show failure and leave busy state |
| `ContextCompacted` | Show a compact summary item |
| `Error` | Add an error item; connection loss also raises the Core banner |

Presenter events carry `Seq`, `TurnId` and (for tool events) `CallId`, plus
bounded `Output` on `ToolCompleted`. Events arrive off the UI thread; the GUI
marshals every change with `Dispatcher.UIThread.Post`. Phase 1 renders one
active tool card at a time; concurrently running tools can be split by
`CallId` in Phase 2.

## 6. Work slices

| ID | Work | Status |
|---|---|---|
| G1 | Shell/layout/theme | Done |
| G2 | Session list/history | Done via `session.list` / `session.history` |
| G3 | Chat streaming | Done via Core events |
| G4 | Tool activity | Done for started/output/completed/diff; concurrent cards are Phase 2 |
| G5 | Approval/diff/cancel | Done: all three decisions, diff rendering and cancel |
| G6 | Settings/status | Done: Core runtime info read-only, permission mode applied via Core |
| G7 | Error/reconnect | Partial: banner + retry + read-only observer; automatic reconnect is Phase 2 |
| G8 | Tests/package | Partial: build works and GUI Core-backed tests exist; release packaging is Phase 2 |

G1-G5 are the minimum usable GUI and are implemented. G6 is implemented; G7 and
G8 remain partial as noted.

## 7. Verification

- SDK regression coverage: `tui/Kl.Tui.Tests/SessionSdkFixesTests.cs` checks
  teardown after connection loss, reconnect after a Core restart (`session.resume`
  before `session.attach`) and the presenter `Seq`/`CallId`/`Output` fields.
- GUI coverage lives in `gui/Kl.Gui.Tests`: protocol parser and presenter tests,
  plus view-model core-flow tests with a fake `ICoreSessionService` (draft send,
  event-to-transcript mapping, approval panel, cancel, history load, observer
  mode, automation, settings and retry).
- TUI tests cover protocol fixtures, fake-core replay/lease and server reconnect;
  Rust tests cover the server transport and `turn.start` loading a persisted
  session after restart.
- The GUI exposes scripted-Core end-to-end hooks (`KL_GUI_E2E_OPEN_LAST`,
  `KL_GUI_E2E_PROMPT`, `KL_GUI_E2E_APPROVAL`) for smoke runs without a real
  provider key.
- Remaining verification before Phase 2: recorded replay/live event ordering at
  the GUI layer, and Windows x64 package smoke.

## 8. Definition of done

- GUI only connects through `Kl.Client`. Done (forced by the service boundary).
- Session, chat, tool, approval, cancel and history flows work through Core. Done
  for Phase 1.
- No Core logic is copied into `gui/`. Done.
- GUI is packaged in the same release as the TUI. Pending (Phase 2); local
  `python scripts/build.py` builds `bin/gui` with a sibling `kl-core.exe`.
