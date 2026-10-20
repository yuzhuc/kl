# ADR 0011: final release RID matrix

- Status: accepted
- Date: 2026-10-21

## Context

The release workflow shipped seven runtime identifiers. `win-x86` and
`osx-x64` cost build and test time on every release while covering a small
share of users, and the matrix was still described as undecided in the docs.

## Decision

Ship exactly five runtime identifiers:

| RID | Platform | Reason |
|---|---|---|
| win-x64 | Windows x64 | primary Windows target |
| win-arm64 | Windows ARM64 | growing Windows on ARM share |
| linux-x64 | Linux x64 | servers and CI |
| linux-arm64 | Linux ARM64 | cloud ARM instances |
| osx-arm64 | macOS ARM64 | Apple Silicon |

`win-x86` and `osx-x64` are dropped. The matrix is recorded in
`.github/workflows/release.yml`, `README.md` and
`docs/architecture/overview.md`, and `scripts/package-format.mjs` accepts only
these five RIDs.

## Consequences

- One release contains five archives instead of seven.
- Changing the matrix requires a new ADR that updates all four places.
- The GUI is packaged separately and stays on win-x64, win-arm64 and
  osx-arm64; it does not change the TUI matrix.
