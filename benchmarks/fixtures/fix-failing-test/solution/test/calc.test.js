import test from 'node:test'
import assert from 'node:assert/strict'
import { average } from '../src/calc.js'

test('average of a non-empty list', () => {
  assert.equal(average([1, 2, 3]), 2)
})

test('average of an empty list is zero', () => {
  assert.equal(average([]), 0)
})
