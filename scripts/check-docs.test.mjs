import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { checkDocs } from "./check-docs.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

function readRepo() {
  return (file) => readFileSync(path.join(root, file), "utf8");
}

test("the current docs satisfy every claim", () => {
  const result = checkDocs(readRepo());
  assert.deepEqual(result.failures, []);
});

test("a regressed claim is reported", () => {
  const read = (file) => {
    const text = readRepo()(file);
    return file === "README.md" ? text.replace("`tty=true` uses portable-pty", "tty is unsupported") : text;
  };
  const result = checkDocs(read);
  assert.equal(result.failures.length, 1);
  assert.equal(result.failures[0].id, "pty");
  assert.match(result.failures[0].details[0], /README\.md: expected the portable-pty PTY note/);
});

test("a missing file is reported", () => {
  const read = (file) => (file === "README.md" ? undefined : readRepo()(file));
  const result = checkDocs(read);
  assert.ok(result.failures.length > 0);
  assert.ok(result.failures.every((failure) => failure.details.some((detail) => detail.includes("file is missing"))));
});
