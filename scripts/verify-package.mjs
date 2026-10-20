import { spawnSync } from "node:child_process";
import { existsSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { assertBinaryFormat, isDiagnosticFile, layoutFor } from "./package-format.mjs";

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
const problems = [];

function requireFile(file, label) {
  if (!existsSync(file)) {
    problems.push(`missing ${label}: ${file}`);
    return false;
  }
  return true;
}

requireFile(archive, "archive");

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

if (existsSync(archive)) {
  const listing = spawnSync("tar", ["-tf", archive], { encoding: "utf8" });
  if (listing.error || listing.status !== 0) {
    problems.push(`cannot list archive: ${listing.error?.message ?? listing.stderr?.trim()}`);
  } else {
    for (const entry of listing.stdout.split("\n").map((line) => line.trim()).filter(Boolean)) {
      if (entry === "." || entry.startsWith("./")) {
        problems.push(`archive entry must not use a './' prefix: ${entry}`);
      }
      const name = entry.split("/").pop();
      if (isDiagnosticFile(name)) {
        problems.push(`archive contains a diagnostic file: ${entry}`);
      }
    }
  }
}

if (problems.length > 0) {
  for (const problem of problems) console.error(`error: ${problem}`);
  process.exit(1);
}

console.log(`verified ${rid}: ${archive}`);
