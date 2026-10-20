import test from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync, readdirSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import * as api from '../src/index.js'

const srcDir = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', 'src')

test('exports the new name', () => {
  assert.equal(api.TIMEOUT_MS, 5000)
  assert.equal(api.requestTimeout(), 5000)
})

test('old name is gone from every source file', () => {
  for (const file of readdirSync(srcDir)) {
    const text = readFileSync(path.join(srcDir, file), 'utf8')
    assert.ok(!text.includes('LEGACY_TIMEOUT'), `${file} still references LEGACY_TIMEOUT`)
  }
})
