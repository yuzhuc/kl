# KL documentation

| Document | Purpose |
|---|---|
| [`architecture/overview.md`](architecture/overview.md) | Process model, transports, protocol, sessions and boundaries |
| [`project-structure.md`](project-structure.md) | Repository layout and crate/project responsibilities |
| [`security/policy.md`](security/policy.md) | Policy, approvals, memory, network and sandbox security |
| [`plugin-api/README.md`](plugin-api/README.md) | Manifest, sidecar, WASM and integrity contract |
| [`gui/mvp-spec.md`](gui/mvp-spec.md) | Current GUI architecture, Codex-style interaction contract, event mapping and release checks |
| [`adr/`](adr/) | Historical architecture decisions |
| [`ui/cc-switch-settings-page-reference.md`](ui/cc-switch-settings-page-reference.md) | External settings-page reference |

Current `dev` state: GUI is a Core-backed Avalonia desktop client with a
Codex-style rail, resizable session/project sidebar, activity rows, Shell
panels, Markdown code blocks, conversation ruler, a model chip with per-turn
model/reasoning-effort selection, full settings/project pages and shared `~/.kl`
data. The GUI test projects are green at 267 tests
(260 view-model/service, 7 Avalonia headless) and the TUI project at 55.

Product-level plans live with the repository:

- `plan/KL方案.md` — product, phase, release and risk plan
- `plan/UI方案.md` — visual system, layout, interaction and UI acceptance plan
- `plan/up.md` — hot-update plan (incremental download, core hot restart, plugin reload)

Real Provider/MCP live smoke remains open (it needs credentials or a running local MCP server).
