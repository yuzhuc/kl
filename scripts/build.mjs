import { spawnSync } from 'node:child_process'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')

function run(command, args) {
  console.log(`\n== ${command} ${args.join(' ')} ==`)
  const result = spawnSync(command, args, {
    cwd: root,
    env: process.env,
    stdio: 'inherit',
  })

  if (result.error) {
    console.error(`\n${command} could not start: ${result.error.message}`)
    process.exit(1)
  }
  if (result.status !== 0) {
    process.exit(result.status ?? 1)
  }
}

run('cargo', ['fmt', '--all', '--', '--check'])
run('cargo', ['clippy', '--workspace', '--all-targets', '--', '-D', 'warnings'])
run('cargo', ['test', '--workspace'])
run('cargo', ['build', '--workspace'])
run('dotnet', ['build', 'kl.sln'])
run('dotnet', ['test', 'kl.sln', '--no-build'])

console.log('\nAll checks passed.')