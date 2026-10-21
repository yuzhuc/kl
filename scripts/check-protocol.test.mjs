import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

import {
  checkProtocol,
  extractCsharpMethods,
  extractRustMethods,
  extractSchemaRoots,
  schemaRootFor,
} from "./check-protocol.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const RUST_FILE = "crates/kl-protocol/src/lib.rs";
const SCHEMA_FILE = "schemas/protocol/v1/methods.schema.json";
const CSHARP_FILE = "sdk/Kl.Client/Protocol.cs";

const R3_METHODS = [
  "server.info",
  "session.attach",
  "session.detach",
  "session.lease",
];

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

test("the R3 methods are declared in Rust, the schema and C#", () => {
  const read = readRepo();
  const rustMethods = extractRustMethods(read(RUST_FILE));
  const schema = JSON.parse(read(SCHEMA_FILE));
  const schemaRoots = new Set(extractSchemaRoots(schema));
  const csharpMethods = new Set(extractCsharpMethods(read(CSHARP_FILE)));

  for (const method of R3_METHODS) {
    assert.ok(rustMethods.includes(method), `${method} missing from Rust`);
    assert.ok(csharpMethods.has(method), `${method} missing from C#`);
    assert.ok(
      schemaRoots.has(schemaRootFor(method)),
      `${method} missing from ${SCHEMA_FILE}`,
    );
  }
});

test("a C# constant missing for an R3 method is reported", () => {
  const read = (file) => {
    const text = readRepo()(file);
    if (file !== CSHARP_FILE) return text;
    return text.replace('    public const string SessionAttach = "session.attach";\n', "");
  };
  const result = checkProtocol(read);
  assert.ok(result.failures.length > 0);
  assert.ok(
    result.failures.some(
      (failure) =>
        failure.id === "method-missing-in-csharp" &&
        failure.detail.includes("session.attach"),
    ),
  );
});

test("a C# constant without a Rust method is reported", () => {
  const read = (file) => {
    const text = readRepo()(file);
    if (file !== CSHARP_FILE) return text;
    return text.replace(
      '    public const string Event = "event";',
      '    public const string Event = "event";\n    public const string Ghost = "session.ghost";',
    );
  };
  const result = checkProtocol(read);
  assert.ok(
    result.failures.some(
      (failure) =>
        failure.id === "csharp-method-unknown" &&
        failure.detail.includes("session.ghost"),
    ),
  );
});
