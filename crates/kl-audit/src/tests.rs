use super::*;

fn record(index: usize) -> AuditRecord {
    let mut record = AuditRecord::new(
        "s",
        "t",
        format!("c{index}"),
        "shell",
        "shell",
        "echo ok",
        "ask",
    );
    record.ok = true;
    record
}

#[test]
fn appends_and_tails_records() {
    let temp = tempfile::tempdir().unwrap();
    let audit = JsonlAudit::open(temp.path().join("audit.jsonl")).unwrap();
    for index in 0..3 {
        audit.record(&record(index)).unwrap();
    }
    let tail = audit.tail(1).unwrap();
    assert_eq!(tail.len(), 1);
    assert_eq!(tail[0]["call_id"], "c2");
    assert!(tail[0].get("approved").is_some());
}

#[test]
fn rotates_jsonl_and_keeps_bounded_archives() {
    let temp = tempfile::tempdir().unwrap();
    let path = temp.path().join("events.jsonl");
    let audit = JsonlAudit::with_rotation(&path, 500, 2).unwrap();
    for index in 0..12 {
        audit.record(&record(index)).unwrap();
    }
    let archives = fs::read_dir(temp.path())
        .unwrap()
        .filter_map(Result::ok)
        .filter(|entry| entry.file_name().to_string_lossy().starts_with("events-"))
        .count();
    assert!(archives <= 2, "expected at most 2 archives, got {archives}");
    let tail = audit.tail(3).unwrap();
    assert_eq!(tail.len(), 3);
    assert_eq!(tail[2]["call_id"], "c11");
}

#[test]
fn sqlite_index_tails_records() {
    let temp = tempfile::tempdir().unwrap();
    let index = SqliteAuditIndex::open(temp.path().join("audit.sqlite")).unwrap();
    for index_number in 0..3 {
        index.insert(&record(index_number)).unwrap();
    }
    assert_eq!(index.count().unwrap(), 3);
    let tail = index.tail(1).unwrap();
    assert_eq!(tail.len(), 1);
    assert_eq!(tail[0]["call_id"], "c2");
    assert!(tail[0].get("approved").is_some());
}

#[test]
fn composite_audit_keeps_jsonl_and_sqlite() {
    let temp = tempfile::tempdir().unwrap();
    let audit = AuditLog::open(temp.path(), 1024 * 1024, 2).unwrap();
    audit.record(&record(0)).unwrap();
    audit.record(&record(1)).unwrap();
    assert!(audit.jsonl_path().exists());
    assert!(audit.sqlite_path().exists());
    assert_eq!(audit.count().unwrap(), 2);
    let tail = audit.tail(1).unwrap();
    assert_eq!(tail[0]["call_id"], "c1");
}

#[test]
fn existing_jsonl_is_backfilled_into_sqlite() {
    let temp = tempfile::tempdir().unwrap();
    let path = temp.path().join("events.jsonl");
    let raw = JsonlAudit::open(&path).unwrap();
    raw.record(&record(0)).unwrap();
    let audit = AuditLog::open(temp.path(), 1024 * 1024, 2).unwrap();
    assert_eq!(audit.count().unwrap(), 1);
    assert_eq!(audit.tail(1).unwrap()[0]["call_id"], "c0");
}

#[test]
fn hashes_are_stable() {
    assert_eq!(hash_text("abc"), hash_text("abc"));
    assert_ne!(hash_text("abc"), hash_text("abd"));
}
