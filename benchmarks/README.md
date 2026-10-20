# KL benchmark

The acceptance benchmark is defined by `benchmarks/tasks.json` and executed by
`scripts/benchmark.mjs`. It currently contains 23 tasks.

## Task fields

- `id` — stable task identifier.
- `prompt` — task text passed to `kl run`.
- `workspace` — external workspace path; `${BENCH_WORKSPACE}` is expanded.
- `fixture` — name under `benchmarks/fixtures/`; the fixture supplies the
  workspace.
- `verify` — verification command or argv array. `${FIXTURE_DIR}` and
  `${WORKSPACE}` are expanded; a `manual:` prefix marks a non-automated check.
- `timeout_secs` — per-task wall-clock limit.

## Fixtures

Each fixture contains:

```text
initial/       starting workspace
solution/      reference end state
verify.mjs     verifier; receives the workspace path
```

`--verify-only` checks the solution state and should be green. `--state initial`
checks the unsolved state and should fail. That pair proves the fixture still
tests something meaningful.

## Run

```powershell
node scripts/benchmark.mjs --dry-run
node scripts/benchmark.mjs --verify-only
```

FIxture and external tasks are intentionally separated; tasks without a
fixture need an external workspace or are skipped with a recorded reason.
