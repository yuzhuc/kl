# KL benchmark

The acceptance benchmark is defined by `benchmarks/tasks.json` and executed by
`scripts/benchmark.mjs`. It contains 23 tasks.

## Task fields

- `id` — stable task identifier.
- `prompt` — task text passed to `kl run`.
- `workspace` — external workspace path; `${BENCH_WORKSPACE}` is expanded.
- `fixture` — name under `benchmarks/fixtures/`.
- `verify` — a command, argv array or `manual:` check. `${FIXTURE_DIR}` and
  `${WORKSPACE}` are expanded.
- `timeout_secs` — per-task wall-clock limit.

## Fixtures

Each fixture has:

```text
initial/       starting workspace
solution/      reference end state
verify.mjs     verifier receiving the workspace path
```

`--verify-only` checks the solution state. `--state initial` checks the
unsolved state and should fail; that pair proves the fixture still measures a
meaningful change.

## Run

```powershell
node scripts/benchmark.mjs --dry-run
node scripts/benchmark.mjs --verify-only
```

Use an external workspace for tasks without a local fixture. Keep live provider
results separate from offline fixture results; never fabricate a pass when a
credential, server or workspace is missing.