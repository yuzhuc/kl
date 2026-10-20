import { spawnSync } from 'node:child_process'

const root = process.argv[2] ?? process.cwd()
const result = spawnSync(process.execPath, ['--test'], {
  cwd: root,
  encoding: 'utf8',
})

process.stdout.write(result.stdout ?? '')
process.stderr.write(result.stderr ?? '')
process.exit(result.status ?? 1)
