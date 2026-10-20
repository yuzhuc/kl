# KL Benchmark

This directory contains the KL acceptance benchmark. `benchmarks/tasks.json`
is the task list, `benchmarks/fixtures/` holds self-contained task
repositories, and `scripts/benchmark.mjs` runs them.

## Task fields

- `id`: unique task identifier.
- `prompt`: task text passed to `kl run`.
- `workspace`: benchmark workspace path. `${BENCH_WORKSPACE}` is replaced with
  the environment variable of the same name. Optional when `fixture` is set.
- `fixture`: name of a directory under `benchmarks/fixtures/`. The fixture
  supplies the workspace, so no external workspace is needed.
- `verify`: command that judges the task. Either a string that is split on
  whitespace, or an array of argv entries. `${FIXTURE_DIR}` and
  `${WORKSPACE}` are expanded before the command runs. A `manual: ...` prefix
  marks a check that cannot be automated and is skipped.
- `timeout_secs`: positive per-task wall-clock limit. The process tree is
  killed when the limit expires.

## Fixtures

Each fixture under `benchmarks/fixtures/<name>/` contains:

- `initial/`: the workspace the model starts from.
- `solution/`: the reference end state.
- `verify.mjs`: the verifier. It takes the workspace path as its first
  argument and exits non-zero when the task is not solved.

| Fixture | Task | Verify |
|---|---|---|
| `fix-failing-test` | Fix a failing unit test | `node --test` must pass |
| `cross-file-rename` | Rename a symbol across three files | `node --test` must pass |
| `read-docs-answer` | Read a doc and write `answer.md` | `answer.md` must state the max retries and backoff rule |

`--verify-only` runs the solution state by default, so the baseline is green.
`--state initial` runs the unsolved state, which must fail. That pair is the
repeatable check that a fixture is still meaningful.

## Dry run

From the repository root:

```powershell
node scripts/benchmark.mjs --dry-run
```

This validates every task and prints the task count without starting a process.

## Verify only

Runs each task's `verify` command without calling the model:

```powershell
node scripts/benchmark.mjs --verify-only
node scripts/benchmark.mjs --verify-only --state initial
node scripts/benchmark.mjs --verify-only --filter cross-file-rename
```

Tasks whose workspace needs `${BENCH_WORKSPACE}` are skipped when the
variable is not set. Skipped tasks are recorded, not counted as failures.

## Run

Set the workspace root and point `--kl` at the CLI executable:

```powershell
$env:BENCH_WORKSPACE = 'D:\benchmark-workspaces'
node scripts/benchmark.mjs `
  --tasks benchmarks/tasks.json `
  --kl .\tui\Kl.Tui\bin\Debug\net10.0\kl.exe `
  --out benchmarks/results/run-001.jsonl
```

Fixture tasks are copied into `benchmarks/.workspaces/<id>` before the run and
are then verified in place.

## Results

One JSON object per task with `id`, `ok`, `exit_code`, `duration_ms`,
`stdout_tail`, `stderr_tail`, and a `verify` object holding the same fields
for the verify command. Timeouts add `timed_out: true`. The output file is
replaced at the start of each run.
