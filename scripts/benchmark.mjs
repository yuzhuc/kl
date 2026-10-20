import { spawn, spawnSync } from 'node:child_process'
import { appendFileSync, cpSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const scriptDir = path.dirname(fileURLToPath(import.meta.url))
const root = path.resolve(scriptDir, '..')
const fixtureRoot = path.join(root, 'benchmarks', 'fixtures')
const maxTailChars = 8192

const valueOptions = {
  '--tasks': 'tasks',
  '--kl': 'kl',
  '--out': 'out',
  '--workspace-root': 'workspaceRoot',
}

function parseArgs(argv) {
  const options = {
    tasks: path.join(root, 'benchmarks', 'tasks.json'),
    kl: 'kl',
    out: path.join(root, 'benchmarks', 'results', 'latest.jsonl'),
    workspaceRoot: path.join(root, 'benchmarks', '.workspaces'),
    dryRun: false,
    verifyOnly: false,
    filter: null,
    state: null,
  }

  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i]
    if (arg === '--dry-run') {
      options.dryRun = true
      continue
    }
    if (arg === '--verify-only') {
      options.verifyOnly = true
      continue
    }
    if (arg === '--filter' || arg === '--state') {
      if (i + 1 >= argv.length) {
        throw new Error(`missing value for ${arg}`)
      }
      options[arg.slice(2)] = argv[++i]
      continue
    }
    const key = valueOptions[arg]
    if (!key) {
      throw new Error(`unknown option: ${arg}`)
    }
    if (i + 1 >= argv.length) {
      throw new Error(`missing value for ${arg}`)
    }
    options[key] = argv[++i]
  }

  if (options.state && !['initial', 'solution'].includes(options.state)) {
    throw new Error('--state must be initial or solution')
  }

  return options
}

function validateTasks(tasks) {
  if (!Array.isArray(tasks)) {
    throw new Error('tasks file must contain a JSON array')
  }

  const ids = new Set()
  for (const [index, task] of tasks.entries()) {
    const label = `task[${index}]`
    if (task === null || typeof task !== 'object' || Array.isArray(task)) {
      throw new Error(`${label} must be an object`)
    }
    for (const field of ['id', 'prompt']) {
      if (typeof task[field] !== 'string' || task[field].trim() === '') {
        throw new Error(`${label}.${field} must be a non-empty string`)
      }
    }
    if (Array.isArray(task.verify)) {
      if (task.verify.length === 0 || task.verify.some((part) => typeof part !== 'string' || part === '')) {
        throw new Error(`${label}.verify must be a non-empty array of strings`)
      }
    } else if (typeof task.verify !== 'string' || task.verify.trim() === '') {
      throw new Error(`${label}.verify must be a non-empty string or array of strings`)
    }
    if (typeof task.fixture === 'string') {
      if (task.fixture.trim() === '') {
        throw new Error(`${label}.fixture must be a non-empty string`)
      }
    } else if (typeof task.workspace !== 'string' || task.workspace.trim() === '') {
      throw new Error(`${label}.workspace must be a non-empty string when no fixture is given`)
    }
    if (!Number.isInteger(task.timeout_secs) || task.timeout_secs <= 0) {
      throw new Error(`${label}.timeout_secs must be a positive integer`)
    }
    if (ids.has(task.id)) {
      throw new Error(`duplicate task id: ${task.id}`)
    }
    ids.add(task.id)
  }
}

function selectTasks(tasks, filter) {
  if (!filter) {
    return tasks
  }
  const selected = tasks.filter((task) => task.id.includes(filter))
  if (selected.length === 0) {
    throw new Error(`no task matches filter: ${filter}`)
  }
  return selected
}

function expandWorkspace(workspace) {
  return workspace.replace(/\$\{([A-Za-z_][A-Za-z0-9_]*)\}/g, (_, name) => {
    const value = process.env[name]
    if (!value) {
      throw new Error(`environment variable ${name} is required by workspace ${workspace}`)
    }
    return value
  })
}

