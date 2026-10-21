import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { checkProtocol } from "./check-protocol.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const RUST_FILE = "crates/kl-protocol/src/lib.rs";
const SCHEMA_FILE = "schemas/protocol/v1/methods.schema.json";

function readRepo() {
  return (file) => readFileSync(path.join(root, file), "utf8");
}

test("the Rust protocol matches the schema", () => {
  const result = checkProtocol(readRepo());
  assert.deepEqual(result.failures, []);
});

test("a method dropped from the schema is reported", () => {
  const read = (file) => {
    const text = readRepo()(file);
    if (file !== SCHEMA_FILE) return text;
    const schema = JSON.parse(text);
    delete schema.$defs.sessionCreateParams;
    return JSON.stringify(schema);
  };
  const result = checkProtocol(read);
  assert.ok(result.failures.length > 0);
  assert.ok(result.failures.some((failure) => failure.id === "method-unschematized"));
});

test("a method added to Rust without a schema entry is reported", () => {
  const read = (file) => {
    const text = readRepo()(file);
    if (file !== RUST_FILE) return text;
    return text.replace("pub mod methods {", 'pub mod methods {\n    pub const KL_TEST_ONLY: &str = "kl.test.only";');
  };
  const result = checkProtocol(read);
  assert.ok(result.failures.length > 0);
  assert.ok(result.failures.some((failure) => failure.id === "method-unschematized"));
});
