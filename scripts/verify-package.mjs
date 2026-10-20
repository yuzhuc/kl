import { existsSync, readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { assertBinaryFormat, layoutFor, sha256File } from "./package-format.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

function option(name, fallback = null) {
  const index = process.argv.indexOf(name);
  if (index < 0) return fallback;
  const value = process.argv[index + 1];
  if (!value || value.startsWith("--")) throw new Error(`missing value for ${name}`);
  return value;
}

const rid = option("--rid");
if (!rid) throw new Error("usage: node scripts/verify-package.mjs --rid <rid> [--output <dir>]");

const layout = layoutFor(rid);
const output = path.resolve(root, option("--output", path.join("dist", rid)));
const archive = path.join(root, "dist", `kl-${rid}${layout.ext}`);
const checksum = `${archive}.sha256`;
const problems = [];

function requireFile(file, label) {
  if (!existsSync(file)) {
    problems.push(`missing ${label}: ${file}`);
    return false;
  }
  return true;
}

if (requireFile(archive, "archive") && requireFile(checksum, "checksum file")) {
  const expected = readFileSync(checksum, "utf8").trim().split(/\s+/)[0];
  const actual = await sha256File(archive);
  if (expected !== actual) problems.push(`checksum mismatch for ${archive}: ${expected} != ${actual}`);
}

for (const [file, label] of [
  [path.join(output, layout.exe), "cli executable"],
  [path.join(output, layout.core), "core executable"],
]) {
  if (!requireFile(file, label)) continue;
  try {
    assertBinaryFormat(file, rid);
  } catch (error) {
    problems.push(error.message);
  }
}

if (problems.length > 0) {
  for (const problem of problems) console.error(`error: ${problem}`);
  process.exit(1);
}

console.log(`verified ${rid}: ${archive}`);
