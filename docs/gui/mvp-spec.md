# KL GUI MVP specification

> Status: paused by product request
> Stack: Avalonia 12.1.4, .NET 10
> Shared client: `sdk/Kl.Client`

## 1. Goal

The GUI is intended to be a desktop client for local KL sessions. It must use
the shared `Kl.Client` transport and session event contract, not a separate
agent loop or direct core integration.

The current `gui/Kl.Gui` is a static shell only. It has no session list,
streaming chat, tool activity, approvals, diff or settings wiring.

## 2. Transport

`KlSessionClient` now supports:

- stdio child-process transport;
- local named-pipe/UDS discovery through `server.json`;
- `session.attach` replay;
- `session_id + seq` de-duplication;
- reconnect from `LastSeq`;
- one-writer lease acquisition and renewal.

The GUI should prefer the local server when one is available and fall back to
stdio only when no endpoint is configured or discoverable. The transport swap
must remain inside `Kl.Client`, not Avalonia views.

## 3. Non-goals

- No Agent, Policy, Sandbox, Tool, Session or Audit implementation in `gui/`.
- No manual JSON-RPC or direct `kl-core` process management.
- No remote server, multi-user support or web client.
- No GUI work until explicitly resumed.
- No 32-bit local build requirement.

## 4. Window layout

- Left sidebar: sessions, search, new session and workspace.
- Main area: transcript with user, assistant, reasoning, tool and error items.
- Composer: multiline prompt, send, cancel and turn status.
- Top bar: provider, model, reasoning effort and connection state.
- Approval surface: action, resource, reason, args and three decisions.
- Diff surface: collapsible per tool result.

## 5. Event mapping

| Session event | GUI behavior |
|---|---|
| `TurnStarted` | Mark busy; enable cancel |
| `AssistantDelta` | Append to active assistant message |
| `ReasoningDelta` | Append to collapsed reasoning |
| `ToolStarted` | Add running tool card |
| `ToolOutput` | Append bounded output |
| `ApprovalRequested` | Open approval surface |
| `ApprovalResolved` | Close and show decision |
| `ToolCompleted` | Mark success/failure and attach diff |
| `TurnCompleted` | Store usage/cost and leave busy state |
| `TurnFailed` | Show failure and leave busy state |
| `ContextCompacted` | Show a compact summary item |
| `Error` | Add an error item without crashing |

Events arrive off the UI thread. Marshal UI changes with
`Dispatcher.UIThread.Post`.

## 6. Work slices

| ID | Work | Acceptance |
|---|---|---|
| G1 | Shell/layout/theme | Window opens and resizes |
| G2 | Session list/history | Create, resume and list sessions |
| G3 | Chat streaming | Prompt, assistant and reasoning events live |
| G4 | Tool activity | Started/output/completed cards |
| G5 | Approval/diff/cancel | All three approval decisions work |
| G6 | Settings/status | Provider, model, skills and MCP visible |
| G7 | Error/reconnect | Core loss, replay and reconnect are usable |
| G8 | Tests/package | Windows build, focused tests and bundle pass |

G1-G5 are the minimum usable GUI. G6-G8 complete the MVP.

## 7. Required tests

- View-model state transitions without a live provider.
- Recorded replay/live event sequence with duplicate suppression.
- Approval allow, deny and allow-session.
- Tool activity and diff rendering.
- Core missing, core exit and reconnect states.
- Windows x64 build and package smoke.

## 8. Definition of done

- GUI only connects through `Kl.Client`.
- Session, chat, tool, approval and reconnect flows work.
- No Core logic is copied into `gui/`.
- GUI is packaged in the same release as TUI if GUI release is enabled.