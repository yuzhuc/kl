//! Lightweight repository index for KL deep mode.
//!
//! The index is intentionally shallow: it records file metadata and a small
//! set of declaration symbols. It is not a symbol graph, type checker, or LSP.

mod symbols;

use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet, HashSet};
use std::fmt::Write as _;
use std::fs;
use std::io;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

use symbols::{language_for, parse_symbols};

const CACHE_VERSION: u32 = 1;
const MAX_PARSE_BYTES: u64 = 512 * 1024;
const DEFAULT_LIMIT: usize = 50;
static CACHE_TEMP_SEQUENCE: AtomicU64 = AtomicU64::new(0);

#[derive(Debug, thiserror::Error)]
pub enum IndexError {
    #[error("workspace {path} is not a directory")]
    NotDirectory { path: String },
    #[error("failed to read {path}: {source}")]
    Io { path: String, source: io::Error },
    #[error("git command failed in {root}: {message}")]
    Git { root: String, message: String },
    #[error("failed to serialize index cache: {0}")]
    Cache(String),
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Symbol {
    pub name: String,
    pub kind: String,
    pub line: u64,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct IndexedFile {
    pub path: String,
    pub language: String,
    pub lines: u64,
    pub symbols: Vec<Symbol>,
    pub modified_ns: u64,
}

#[derive(Debug, Clone, Default)]
pub struct IndexQuery {
    pub path: Option<String>,
    pub symbol: Option<String>,
    pub language: Option<String>,
    pub recent_days: Option<u64>,
    pub changed_only: bool,
    pub limit: usize,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TestPlan {
    pub tests: Vec<String>,
    pub fallback_command: Option<String>,
    pub reason: String,
}

#[derive(Debug, Clone)]
pub struct RepoIndex {
    root: PathBuf,
    files: Vec<IndexedFile>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
struct ManifestEntry {
    path: String,
    size: u64,
    modified_ns: u64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct CacheFile {
    version: u32,
    root: String,
    manifest: Vec<ManifestEntry>,
    files: Vec<IndexedFile>,
}

impl RepoIndex {
    pub fn load(root: &Path) -> Result<Self, IndexError> {
        Self::load_with_cache(root, None)
    }

    pub fn load_with_cache(root: &Path, cache_dir: Option<&Path>) -> Result<Self, IndexError> {
        let root = canonical_root(root)?;
        let manifest = manifest(&root)?;
        if let Some(path) = cache_path_for(&root, cache_dir) {
            if let Ok(bytes) = fs::read(&path) {
                if let Ok(cache) = serde_json::from_slice::<CacheFile>(&bytes) {
                    if cache.version == CACHE_VERSION
                        && cache.root == root_key(&root)
                        && cache.manifest == manifest
                    {
                        return Ok(Self {
                            root,
                            files: cache.files,
                        });
                    }
                }
            }
        }
        let index = Self {
            files: build_files(&root, &manifest),
            root,
        };
        if let Some(path) = cache_path_for(&index.root, cache_dir) {
            let _ = write_cache(&path, &index, &manifest);
        }
        Ok(index)
    }

    pub fn build(root: &Path) -> Result<Self, IndexError> {
        let root = canonical_root(root)?;
        let manifest = manifest(&root)?;
        Ok(Self {
            files: build_files(&root, &manifest),
            root,
        })
    }

    pub fn rebuild_with_cache(root: &Path, cache_dir: Option<&Path>) -> Result<Self, IndexError> {
        let root = canonical_root(root)?;
        let manifest = manifest(&root)?;
        let index = Self {
            files: build_files(&root, &manifest),
            root,
        };
        if let Some(path) = cache_path_for(&index.root, cache_dir) {
            let _ = write_cache(&path, &index, &manifest);
        }
        Ok(index)
    }

    pub fn invalidate(root: &Path) -> Result<bool, IndexError> {
        Self::invalidate_with(root, None)
    }

    pub fn invalidate_with(root: &Path, cache_dir: Option<&Path>) -> Result<bool, IndexError> {
        let root = canonical_root(root)?;
        let Some(path) = cache_path_for(&root, cache_dir) else {
            return Ok(false);
        };
        if !path.exists() {
            return Ok(false);
        }
        fs::remove_file(&path).map_err(|source| io_error(&path, source))?;
        Ok(true)
    }

    pub fn cache_path(root: &Path) -> Result<PathBuf, IndexError> {
        Self::cache_path_with(root, None)
    }

    pub fn cache_path_with(root: &Path, cache_dir: Option<&Path>) -> Result<PathBuf, IndexError> {
        let root = canonical_root(root)?;
        cache_path_for(&root, cache_dir)
            .ok_or_else(|| IndexError::Cache("no cache directory available".to_owned()))
    }

    pub fn root(&self) -> &Path {
        &self.root
    }

    pub fn files(&self) -> &[IndexedFile] {
        &self.files
    }

    pub fn file(&self, path: &str) -> Option<&IndexedFile> {
        self.files.iter().find(|file| file.path == path)
    }

    pub fn search(&self, query: &IndexQuery) -> Result<Vec<&IndexedFile>, IndexError> {
        let changed: HashSet<String> = if query.changed_only {
            self.changed_files()?.into_iter().collect()
        } else {
            HashSet::new()
        };
        let cutoff = query
            .recent_days
            .map(|days| now_ns().saturating_sub(days.saturating_mul(24 * 60 * 60 * 1_000_000_000)));
        let path = query.path.as_deref().map(str::to_lowercase);
        let symbol = query.symbol.as_deref().map(str::to_lowercase);
        let language = query.language.as_deref().map(str::to_lowercase);
        let mut files: Vec<&IndexedFile> = self
            .files
            .iter()
            .filter(|file| {
                if let Some(path) = &path {
                    if !file.path.to_lowercase().contains(path) {
                        return false;
                    }
                }
                if let Some(symbol) = &symbol {
                    if !file
                        .symbols
                        .iter()
                        .any(|candidate| candidate.name.to_lowercase().contains(symbol))
                    {
                        return false;
                    }
                }
                if let Some(language) = &language {
                    if !file.language.eq_ignore_ascii_case(language) {
                        return false;
                    }
                }
                if let Some(cutoff) = cutoff {
                    if file.modified_ns < cutoff {
                        return false;
                    }
                }
                if query.changed_only && !changed.contains(&file.path) {
                    return false;
                }
                true
            })
            .collect();
        files.sort_by(|left, right| {
            right
                .modified_ns
                .cmp(&left.modified_ns)
                .then_with(|| left.path.cmp(&right.path))
        });
        files.truncate(if query.limit == 0 {
            DEFAULT_LIMIT
        } else {
            query.limit
        });
        Ok(files)
    }

    pub fn changed_files(&self) -> Result<Vec<String>, IndexError> {
        let mut changed = BTreeSet::new();
        let mut any_success = false;
        for args in [
            &["diff", "--name-only", "-z", "--", "."][..],
            &["diff", "--cached", "--name-only", "-z", "--", "."][..],
            &[
                "ls-files",
                "--others",
                "--exclude-standard",
                "-z",
                "--",
                ".",
            ][..],
        ] {
            if let Some(output) = git_output(&self.root, args) {
                any_success = true;
                for path in String::from_utf8_lossy(&output)
                    .split('\0')
                    .filter(|path| !path.is_empty())
                {
                    changed.insert(normalize_path(path));
                }
            }
        }
        if !any_success {
            return Err(IndexError::Git {
                root: self.root.display().to_string(),
                message: "git diff/ls-files failed".to_owned(),
            });
        }
        Ok(changed.into_iter().collect())
    }

    pub fn test_files(&self) -> Vec<&IndexedFile> {
        self.files
            .iter()
            .filter(|file| is_test_path(&file.path))
            .collect()
    }

    pub fn select_tests(&self, changed: &[String]) -> TestPlan {
        let mut selected = BTreeSet::new();
        for source in changed {
            if is_test_path(source) {
                selected.insert(source.clone());
                continue;
            }
            let source_stem = file_stem(source);
            for test in self.test_files() {
                if same_module(source, &test.path)
                    && stems_match(&source_stem, &file_stem(&test.path))
                {
                    selected.insert(test.path.clone());
                }
            }
        }
        if selected.is_empty() {
            let fallback = self.project_test_command();
            let reason = match &fallback {
                Some(command) => format!("no same-module test mapping; fallback to {command}"),
                None => "no same-module test mapping or project test command".to_owned(),
            };
            return TestPlan {
                tests: Vec::new(),
                fallback_command: fallback,
                reason,
            };
        }
        let tests: Vec<String> = selected.into_iter().collect();
        TestPlan {
            reason: format!(
                "mapped {} changed file(s) to {} same-module test file(s)",
                changed.len(),
                tests.len()
            ),
            tests,
            fallback_command: None,
        }
    }

    pub fn prompt_context(&self, task: &str, changed: &[String]) -> String {
        let symbol_count: usize = self.files.iter().map(|file| file.symbols.len()).sum();
        let mut output = String::new();
        let _ = writeln!(
            output,
            "{} files, {} symbols.",
            self.files.len(),
            symbol_count
        );
        let mut languages: BTreeMap<&str, usize> = BTreeMap::new();
        for file in &self.files {
            *languages.entry(file.language.as_str()).or_insert(0) += 1;
        }
        let mut languages: Vec<(&str, usize)> = languages.into_iter().collect();
        languages.sort_by(|left, right| right.1.cmp(&left.1).then_with(|| left.0.cmp(right.0)));
        let mix = languages
            .iter()
            .take(6)
            .map(|(language, count)| format!("{language} {count}"))
            .collect::<Vec<_>>()
            .join(", ");
        if mix.is_empty() {
            let _ = writeln!(output, "Languages: none.");
        } else {
            let _ = writeln!(output, "Languages: {mix}.");
        }
        if changed.is_empty() {
            let _ = writeln!(output, "Changed files: none detected.");
        } else {
            let list = changed
                .iter()
                .take(8)
                .cloned()
                .collect::<Vec<_>>()
                .join(", ");
            let _ = writeln!(output, "Changed files: {list}.");
        }
        let _ = writeln!(output, "Likely files:");
        let relevant = self.relevant_files(task, changed, 8);
        for (_, file) in &relevant {
            let mut line = format!("- {} ({}; {} lines", file.path, file.language, file.lines);
            if changed.iter().any(|path| path == &file.path) {
                line.push_str("; changed");
            }
            if is_test_path(&file.path) {
                line.push_str("; test");
            }
            line.push(')');
            if !file.symbols.is_empty() {
                let names: Vec<&str> = file
                    .symbols
                    .iter()
                    .take(4)
                    .map(|symbol| symbol.name.as_str())
                    .collect();
                let extra = file.symbols.len().saturating_sub(names.len());
                let mut list = names.join(", ");
                if extra > 0 {
                    let _ = write!(list, " +{extra}");
                }
                let _ = write!(line, " symbols: {list}");
            }
            let _ = writeln!(output, "{line}");
        }
        let test_sources: Vec<String> = if changed.is_empty() {
            relevant.iter().map(|(_, file)| file.path.clone()).collect()
        } else {
            changed.to_vec()
        };
        let plan = self.select_tests(&test_sources);
        if plan.tests.is_empty() {
            let _ = writeln!(output, "Tests: {}", plan.reason);
            if let Some(command) = plan.fallback_command {
                let _ = writeln!(output, "Fallback test command: {command}");
            }
        } else {
            let list = plan
                .tests
                .iter()
                .take(8)
                .cloned()
                .collect::<Vec<_>>()
                .join(", ");
            let _ = writeln!(output, "Tests: {list}.");
        }
        let _ = writeln!(
            output,
            "These are orientation hints; verify with read_file, grep or glob before relying on them."
        );
        output
    }

    fn relevant_files<'a>(
        &'a self,
        task: &str,
        changed: &[String],
        limit: usize,
    ) -> Vec<(i32, &'a IndexedFile)> {
        let keywords = task_keywords(task);
        let changed_set: HashSet<&str> = changed.iter().map(String::as_str).collect();
        let mut scored: Vec<(i32, &IndexedFile)> = self
            .files
            .iter()
            .map(|file| {
                let mut score = 0;
                if changed_set.contains(file.path.as_str()) {
                    score += 20;
                }
                if is_test_path(&file.path) {
                    score += 2;
                }
                let lower_path = file.path.to_lowercase();
                for keyword in &keywords {
                    if lower_path.contains(keyword) {
                        score += 3;
                    }
                    for symbol in &file.symbols {
                        let name = symbol.name.to_lowercase();
                        if name == *keyword {
                            score += 8;
                        } else if name.contains(keyword) {
                            score += 4;
                        }
                    }
                }
                (score, file)
            })
            .collect();
        if scored.iter().any(|(score, _)| *score > 2) {
            scored.retain(|(score, _)| *score > 2);
        }
        scored.sort_by(|left, right| {
            right
                .0
                .cmp(&left.0)
                .then_with(|| right.1.modified_ns.cmp(&left.1.modified_ns))
                .then_with(|| left.1.path.cmp(&right.1.path))
        });
        scored.truncate(limit);
        scored
    }

    fn project_test_command(&self) -> Option<String> {
        if self.files.iter().any(|file| file.path == "Cargo.toml") {
            return Some("cargo test".to_owned());
        }
        if self.files.iter().any(|file| file.path == "package.json") {
            return Some("npm test".to_owned());
        }
        if self
            .files
            .iter()
            .any(|file| file.path.ends_with(".sln") || file.path.ends_with(".csproj"))
        {
            return Some("dotnet test".to_owned());
        }
        None
    }
}

fn canonical_root(root: &Path) -> Result<PathBuf, IndexError> {
    let canonical = dunce::canonicalize(root).map_err(|source| io_error(root, source))?;
    if !canonical.is_dir() {
        return Err(IndexError::NotDirectory {
            path: root.display().to_string(),
        });
    }
    Ok(canonical)
}

fn io_error(path: &Path, source: io::Error) -> IndexError {
    IndexError::Io {
        path: path.display().to_string(),
        source,
    }
}

fn root_key(root: &Path) -> String {
    root.to_string_lossy().replace('\\', "/")
}

fn cache_path_for(root: &Path, cache_dir: Option<&Path>) -> Option<PathBuf> {
    let directory = match cache_dir {
        Some(directory) => directory.to_path_buf(),
        None => default_cache_dir()?,
    };
    let key = fnv1a(&root_key(root));
    Some(directory.join(format!("{key:016x}.json")))
}

fn default_cache_dir() -> Option<PathBuf> {
    if let Some(directory) = std::env::var_os("KL_INDEX_CACHE_DIR") {
        return Some(PathBuf::from(directory));
    }
    let home = std::env::var_os("USERPROFILE").or_else(|| std::env::var_os("HOME"))?;
    Some(
        PathBuf::from(home)
            .join(".kl")
            .join("cache")
            .join("repo-index"),
    )
}

fn fnv1a(text: &str) -> u64 {
    let mut hash = 0xcbf2_9ce4_8422_2325_u64;
    for byte in text.as_bytes() {
        hash ^= u64::from(*byte);
        hash = hash.wrapping_mul(0x0000_0100_0000_01b3);
    }
    hash
}

fn manifest(root: &Path) -> Result<Vec<ManifestEntry>, IndexError> {
    let mut entries = Vec::new();
    for path in collect_paths(root) {
        let absolute = join_relative(root, &path);
        let metadata = match fs::metadata(&absolute) {
            Ok(metadata) if metadata.is_file() => metadata,
            Ok(_) => continue,
            Err(_) => continue,
        };
        entries.push(ManifestEntry {
            path,
            size: metadata.len(),
            modified_ns: modified_ns(&metadata),
        });
    }
    entries.sort_by(|left, right| left.path.cmp(&right.path));
    Ok(entries)
}

fn build_files(root: &Path, manifest: &[ManifestEntry]) -> Vec<IndexedFile> {
    manifest
        .iter()
        .map(|entry| index_file(&join_relative(root, &entry.path), entry))
        .collect()
}

fn index_file(absolute: &Path, entry: &ManifestEntry) -> IndexedFile {
    let language = language_for(&entry.path).to_owned();
    let bytes = match fs::read(absolute) {
        Ok(bytes) => bytes,
        Err(_) => {
            return IndexedFile {
                path: entry.path.clone(),
                language,
                lines: 0,
                symbols: Vec::new(),
                modified_ns: entry.modified_ns,
            };
        }
    };
    let lines = count_lines(&bytes);
    let binary = bytes.iter().take(8192).any(|byte| *byte == 0);
    let symbols = if binary || bytes.len() as u64 > MAX_PARSE_BYTES {
        Vec::new()
    } else {
        let text = String::from_utf8_lossy(&bytes);
        parse_symbols(&language, &text)
    };
    IndexedFile {
        path: entry.path.clone(),
        language,
        lines,
        symbols,
        modified_ns: entry.modified_ns,
    }
}

fn count_lines(bytes: &[u8]) -> u64 {
    if bytes.is_empty() {
        return 0;
    }
    let newlines = bytes.iter().filter(|byte| **byte == b'\n').count() as u64;
    if bytes.last() == Some(&b'\n') {
        newlines
    } else {
        newlines + 1
    }
}

fn modified_ns(metadata: &fs::Metadata) -> u64 {
    metadata
        .modified()
        .ok()
        .and_then(|time| time.duration_since(UNIX_EPOCH).ok())
        .map(|duration| u64::try_from(duration.as_nanos()).unwrap_or(u64::MAX))
        .unwrap_or(0)
}

fn now_ns() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| u64::try_from(duration.as_nanos()).unwrap_or(u64::MAX))
        .unwrap_or(0)
}

fn collect_paths(root: &Path) -> Vec<String> {
    if let Some(output) = git_output(
        root,
        &[
            "ls-files",
            "--cached",
            "--others",
            "--exclude-standard",
            "-z",
            "--",
            ".",
        ],
    ) {
        let mut paths: Vec<String> = String::from_utf8_lossy(&output)
            .split('\0')
            .filter(|path| !path.is_empty())
            .map(normalize_path)
            .collect();
        paths.sort();
        paths.dedup();
        return paths;
    }
    walk_paths(root)
}

fn git_output(root: &Path, args: &[&str]) -> Option<Vec<u8>> {
    let output = std::process::Command::new("git")
        .arg("-C")
        .arg(root)
        .args(args)
        .output()
        .ok()?;
    output.status.success().then_some(output.stdout)
}

fn walk_paths(root: &Path) -> Vec<String> {
    let ignored = root_ignore_names(root);
    let mut paths = Vec::new();
    walk_dir(root, root, &ignored, &mut paths);
    paths.sort();
    paths.dedup();
    paths
}

fn walk_dir(root: &Path, directory: &Path, ignored: &HashSet<String>, paths: &mut Vec<String>) {
    let Ok(entries) = fs::read_dir(directory) else {
        return;
    };
    let mut entries: Vec<_> = entries.flatten().collect();
    entries.sort_by_key(|entry| entry.file_name());
    for entry in entries {
        let name = entry.file_name().to_string_lossy().to_string();
        let file_type = match entry.file_type() {
            Ok(file_type) => file_type,
            Err(_) => continue,
        };
        if file_type.is_dir() {
            if hardcoded_ignored(&name) || ignored.contains(&name) {
                continue;
            }
            walk_dir(root, &entry.path(), ignored, paths);
        } else if file_type.is_file() {
            if let Ok(relative) = entry.path().strip_prefix(root) {
                paths.push(normalize_path(&relative.to_string_lossy()));
            }
        }
    }
}

fn hardcoded_ignored(name: &str) -> bool {
    matches!(
        name,
        ".git"
            | "target"
            | "node_modules"
            | ".kl"
            | "bin"
            | "obj"
            | "dist"
            | "build"
            | ".vs"
            | ".idea"
    )
}

fn root_ignore_names(root: &Path) -> HashSet<String> {
    let mut names = HashSet::new();
    let Ok(text) = fs::read_to_string(root.join(".gitignore")) else {
        return names;
    };
    for line in text.lines() {
        let line = line.trim();
        if line.is_empty() || line.starts_with('#') || line.starts_with('!') {
            continue;
        }
        let candidate = line.trim_start_matches('/').trim_end_matches('/');
        if !candidate.is_empty()
            && !candidate.contains('*')
            && !candidate.contains('?')
            && !candidate.contains('[')
            && !candidate.contains('/')
        {
            names.insert(candidate.to_owned());
        }
    }
    names
}

fn normalize_path(path: &str) -> String {
    path.replace('\\', "/")
        .trim_start_matches("./")
        .trim_start_matches('/')
        .to_owned()
}

fn join_relative(root: &Path, relative: &str) -> PathBuf {
    let mut path = root.to_path_buf();
    for part in relative.split('/') {
        path.push(part);
    }
    path
}

fn write_cache(
    path: &Path,
    index: &RepoIndex,
    manifest: &[ManifestEntry],
) -> Result<(), IndexError> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).map_err(|source| io_error(parent, source))?;
    }
    let cache = CacheFile {
        version: CACHE_VERSION,
        root: root_key(&index.root),
        manifest: manifest.to_vec(),
        files: index.files.clone(),
    };
    let bytes = serde_json::to_vec(&cache).map_err(|error| IndexError::Cache(error.to_string()))?;
    let sequence = CACHE_TEMP_SEQUENCE.fetch_add(1, Ordering::Relaxed);
    let temporary = path.with_extension(format!("tmp{}-{sequence}", std::process::id()));
    fs::write(&temporary, bytes).map_err(|source| io_error(&temporary, source))?;
    if path.exists() {
        let _ = fs::remove_file(path);
    }
    fs::rename(&temporary, path).map_err(|source| io_error(path, source))
}

