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
panels, Markdown code blocks, conversation ruler, full settings/project pages
and shared `~/.kl` data. The GUI test project is green at 60 tests.

Product-level plans are maintained outside the repository:

- `D:\kl\KL方案.md` — product, phase, release and risk plan
- `D:\kl\UI方案.md` — visual system, layout, interaction and UI acceptance plan

GUI release packaging, real Provider/MCP live smoke, automatic reconnect UX and
exact concurrent-tool grouping remain open.