function tail(current, chunk) {
  return (current + chunk.toString()).slice(-maxTailChars)
}

function killTree(child) {
  if (!child.pid) {
    return
  }
  if (process.platform === 'win32') {
    spawnSync('taskkill', ['/PID', String(child.pid), '/T', '/F'], {
      stdio: 'ignore',
      windowsHide: true,
    })
    child.kill()
    return
  }
  try {
    process.kill(-child.pid, 'SIGKILL')
  } catch {
    child.kill('SIGKILL')
  }
}

function runProcess(file, args, cwd, timeoutSecs) {
  return new Promise((resolve) => {
    const startedAt = Date.now()
    let stdout = ''
    let stderr = ''
    let timedOut = false
    let settled = false

    let child
    try {
      const target = spawnTarget(file, args)
      child = spawn(target.file, target.args, {
        cwd,
        env: process.env,
        detached: process.platform !== 'win32',
        windowsHide: true,
        stdio: ['ignore', 'pipe', 'pipe'],
      })
    } catch (error) {
      resolve({
        ok: false,
        exit_code: null,
        duration_ms: Date.now() - startedAt,
        stdout_tail: stdout,
        stderr_tail: stderr,
        error: error.message,
      })
      return
    }

    child.stdout.on('data', (chunk) => {
      stdout = tail(stdout, chunk)
    })
    child.stderr.on('data', (chunk) => {
      stderr = tail(stderr, chunk)
    })

    const timer = setTimeout(() => {
      timedOut = true
      killTree(child)
    }, timeoutSecs * 1000)

    const finish = (exitCode, error) => {
      if (settled) {
        return
      }
      settled = true
      clearTimeout(timer)
      const result = {
        ok: !timedOut && exitCode === 0,
        exit_code: exitCode,
        duration_ms: Date.now() - startedAt,
        stdout_tail: stdout,
        stderr_tail: stderr,
      }
      if (timedOut) {
        result.timed_out = true
      }
      if (error) {
        result.error = error
      }
      resolve(result)
    }

    child.on('error', (error) => finish(null, error.message))
    child.on('close', (code, signal) => finish(code, signal ? `terminated by ${signal}` : undefined))
  })
}

function spawnTarget(file, args) {
  // Windows cannot spawn .cmd/.bat directly; cmd.exe can run them.
  if (process.platform === 'win32' && /\.(cmd|bat)$/i.test(file)) {
    return {
      file: 'cmd.exe',
      args: ['/d', '/c', 'call', file, ...args],
    }
  }
  return { file, args }
}

function prepareFixture(task, state, workspaceRoot) {
  const fixtureDir = path.join(fixtureRoot, task.fixture)
  const source = path.join(fixtureDir, state)
  if (!existsSync(source)) {
    throw new Error(`fixture ${task.fixture} has no ${state} state`)
  }
  const target = path.join(workspaceRoot, task.id)
  rmSync(target, { recursive: true, force: true })
  mkdirSync(path.dirname(target), { recursive: true })
  cpSync(source, target, { recursive: true })
  return { workspace: target, fixtureDir }
}

function resolveWorkspace(task, options, state) {
  if (task.fixture) {
    return prepareFixture(task, state, options.workspaceRoot)
  }
  const workspace = expandWorkspace(task.workspace)
  if (options.verifyOnly && !existsSync(workspace)) {
    throw new Error(`workspace does not exist: ${workspace}`)
  }
  return { workspace, fixtureDir: null }
}

function expandVerifyCommand(command, workspace, fixtureDir) {
  const expand = (part) => {
    let value = part
    if (fixtureDir) {
      value = value.replace(/\$\{FIXTURE_DIR\}/g, fixtureDir)
    }
    return value.replace(/\$\{WORKSPACE\}/g, workspace)
  }
  return Array.isArray(command) ? command.map(expand) : expand(command)
}

function splitCommand(command) {
  const parts = command.trim().split(/\s+/)
  return { file: parts[0], args: parts.slice(1) }
}

