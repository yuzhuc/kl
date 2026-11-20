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
