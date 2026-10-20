import { existsSync, readFileSync } from 'node:fs'
import path from 'node:path'

const root = process.argv[2] ?? process.cwd()
const answerPath = path.join(root, 'answer.md')

if (!existsSync(answerPath)) {
  console.error('answer.md was not created')
  process.exit(1)
}

const text = readFileSync(answerPath, 'utf8')
const problems = []

if (!/\b5\b/.test(text)) {
  problems.push('answer.md does not state the maximum retry count (5)')
}
if (!text.toLowerCase().includes('backoff')) {
  problems.push('answer.md does not describe the backoff rule')
}
if (!/\b200\b/.test(text)) {
  problems.push('answer.md does not state the base delay (200 ms)')
}

if (problems.length > 0) {
  console.error(problems.join('\n'))
  process.exit(1)
}

console.log('answer.md answers the retry policy question')
