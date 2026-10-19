# KL Benchmark Skeleton

This directory contains a 20-task acceptance benchmark for KL. The skeleton
validates task definitions and records `kl run` results as JSONL. It does not
execute task-specific `verify` commands automatically.

## Task fields

- `id`: unique task identifier.
- `prompt`: task text passed to `kl run`.
- `workspace`: benchmark workspace path. `${BENCH_WORKSPACE}` is replaced with
  the environment variable of the same name.
- `verify`: the command or manual check used to judge success. It is recorded
  in the task file but is not run by this script.
- `timeout_secs`: positive per-task wall-clock limit. The process tree is killed
  when the limit expires.

Each benchmark workspace must be a disposable copy or fixture. `kl run` is
launched with `--yes`; the script does not reset, modify, or clean workspaces.

## Dry run

From the repository root:

```powershell
node scripts/benchmark.mjs --dry-run
```

This validates every task and prints the task count without starting a process.

## Run

Set the workspace root and point `--kl` at the CLI executable:

```powershell
$env:BENCH_WORKSPACE = 'D:\benchmark-workspaces'
node scripts/benchmark.mjs `
  --tasks benchmarks/tasks.json `
  --kl .\cli\Kl.Cli\bin\Debug\net10.0\kl.exe `
  --out benchmarks/results/run-001.jsonl
```

The output is one JSON object per task with `id`, `ok`, `exit_code`,
`duration_ms`, `stdout_tail`, and `stderr_tail`. Timeouts add `timed_out: true`.
The output file is replaced at the start of each run.