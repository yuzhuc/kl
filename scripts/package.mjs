import { spawnSync } from "node:child_process";
import { chmodSync, cpSync, existsSync, mkdirSync, rmSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { assertBinaryFormat, layoutFor } from "./package-format.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

function option(name, fallback = null) {
  const index = process.argv.indexOf(name);
  if (index < 0) return fallback;
  const value = process.argv[index + 1];
  if (!value || value.startsWith("--")) throw new Error(`missing value for ${name}`);
  return value;
}

const rid = option("--rid");
const rustTarget = option("--rust-target");
if (!rid || !rustTarget) {
  throw new Error("usage: node scripts/package.mjs --rid <rid> --rust-target <target> [--zig] [--output <dir>]");
}

const layout = layoutFor(rid);
const useZig = process.argv.includes("--zig");
const output = path.resolve(root, option("--output", path.join("dist", rid)));
const archive = path.join(root, "dist", `kl-${rid}${layout.ext}`);
const cargoTarget = path.resolve(root, process.env.CARGO_TARGET_DIR ?? path.join(root, "target"));

function run(command, args, cwd = root) {
  console.log(`\n== ${command} ${args.join(" ")} ==`);
  const result = spawnSync(command, args, { cwd, env: process.env, stdio: "inherit" });
  if (result.error) throw result.error;
  if (result.status !== 0) process.exit(result.status ?? 1);
}

rmSync(output, { recursive: true, force: true });
mkdirSync(output, { recursive: true });

run("cargo", [useZig ? "zigbuild" : "build", "--release", "--target", rustTarget, "-p", "kl-core"]);
run("dotnet", [
  "publish",
  "cli/Kl.Cli/Kl.Cli.csproj",
  "-c", "Release",
  "-r", rid,
  "--self-contained", "true",
  "-o", output,
]);

const coreName = layout.core;
const core = path.join(cargoTarget, rustTarget, "release", coreName);
if (!existsSync(core)) throw new Error(`core binary not found: ${core}`);
cpSync(core, path.join(output, coreName));
cpSync(path.join(root, "schemas"), path.join(output, "schemas"), { recursive: true });

if (!rid.startsWith("win")) {
  for (const name of ["kl", "kl-core"]) {
    const file = path.join(output, name);
    if (existsSync(file)) chmodSync(file, 0o755);
  }
}

if (rid.startsWith("win")) {
  run("tar", ["-a", "-c", "-f", archive, "-C", output, "."]);
} else {
  run("tar", ["-czf", archive, "-C", output, "."]);
}

assertBinaryFormat(path.join(output, layout.exe), rid);
assertBinaryFormat(path.join(output, layout.core), rid);

rmSync(`${archive}.sha256`, { force: true });

console.log(`\nPackaged ${output}\nArchive ${archive}`);