fn is_test_path(path: &str) -> bool {
    let lower = path.to_lowercase();
    if lower
        .split('/')
        .any(|segment| matches!(segment, "test" | "tests" | "__tests__" | "spec" | "specs"))
    {
        return true;
    }
    let name = path.rsplit('/').next().unwrap_or(path);
    let lower_name = name.to_lowercase();
    let stem = name.split('.').next().unwrap_or(name).to_lowercase();
    lower_name.starts_with("test_")
        || matches!(stem.as_str(), "test" | "tests" | "spec" | "specs")
        || lower_name.contains(".test.")
        || lower_name.contains(".spec.")
        || lower_name.contains("_test.")
        || lower_name.contains("_tests.")
        || name.ends_with("Test.cs")
        || name.ends_with("Tests.cs")
}

fn file_stem(path: &str) -> String {
    let name = path.rsplit('/').next().unwrap_or(path);
    let stem = name.split('.').next().unwrap_or(name);
    stem.to_lowercase()
}

fn same_module(source: &str, test: &str) -> bool {
    if parent_path(source) == parent_path(test) {
        return true;
    }
    test.split('/').any(|segment| {
        matches!(
            segment.to_lowercase().as_str(),
            "test" | "tests" | "__tests__" | "spec" | "specs"
        )
    })
}

