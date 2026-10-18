# ADR 0010: lightweight repository index and test selection

- Status: accepted
- Date: 2026-10-18

## Context

Deep mode starts with an explorer that has no orientation beyond the task text
and the workspace tools. Reading every file is slow, and re-reading unchanged
files across turns is wasteful. A full symbol graph, type checker, language
server, or cross-language semantic analysis is out of scope for this stage.

## Decision

- Add a small kl-index crate. It is a hint source for deep mode, not a source
  of truth and not a second agent runtime.
- Discover files with git ls-files --cached --others --exclude-standard so
  .gitignore is respected and untracked, non-ignored files are included. When
  git is unavailable, fall back to a bounded directory walk that skips common
  build directories and simple root-level .gitignore names.
- Record a repo-relative path, canonical language name, line count, last
  modification time, and a shallow symbol list. Symbols are limited to Rust,
  C#, and TypeScript; every other language is file-level only.
- Symbol extraction is line-oriented and heuristic. It recognizes Rust
  functions, structs, enums, traits, unions and type aliases; C# classes,
  interfaces, structs, records, enums and methods; TypeScript functions,
  classes, interfaces, enums, type aliases, arrow functions, and methods.
- Expose queries for path substring, symbol substring, language, recent
  modification, and git-changed files. Query results are ordered by
  modification time.
- Select tests by mapping each changed source file to a same-directory or
  same-module test file whose normalized stem matches. When no mapping exists,
  fall back to the project test command (cargo test, npm test, or dotnet test).
- Append a bounded index summary to the deep explorer and planner prompts.
  Reviewer and verifier prompts and tool-access boundaries are unchanged.
- Cache the index as versioned JSON outside the workspace, under
  ~/.kl/cache/repo-index or KL_INDEX_CACHE_DIR. The cache stores a manifest of
  path, size and modification time. Any mismatch rebuilds the index. Cache
  read/write failures are non-fatal. The index never writes into the workspace.

## Consequences

- Deep mode gets useful file, symbol and test hints without an LSP or semantic
  index, but the hints can be stale or incomplete and must be verified with
  read_file, grep, or glob.
- Cache invalidation is metadata-based. A content change that preserves both
  size and modification time could be missed until the next rebuild or
  invalidation.
- Languages outside Rust, C# and TypeScript remain searchable by path and
  language but contribute no symbols.
- Symbol parsing is intentionally conservative. Macros, generated members,
  partial classes and dynamic constructs may be missed.