function isRunnableVerify(command) {
  const text = Array.isArray(command) ? command.join(' ') : command
  return !/^manual\s*:/i.test(text.trim())
}

async function runVerify(task, workspace, fixtureDir) {
  const command = expandVerifyCommand(task.verify, workspace, fixtureDir)
  if (!isRunnableVerify(task.verify)) {
    return {
      command,
      skipped: true,
      ok: false,
      exit_code: null,
      duration_ms: 0,
      stdout_tail: '',
      stderr_tail: '',
    }
  }
  const { file, args } = Array.isArray(command)
    ? { file: command[0], args: command.slice(1) }
    : splitCommand(command)
  const result = await runProcess(file, args, workspace, task.timeout_secs)
  return { command, ...result }
}

async function runTask(task, options) {
  const state = options.state ?? (options.verifyOnly ? 'solution' : 'initial')
  let workspace
  let fixtureDir
  try {
    ;({ workspace, fixtureDir } = resolveWorkspace(task, options, state))
  } catch (error) {
    return {
      id: task.id,
      ok: false,
      skipped: true,
      exit_code: null,
      duration_ms: 0,
      stdout_tail: '',
      stderr_tail: '',
      error: error.message,
      verify: {
        command: task.verify,
        skipped: true,
        ok: false,
        exit_code: null,
        duration_ms: 0,
        stdout_tail: '',
        stderr_tail: '',
      },
    }
  }

  if (options.verifyOnly) {
    const verify = await runVerify(task, workspace, fixtureDir)
    return {
      id: task.id,
      ok: verify.ok,
      skipped: verify.skipped === true,
      exit_code: verify.exit_code,
      duration_ms: verify.duration_ms,
      stdout_tail: verify.stdout_tail,
      stderr_tail: verify.stderr_tail,
      workspace,
      verify,
    }
  }

  const run = await runProcess(
    options.kl,
    ['run', task.prompt, '--workspace', workspace, '--yes'],
    root,
    task.timeout_secs,
  )
  const verify = await runVerify(task, workspace, fixtureDir)
  return {
    id: task.id,
    ok: verify.skipped === true ? run.ok : run.ok && verify.ok,
    exit_code: run.exit_code,
    duration_ms: run.duration_ms,
    stdout_tail: run.stdout_tail,
    stderr_tail: run.stderr_tail,
    workspace,
    verify,
  }
}

async function main() {
  const options = parseArgs(process.argv.slice(2))
  const tasks = JSON.parse(readFileSync(options.tasks, 'utf8').replace(/^\uFEFF/, ''))
  validateTasks(tasks)
  const selected = selectTasks(tasks, options.filter)

  if (options.dryRun) {
    console.log(`Validated ${tasks.length} benchmark tasks.`)
    console.log(`Selected ${selected.length} task(s) for this run.`)
    return
  }

  mkdirSync(path.dirname(path.resolve(options.out)), { recursive: true })
  writeFileSync(options.out, '')

  let failed = 0
  let skipped = 0
  for (const [index, task] of selected.entries()) {
    console.log(`[${index + 1}/${selected.length}] ${task.id}`)
    const result = await runTask(task, options)
    appendFileSync(options.out, `${JSON.stringify(result)}\n`)
    if (result.skipped) {
      skipped++
      console.log(`  SKIP ${result.error ?? 'skipped'}`)
      continue
    }
    if (!result.ok) {
      failed++
      console.log(`  FAIL exit=${result.exit_code ?? 'null'}`)
    } else {
      console.log(`  ok ${result.duration_ms}ms`)
    }
  }

  console.log(`Wrote ${selected.length} results to ${options.out}`)
  if (skipped > 0) {
    console.log(`Skipped ${skipped} task(s).`)
  }
  if (failed > 0) {
    process.exitCode = 1
  }
}

main().catch((error) => {
  console.error(error.message)
  process.exitCode = 1
})
