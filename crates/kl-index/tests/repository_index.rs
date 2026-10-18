use kl_index::{IndexQuery, RepoIndex};
use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;

fn write(path: &Path, content: &str) {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).unwrap();
    }
    fs::write(path, content).unwrap();
}

fn git(root: &Path, args: &[&str]) {
    let status = Command::new("git")
        .arg("-C")
        .arg(root)
        .args([
            "-c",
            "user.email=index@example.test",
            "-c",
            "user.name=index",
        ])
        .args(args)
        .status()
        .unwrap();
    assert!(status.success(), "git {args:?} failed");
}

struct Fixture {
    temp: tempfile::TempDir,
    root: PathBuf,
}

impl Fixture {
    fn new() -> Self {
        let temp = tempfile::Builder::new()
            .prefix("kl-index-")
            .tempdir_in(env!("CARGO_TARGET_TMPDIR"))
            .unwrap();
        let root = temp.path().join("repo");
        fs::create_dir_all(&root).unwrap();
        write(&root.join(".gitignore"), "/target/\n/node_modules/\n");
        write(
            &root.join("Cargo.toml"),
            "[package]\nname = \"fixture\"\nversion = \"0.1.0\"\n",
        );
        write(
            &root.join("src/lib.rs"),
            "pub struct WidgetIndex;\n\npub fn index_repo() {}\n\n#[cfg(test)]\nmod tests {\n    #[test]\n    fn indexes() {}\n}\n",
        );
        write(
            &root.join("src/widget.ts"),
            "export class Widget {\n  renderWidget() {\n    return 'widget';\n  }\n}\n\nexport function mountWidget() {}\n",
        );
        write(
            &root.join("src/widget.test.ts"),
            "import { Widget } from './widget';\ntest('widget', () => { new Widget(); });\n",
        );
        write(
            &root.join("src/Calculator.cs"),
            "public class Calculator {\n    public int Add(int a, int b) {\n        return a + b;\n    }\n}\n",
        );
        write(
            &root.join("tests/CalculatorTests.cs"),
            "public class CalculatorTests {\n    public void AddWorks() {}\n}\n",
        );
        write(&root.join("docs/readme.md"), "# Fixture\n");
        write(
            &root.join("target/generated.rs"),
            "pub fn ignored_target() {}\n",
        );
        write(
            &root.join("node_modules/pkg/index.ts"),
            "export function ignoredPackage() {}\n",
        );
        git(&root, &["init"]);
        git(&root, &["add", "."]);
        git(&root, &["commit", "-m", "fixture"]);
        Self { temp, root }
    }
}

#[test]
fn index_respects_gitignore_and_extracts_symbols() {
    let fixture = Fixture::new();
    let index = RepoIndex::build(&fixture.root).unwrap();
    let paths: Vec<&str> = index
        .files()
        .iter()
        .map(|file| file.path.as_str())
        .collect();
    assert!(paths.contains(&"src/lib.rs"));
    assert!(paths.contains(&"src/widget.ts"));
    assert!(paths.contains(&"src/Calculator.cs"));
    assert!(!paths.iter().any(|path| path.starts_with("target/")));
    assert!(!paths.iter().any(|path| path.starts_with("node_modules/")));

    let rust = index.file("src/lib.rs").unwrap();
    assert_eq!(rust.language, "rust");
    assert!(rust.lines >= 8);
    assert!(
        rust.symbols
            .iter()
            .any(|symbol| symbol.name == "WidgetIndex" && symbol.kind == "struct")
    );
    assert!(
        rust.symbols
            .iter()
            .any(|symbol| symbol.name == "index_repo" && symbol.kind == "function")
    );

    let typescript = index.file("src/widget.ts").unwrap();
    assert_eq!(typescript.language, "typescript");
    assert!(
        typescript
            .symbols
            .iter()
            .any(|symbol| symbol.name == "Widget" && symbol.kind == "class")
    );
    assert!(
        typescript
            .symbols
            .iter()
            .any(|symbol| symbol.name == "renderWidget" && symbol.kind == "method")
    );
    assert!(
        typescript
            .symbols
            .iter()
            .any(|symbol| symbol.name == "mountWidget" && symbol.kind == "function")
    );

    let csharp = index.file("src/Calculator.cs").unwrap();
    assert!(
        csharp
            .symbols
            .iter()
            .any(|symbol| symbol.name == "Calculator" && symbol.kind == "class")
    );
    assert!(
        csharp
            .symbols
            .iter()
            .any(|symbol| symbol.name == "Add" && symbol.kind == "method")
    );

    let markdown = index.file("docs/readme.md").unwrap();
    assert_eq!(markdown.language, "markdown");
    assert!(markdown.symbols.is_empty());
}

