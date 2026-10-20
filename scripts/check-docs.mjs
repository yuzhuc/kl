import { existsSync, readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

function must(read, file, pattern, note) {
  const text = read(file);
  if (text === undefined) return `${file}: file is missing`;
  if (!pattern.test(text)) return `${file}: expected ${note}`;
  return null;
}

function mustNot(read, file, pattern, note) {
  const text = read(file);
  if (text === undefined) return `${file}: file is missing`;
  if (pattern.test(text)) return `${file}: still claims ${note}`;
  return null;
}

const CHECKS = [
  {
    id: "pty",
    description: "Shell PTY is implemented through portable-pty",
    failures(read) {
      return [
        must(read, "README.md", /`tty=true` uses portable-pty/, "the portable-pty PTY note"),
        mustNot(read, "README.md", /add PTY/, "PTY as an upcoming feature"),
        must(read, "docs/architecture/overview.md", /Shell PTY is implemented/, "the PTY status line"),
        must(read, "crates/kl-tools/src/pty.rs", /portable_pty/, "the portable-pty implementation"),
        must(read, "crates/kl-tools/Cargo.toml", /portable-pty/, "the portable-pty dependency"),
      ].filter(Boolean);
    },
  },
  {
    id: "sandbox-backends",
    description: "Linux bubblewrap and macOS Seatbelt backends exist",
    failures(read) {
      return [
        must(read, "README.md", /`bubblewrap`/, "the bubblewrap backend"),
        must(read, "README.md", /Seatbelt/, "the Seatbelt backend"),
        must(read, "docs/security/policy.md", /bubblewrap/, "the bubblewrap backend"),
        must(read, "docs/security/policy.md", /Seatbelt/, "the Seatbelt backend"),
        must(read, "docs/architecture/overview.md", /bubblewrap/, "the bubblewrap backend"),
        must(read, "docs/architecture/overview.md", /Seatbelt/, "the Seatbelt backend"),
        must(read, "crates/kl-sandbox/src/linux.rs", /bubblewrap/, "the bubblewrap backend"),
        must(read, "crates/kl-sandbox/src/macos.rs", /Seatbelt/, "the Seatbelt backend"),
      ].filter(Boolean);
    },
  },
  {
    id: "mcp-sandbox",
    description: "MCP server processes start through the sandbox",
    failures(read) {
      return [
        must(read, "README.md", /MCP server processes start through the active `SandboxManager`/, "the sandboxed MCP note"),
        must(read, "docs/security/policy.md", /start through the active sandbox/, "the sandboxed MCP note"),
        must(read, "crates/kl-core/src/mcp.rs", /sandbox\.spawn\(/, "the sandboxed MCP spawn"),
      ].filter(Boolean);
    },
  },
  {
    id: "allow-session",
    description: "Approvals support a session-scoped allow rule",
    failures(read) {
      return [
        must(read, "README.md", /allow for this session/, "the session approval note"),
        must(read, "docs/security/policy.md", /`allow_session`/, "the allow_session decision"),
        must(read, "tui/Kl.Tui/SessionUi.cs", /allow_session/, "the allow_session decision"),
      ].filter(Boolean);
    },
  },
  {
    id: "benchmark-tasks",
    description: "The benchmark harness keeps 23 tasks",
    failures(read) {
      const text = read("benchmarks/tasks.json");
      if (text === undefined) return ["benchmarks/tasks.json: file is missing"];
      let tasks;
      try {
        tasks = JSON.parse(text);
      } catch (error) {
        return [`benchmarks/tasks.json: invalid JSON (${error.message})`];
      }
      const problems = [];
      if (!Array.isArray(tasks)) problems.push("benchmarks/tasks.json: expected a JSON array");
      else if (tasks.length !== 23) problems.push(`benchmarks/tasks.json: expected 23 tasks, found ${tasks.length}`);
      problems.push(must(read, "README.md", /23-task acceptance harness/, "the 23-task harness note"));
      return problems.filter(Boolean);
    },
  },
  {
    id: "release-policy",
    description: "Releases are built only from main and tagged v<version>-relN",
    failures(read) {
      const problems = [
        must(read, "README.md", /Releases are built only from pushes to `main`/, "the main-only release policy"),
        must(read, "README.md", /`v<version>-rel1`/, "the v<version>-relN tag scheme"),
        must(read, "README.md", /current `v<major>\.<minor>\.` series/, "the current-series retention rule"),
        mustNot(read, "README.md", /retention list has\s+not been chosen/, "an undecided retention list"),
        must(read, ".github/workflows/release.yml", /branches: \[main\]/, "a main-only push trigger"),
        must(read, ".github/workflows/release.yml", /-rel\$\{rel\}/, "the -relN tag suffix"),
        mustNot(read, ".github/workflows/release.yml", /pull_request/, "pull request triggers"),
        mustNot(read, ".github/workflows/release.yml", /workflow_dispatch/, "manual dispatch triggers"),
      ].filter(Boolean);
      const workflow = read(".github/workflows/release.yml");
      if (workflow !== undefined) {
        const branches = workflow.match(/branches:/g) ?? [];
        if (branches.length !== 1) {
          problems.push(`.github/workflows/release.yml: expected 1 branch trigger, found ${branches.length}`);
        }
      }
      return problems;
    },
  },
  {
    id: "no-sha-files",
    description: "Releases ship archives only, without .sha256 files",
    failures(read) {
      return [
        must(read, "README.md", /without `\.sha256` files/, "the archive-only release note"),
        must(read, "scripts/package.mjs", /rmSync\(`\$\{archive\}\.sha256`/, "the .sha256 cleanup"),
        mustNot(read, ".github/workflows/release.yml", /sha256/, "checksum artifacts"),
      ].filter(Boolean);
    },
  },
  {
    id: "release-matrix",
    description: "The release RID matrix is identical in the workflow, README and packer",
    failures(read) {
      const workflow = read(".github/workflows/release.yml");
      const readme = read("README.md");
      const packer = read("scripts/package-format.mjs");
      if (workflow === undefined || readme === undefined || packer === undefined) {
        return ["release matrix: file is missing"];
      }
      const ridPattern = /\b(?:win|linux|osx)-[a-z0-9]+\b/g;
      const workflowRids = [...workflow.matchAll(/^\s+(?:- )?rid: (\S+)$/gm)].map((match) => match[1]);
      const readmeBlock = readme.match(/current release matrix is:[\s\S]*?```text([\s\S]*?)```/);
      const readmeRids = readmeBlock === null ? [] : [...readmeBlock[1].matchAll(ridPattern)].map((m) => m[0]);
      const packerRids = [...packer.matchAll(/^\s+"([a-z0-9-]+)": \{ kind:/gm)].map((match) => match[1]);
      const problems = [];
      for (const [label, rids] of [
        [".github/workflows/release.yml", workflowRids],
        ["README.md", readmeRids],
        ["scripts/package-format.mjs", packerRids],
      ]) {
        if (rids.length === 0) problems.push(`${label}: expected a release RID list`);
      }
      const unique = (rids) => [...new Set(rids)].sort();
      const expected = unique(workflowRids);
      if (expected.length > 0) {
        for (const [label, rids] of [
          ["README.md", readmeRids],
          ["scripts/package-format.mjs", packerRids],
        ]) {
          const actual = unique(rids);
          if (actual.join(",") !== expected.join(",")) {
            problems.push(`${label}: expected RIDs [${expected.join(", ")}], found [${actual.join(", ")}]`);
          }
        }
      }
      return problems;
    },
  },
];

export function checkDocs(read) {
  const failures = [];
  for (const check of CHECKS) {
    const details = check.failures(read);
    if (details.length > 0) failures.push({ id: check.id, description: check.description, details });
  }
  return { total: CHECKS.length, failures };
}

function readFromDisk(file) {
  const full = path.join(root, file);
  return existsSync(full) ? readFileSync(full, "utf8") : undefined;
}

function main() {
  const result = checkDocs(readFromDisk);
  for (const failure of result.failures) {
    console.error(`docs check failed: ${failure.id} (${failure.description})`);
    for (const detail of failure.details) console.error(`  - ${detail}`);
  }
  if (result.failures.length > 0) {
    console.error(`docs check: ${result.failures.length} of ${result.total} claims regressed`);
    process.exit(1);
  }
  console.log(`docs check: ${result.total} claims verified`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) main();