fn parent_path(path: &str) -> &str {
    path.rsplit_once('/')
        .map(|(parent, _)| parent)
        .unwrap_or("")
}

fn stems_match(source: &str, test: &str) -> bool {
    let mut test = test.to_owned();
    for suffix in [".test", ".spec", "_test", "_tests", "-test", "-tests"] {
        if let Some(stripped) = test.strip_suffix(suffix) {
            test = stripped.to_owned();
            break;
        }
    }
    if test.ends_with("tests") && test.len() > 5 {
        test.truncate(test.len() - 5);
    } else if test.ends_with("test") && test.len() > 4 {
        test.truncate(test.len() - 4);
    }
    test == source
}

fn task_keywords(task: &str) -> Vec<String> {
    const STOP: &[&str] = &[
        "this", "that", "with", "from", "into", "when", "then", "than", "have", "will", "should",
        "would", "could", "about", "after", "before", "where", "which", "while", "make", "only",
        "also", "more", "most", "some", "such", "your", "task", "code", "file", "files", "test",
        "tests",
    ];
    let mut keywords = Vec::new();
    for word in task.split(|character: char| {
        !(character.is_ascii_alphanumeric() || character == '_' || character == '-')
    }) {
        let word = word
            .trim_matches(|character: char| character == '_' || character == '-')
            .to_lowercase();
        if word.len() >= 4
            && word
                .chars()
                .any(|character| character.is_ascii_alphabetic())
            && !STOP.contains(&word.as_str())
            && !keywords.contains(&word)
        {
            keywords.push(word);
        }
    }
    keywords.truncate(12);
    keywords
}

#[cfg(test)]
mod tests {
    use super::{file_stem, is_test_path, stems_match, task_keywords};

    #[test]
    fn classifies_test_paths_and_stems() {
        assert!(is_test_path("src/widget.test.ts"));
        assert!(is_test_path("tests/CalculatorTests.cs"));
        assert!(!is_test_path("crates/foo/src/lib.rs"));
        assert!(is_test_path("src/test.rs"));
        assert!(is_test_path("src/tests.ts"));
        assert!(!is_test_path("src/widget.ts"));
        assert_eq!(file_stem("src/CalculatorTests.cs"), "calculatortests");
        assert!(stems_match("calculator", "calculatortests"));
        assert!(stems_match("widget", "widget"));
        assert!(!stems_match("widget", "other"));
        assert!(!stems_match("lib", "library_test"));
    }

    #[test]
    fn extracts_ascii_task_keywords() {
        let keywords = task_keywords("Add a cache_probe to the repository index");
        assert!(keywords.contains(&"cache_probe".to_owned()));
        assert!(!keywords.contains(&"the".to_owned()));
    }
}