#[test]
fn queries_by_path_symbol_language_and_recent_time() {
    let fixture = Fixture::new();
    let index = RepoIndex::build(&fixture.root).unwrap();

    let by_symbol = index
        .search(&IndexQuery {
            symbol: Some("renderwidget".to_owned()),
            ..IndexQuery::default()
        })
        .unwrap();
    assert_eq!(by_symbol.len(), 1);
    assert_eq!(by_symbol[0].path, "src/widget.ts");

    let by_path = index
        .search(&IndexQuery {
            path: Some("calculator".to_owned()),
            ..IndexQuery::default()
        })
        .unwrap();
    assert!(by_path.iter().any(|file| file.path == "src/Calculator.cs"));

    let by_language = index
        .search(&IndexQuery {
            language: Some("typescript".to_owned()),
            ..IndexQuery::default()
        })
        .unwrap();
    assert!(!by_language.is_empty());
    assert!(by_language.iter().all(|file| file.language == "typescript"));

    let recent = index
        .search(&IndexQuery {
            recent_days: Some(7),
            ..IndexQuery::default()
        })
        .unwrap();
    assert!(recent.iter().any(|file| file.path == "src/widget.ts"));

    let limited = index
        .search(&IndexQuery {
            limit: 1,
            ..IndexQuery::default()
        })
        .unwrap();
    assert_eq!(limited.len(), 1);
}

#[test]
fn diff_query_selects_modified_and_untracked_files() {
    let fixture = Fixture::new();
    let cache = fixture.temp.path().join("cache");
    let _ = RepoIndex::load_with_cache(&fixture.root, Some(&cache)).unwrap();

    let widget = fixture.root.join("src/widget.ts");
    let original = fs::read_to_string(&widget).unwrap();
    write(
        &widget,
        &format!("{original}\nexport function changedWidget() {{}}\n"),
    );
    write(
        &fixture.root.join("src/new.ts"),
        "export function brandNew() {}\n",
    );

    let index = RepoIndex::load_with_cache(&fixture.root, Some(&cache)).unwrap();
    let changed = index.changed_files().unwrap();
    assert!(changed.contains(&"src/widget.ts".to_owned()));
    assert!(changed.contains(&"src/new.ts".to_owned()));

    let changed_files = index
        .search(&IndexQuery {
            changed_only: true,
            ..IndexQuery::default()
        })
        .unwrap();
    assert!(
        changed_files
            .iter()
            .any(|file| file.path == "src/widget.ts")
    );
    assert!(changed_files.iter().any(|file| file.path == "src/new.ts"));

    let symbol = index
        .search(&IndexQuery {
            symbol: Some("changedWidget".to_owned()),
            changed_only: true,
            ..IndexQuery::default()
        })
        .unwrap();
    assert_eq!(symbol.len(), 1);
    assert_eq!(symbol[0].path, "src/widget.ts");
}

#[test]
fn selects_same_module_tests_and_falls_back_to_project_command() {
    let fixture = Fixture::new();
    let index = RepoIndex::build(&fixture.root).unwrap();

    let mapped = index.select_tests(&["src/widget.ts".to_owned()]);
    assert_eq!(mapped.tests, vec!["src/widget.test.ts".to_owned()]);
    assert!(mapped.fallback_command.is_none());

    let csharp = index.select_tests(&["src/Calculator.cs".to_owned()]);
    assert_eq!(csharp.tests, vec!["tests/CalculatorTests.cs".to_owned()]);
    assert!(csharp.fallback_command.is_none());

    let fallback = index.select_tests(&["src/lib.rs".to_owned()]);
    assert!(fallback.tests.is_empty());
    assert_eq!(fallback.fallback_command.as_deref(), Some("cargo test"));

    let task_context = index.prompt_context("render the widget", &[]);
    assert!(task_context.contains("src/widget.test.ts"));

    let context = index.prompt_context("render the widget", &["src/widget.ts".to_owned()]);
    assert!(context.contains("src/widget.ts"));
    assert!(context.contains("src/widget.test.ts"));
    assert!(context.contains("Tests:"));
}

#[test]
fn cache_is_invalidated_rebuilt_and_kept_outside_the_workspace() {
    let fixture = Fixture::new();
    let cache_dir = fixture.temp.path().join("cache");
    let cache_path = RepoIndex::cache_path_with(&fixture.root, Some(&cache_dir)).unwrap();

    let index = RepoIndex::load_with_cache(&fixture.root, Some(&cache_dir)).unwrap();
    assert!(cache_path.exists());
    assert!(
        !index
            .file("src/lib.rs")
            .unwrap()
            .symbols
            .iter()
            .any(|symbol| symbol.name == "cache_probe")
    );
    assert!(!fixture.root.join(".kl").exists());

    let lib = fixture.root.join("src/lib.rs");
    let original = fs::read_to_string(&lib).unwrap();
    write(&lib, &format!("{original}\npub fn cache_probe() {{}}\n"));

    let rebuilt = RepoIndex::load_with_cache(&fixture.root, Some(&cache_dir)).unwrap();
    assert!(
        rebuilt
            .file("src/lib.rs")
            .unwrap()
            .symbols
            .iter()
            .any(|symbol| symbol.name == "cache_probe")
    );

    assert!(RepoIndex::invalidate_with(&fixture.root, Some(&cache_dir)).unwrap());
    assert!(!cache_path.exists());

    let explicit = RepoIndex::rebuild_with_cache(&fixture.root, Some(&cache_dir)).unwrap();
    assert!(
        explicit
            .file("src/lib.rs")
            .unwrap()
            .symbols
            .iter()
            .any(|symbol| symbol.name == "cache_probe")
    );
    assert!(cache_path.exists());
}
