# KL documentation

| Document | Purpose |
|---|---|
| [`architecture/overview.md`](architecture/overview.md) | Process model, transports, protocol, sessions and boundaries |
| [`project-structure.md`](project-structure.md) | Repository layout and crate/project responsibilities |
| [`security/policy.md`](security/policy.md) | Policy, approvals, memory, network and sandbox security |
| [`plugin-api/README.md`](plugin-api/README.md) | Manifest, sidecar, WASM and integrity contract |
| [`gui/mvp-spec.md`](gui/mvp-spec.md) | GUI Phase 1 (implemented) and Phase 2 scope, transport and event contract |
| [`adr/`](adr/) | Historical architecture decisions |
| [`ui/cc-switch-settings-page-reference.md`](ui/cc-switch-settings-page-reference.md) | External settings-page reference |

The repository contains the implemented R1/R2/R3 work, including the local
multi-client transport and the GUI Phase 1 integration: `gui/Kl.Gui` is now a
`Kl.Client` protocol client over `kl-core` (sessions, events, tools, approvals,
cancel and history). GUI release packaging and GUI Phase 2 multi-window UX
remain open. The separate product plan is maintained outside the repository.