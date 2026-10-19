import { spawn, spawnSync } from 'node:child_process'
import { appendFileSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const scriptDir = path.dirname(fileURLToPath(import.meta.url))
const root = path.resolve(scriptDir, '..')
const maxTailChars = 8192

function parseArgs(argv) {
  const options = {
    tasks: path.join(root, 'benchmarks', 'tasks.json'),
    kl: 'kl',
    out: path.join(root, 'benchmarks', 'results', 'latest.jsonl'),
    dryRun: false,
  }

  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i]
    if (arg === '--dry-run') {
      options.dryRun = true
      continue
    }
    if (!['--tasks', '--kl', '--out'].includes(arg)) {
      throw new Error(`unknown option: ${arg}`)
    }
    if (i + 1 >= argv.length) {
      throw new Error(`missing value for ${arg}`)
    }
    options[arg.slice(2)] = argv[++i]
  }

  return options
}

function validateTasks(tasks) {
  if (!Array.isArray(tasks)) {
    throw new Error('tasks file must contain a JSON array')
  }

  const ids = new Set()
  const fields = ['id', 'prompt', 'workspace', 'verify']
  for (const [index, task] of tasks.entries()) {
    const label = `task[${index}]`
    if (task === null || typeof task !== 'object' || Array.isArray(task)) {
      throw new Error(`${label} must be an object`)
    }
    for (const field of fields) {
      if (typeof task[field] !== 'string' || task[field].trim() === '') {
        throw new Error(`${label}.${field} must be a non-empty string`)
      }
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

function runTask(task, kl) {
  return new Promise((resolve) => {
    const startedAt = Date.now()
    let stdout = ''
    let stderr = ''
    let timedOut = false
    let settled = false

    let child
    try {
      child = spawn(
        kl,
        ['run', task.prompt, '--workspace', expandWorkspace(task.workspace), '--yes'],
        {
          cwd: root,
          env: process.env,
          detached: process.platform !== 'win32',
          windowsHide: true,
          stdio: ['ignore', 'pipe', 'pipe'],
        },
      )
    } catch (error) {
      resolve({
        id: task.id,
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
    }, task.timeout_secs * 1000)

    const finish = (exitCode, error) => {
      if (settled) {
        return
      }
      settled = true
      clearTimeout(timer)
      const result = {
        id: task.id,
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

async function main() {
  const options = parseArgs(process.argv.slice(2))
  const tasks = JSON.parse(readFileSync(options.tasks, 'utf8').replace(/^\uFEFF/, ''))
  validateTasks(tasks)

  if (options.dryRun) {
    console.log(`Validated ${tasks.length} benchmark tasks.`)
    return
  }

  mkdirSync(path.dirname(path.resolve(options.out)), { recursive: true })
  writeFileSync(options.out, '')

  let failed = 0
  for (const [index, task] of tasks.entries()) {
    console.log(`[${index + 1}/${tasks.length}] ${task.id}`)
    const result = await runTask(task, options.kl)
    appendFileSync(options.out, `${JSON.stringify(result)}\n`)
    if (!result.ok) {
      failed++
      console.log(`  FAIL exit=${result.exit_code ?? 'null'}`)
    } else {
      console.log(`  ok ${result.duration_ms}ms`)
    }
  }

  console.log(`Wrote ${tasks.length} results to ${options.out}`)
  if (failed > 0) {
    process.exitCode = 1
  }
}

main().catch((error) => {
  console.error(error.message)
  process.exitCode = 1
})