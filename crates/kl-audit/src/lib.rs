//! Append-only runtime audit. Every tool invocation produces exactly one
//! record, including denied and failed invocations.
//!
//! Raw records stay in rotated JSONL files; SQLite is the query index.

use chrono::{SecondsFormat, Utc};
use rusqlite::{Connection, params};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use sha2::{Digest, Sha256};
use std::fs::{self, OpenOptions};
use std::io::{BufRead, BufReader, Write};
use std::path::{Path, PathBuf};
use std::sync::Mutex;

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct AuditRecord {
    pub id: String,
    pub timestamp: String,
    pub session_id: String,
    pub turn_id: String,
    pub call_id: String,
    pub actor: String,
    pub tool: String,
    pub action: String,
    pub resource: String,
    pub decision: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub approved: Option<bool>,
    pub ok: bool,
    pub duration_ms: u64,
    pub args_hash: String,
    pub output_hash: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub error: Option<String>,
}

impl AuditRecord {
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        session_id: impl Into<String>,
        turn_id: impl Into<String>,
        call_id: impl Into<String>,
        tool: impl Into<String>,
        action: impl Into<String>,
        resource: impl Into<String>,
        decision: impl Into<String>,
    ) -> Self {
        Self {
            id: format!("audit-{}", uuid::Uuid::new_v4()),
            timestamp: Utc::now().to_rfc3339_opts(SecondsFormat::Millis, true),
            session_id: session_id.into(),
            turn_id: turn_id.into(),
            call_id: call_id.into(),
            actor: "agent".to_owned(),
            tool: tool.into(),
            action: action.into(),
            resource: resource.into(),
            decision: decision.into(),
            approved: None,
            ok: false,
            duration_ms: 0,
            args_hash: String::new(),
            output_hash: String::new(),
            error: None,
        }
    }

    pub fn with_args(&mut self, args: &Value) -> &mut Self {
        self.args_hash = hash_json(args);
        self
    }

    pub fn with_output(&mut self, output: &str) -> &mut Self {
        self.output_hash = hash_text(output);
        self
    }
}

#[derive(Debug, thiserror::Error)]
pub enum AuditError {
    #[error("io error: {0}")]
    Io(#[from] std::io::Error),
    #[error("serialization error: {0}")]
    Serialization(#[from] serde_json::Error),
    #[error("sqlite error: {0}")]
    Sqlite(#[from] rusqlite::Error),
}

pub trait AuditSink: Send + Sync {
    fn record(&self, record: &AuditRecord) -> Result<(), AuditError>;
}

#[derive(Debug)]
pub struct JsonlAudit {
    path: PathBuf,
    rotate_bytes: u64,
    keep_files: usize,
    lock: Mutex<()>,
}

impl JsonlAudit {
    pub fn open(path: impl AsRef<Path>) -> Result<Self, AuditError> {
        Self::with_rotation(path, 10 * 1024 * 1024, 5)
    }

    pub fn with_rotation(
        path: impl AsRef<Path>,
        rotate_bytes: u64,
        keep_files: usize,
    ) -> Result<Self, AuditError> {
        let path = path.as_ref().to_path_buf();
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent)?;
        }
        Ok(Self {
            path,
            rotate_bytes,
            keep_files,
            lock: Mutex::new(()),
        })
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    pub fn tail(&self, limit: usize) -> Result<Vec<Value>, AuditError> {
        let mut records = Vec::new();
        for path in self.files()? {
            let file = fs::File::open(path)?;
            for line in BufReader::new(file).lines() {
                let line = line?;
                if line.trim().is_empty() {
                    continue;
                }
                if let Ok(value) = serde_json::from_str::<Value>(&line) {
                    records.push(value);
                }
            }
        }
        let start = records.len().saturating_sub(limit);
        Ok(records.split_off(start))
    }

    pub fn records(&self) -> Result<Vec<AuditRecord>, AuditError> {
        let mut records = Vec::new();
        for path in self.files()? {
            let file = fs::File::open(path)?;
            for line in BufReader::new(file).lines() {
                let line = line?;
                if let Ok(record) = serde_json::from_str::<AuditRecord>(&line) {
                    records.push(record);
                }
            }
        }
        Ok(records)
    }

    fn files(&self) -> Result<Vec<PathBuf>, AuditError> {
        let parent = self.path.parent().unwrap_or_else(|| Path::new("."));
        let prefix = self
            .path
            .file_stem()
            .and_then(|stem| stem.to_str())
            .map(|stem| format!("{stem}-"))
            .unwrap_or_default();
        let mut archives = Vec::new();
        if parent.exists() {
            for entry in fs::read_dir(parent)? {
                let entry = entry?;
                if !entry.file_type()?.is_file() {
                    continue;
                }
                let name = entry.file_name().to_string_lossy().into_owned();
                if name.starts_with(&prefix) && name.ends_with(".jsonl") {
                    archives.push(entry.path());
                }
            }
        }
        archives.sort();
        if self.path.exists() {
            archives.push(self.path.clone());
        }
        Ok(archives)
    }

    fn rotate(&self) -> Result<(), AuditError> {
        if !self.path.exists() {
            return Ok(());
        }
        let Some(parent) = self.path.parent() else {
            return Ok(());
        };
        let stem = self
            .path
            .file_stem()
            .and_then(|stem| stem.to_str())
            .unwrap_or("events");
        let timestamp = Utc::now()
            .to_rfc3339_opts(SecondsFormat::Millis, true)
            .replace([':', '.'], "-");
        let archive = parent.join(format!("{stem}-{timestamp}.jsonl"));
        fs::rename(&self.path, &archive)?;
        self.cleanup(parent, stem)?;
        Ok(())
    }

    fn cleanup(&self, parent: &Path, stem: &str) -> Result<(), AuditError> {
        let prefix = format!("{stem}-");
        let mut archives = Vec::new();
        for entry in fs::read_dir(parent)? {
            let entry = entry?;
            if !entry.file_type()?.is_file() {
                continue;
            }
            let name = entry.file_name().to_string_lossy().into_owned();
            if name.starts_with(&prefix) && name.ends_with(".jsonl") {
                archives.push(entry.path());
            }
        }
        archives.sort();
        let excess = archives.len().saturating_sub(self.keep_files);
        for path in archives.into_iter().take(excess) {
            let _ = fs::remove_file(path);
        }
        Ok(())
    }
}

impl AuditSink for JsonlAudit {
    fn record(&self, record: &AuditRecord) -> Result<(), AuditError> {
        let _guard = self.lock.lock().unwrap_or_else(|error| error.into_inner());
        let mut line = serde_json::to_vec(record)?;
        line.push(b'\n');
        if self.rotate_bytes > 0 {
            let size = fs::metadata(&self.path).map(|meta| meta.len()).unwrap_or(0);
            if size > 0 && size.saturating_add(line.len() as u64) > self.rotate_bytes {
                self.rotate()?;
            }
        }
        let mut file = OpenOptions::new()
            .create(true)
            .append(true)
            .open(&self.path)?;
        file.write_all(&line)?;
        file.flush()?;
        Ok(())
    }
}

#[derive(Debug)]
pub struct SqliteAuditIndex {
    path: PathBuf,
    connection: Mutex<Connection>,
}

impl SqliteAuditIndex {
    pub fn open(path: impl AsRef<Path>) -> Result<Self, AuditError> {
        let path = path.as_ref().to_path_buf();
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent)?;
        }
        let connection = Connection::open(&path)?;
        connection.execute_batch(
            "CREATE TABLE IF NOT EXISTS audit_records (
                id TEXT PRIMARY KEY,
                timestamp TEXT NOT NULL,
                session_id TEXT NOT NULL,
                turn_id TEXT NOT NULL,
                call_id TEXT NOT NULL,
                tool TEXT NOT NULL,
                action TEXT NOT NULL,
                decision TEXT NOT NULL,
                ok INTEGER NOT NULL,
                resource TEXT NOT NULL,
                record_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS audit_timestamp_idx ON audit_records(timestamp);
            CREATE INDEX IF NOT EXISTS audit_session_idx ON audit_records(session_id);",
        )?;
        Ok(Self {
            path,
            connection: Mutex::new(connection),
        })
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    pub fn insert(&self, record: &AuditRecord) -> Result<(), AuditError> {
        let json = serde_json::to_string(record)?;
        let connection = self
            .connection
            .lock()
            .unwrap_or_else(|error| error.into_inner());
        connection.execute(
            "INSERT OR REPLACE INTO audit_records
             (id, timestamp, session_id, turn_id, call_id, tool, action, decision, ok, resource, record_json)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11)",
            params![
                record.id,
                record.timestamp,
                record.session_id,
                record.turn_id,
                record.call_id,
                record.tool,
                record.action,
                record.decision,
                if record.ok { 1 } else { 0 },
                record.resource,
                json,
            ],
        )?;
        Ok(())
    }

    pub fn tail(&self, limit: usize) -> Result<Vec<Value>, AuditError> {
        let connection = self
            .connection
            .lock()
            .unwrap_or_else(|error| error.into_inner());
        let mut statement = connection.prepare(
            "SELECT record_json FROM audit_records ORDER BY timestamp DESC, rowid DESC LIMIT ?1",
        )?;
        let rows = statement.query_map([limit as i64], |row| row.get::<_, String>(0))?;
        let mut records = Vec::new();
        for row in rows {
            let json = row?;
            if let Ok(value) = serde_json::from_str::<Value>(&json) {
                records.push(value);
            }
        }
        records.reverse();
        Ok(records)
    }

    pub fn count(&self) -> Result<usize, AuditError> {
        let connection = self
            .connection
            .lock()
            .unwrap_or_else(|error| error.into_inner());
        let count: i64 =
            connection.query_row("SELECT COUNT(*) FROM audit_records", [], |row| row.get(0))?;
        Ok(count as usize)
    }
}

#[derive(Debug)]
pub struct AuditLog {
    jsonl: JsonlAudit,
    sqlite: SqliteAuditIndex,
}

impl AuditLog {
    pub fn open(
        directory: impl AsRef<Path>,
        rotate_bytes: u64,
        keep_files: usize,
    ) -> Result<Self, AuditError> {
        let directory = directory.as_ref();
        let log = Self {
            jsonl: JsonlAudit::with_rotation(
                directory.join("events.jsonl"),
                rotate_bytes,
                keep_files,
            )?,
            sqlite: SqliteAuditIndex::open(directory.join("audit.sqlite"))?,
        };
        // Existing raw JSONL is authoritative; re-index it on startup.
        for record in log.jsonl.records()? {
            log.sqlite.insert(&record)?;
        }
        Ok(log)
    }

    pub fn tail(&self, limit: usize) -> Result<Vec<Value>, AuditError> {
        self.sqlite.tail(limit)
    }

    pub fn count(&self) -> Result<usize, AuditError> {
        self.sqlite.count()
    }

    pub fn jsonl_path(&self) -> &Path {
        self.jsonl.path()
    }

    pub fn sqlite_path(&self) -> &Path {
        self.sqlite.path()
    }
}

impl AuditSink for AuditLog {
    fn record(&self, record: &AuditRecord) -> Result<(), AuditError> {
        self.jsonl.record(record)?;
        self.sqlite.insert(record)
    }
}

#[derive(Debug, Default)]
pub struct MemoryAudit {
    records: Mutex<Vec<AuditRecord>>,
}

impl MemoryAudit {
    pub fn records(&self) -> Vec<AuditRecord> {
        self.records
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .clone()
    }
}

impl AuditSink for MemoryAudit {
    fn record(&self, record: &AuditRecord) -> Result<(), AuditError> {
        self.records
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .push(record.clone());
        Ok(())
    }
}

pub fn hash_json(value: &Value) -> String {
    hash_text(&serde_json::to_string(value).unwrap_or_default())
}

pub fn hash_text(value: &str) -> String {
    let digest = Sha256::digest(value.as_bytes());
    format!("{digest:x}")
}

#[cfg(test)]
mod tests {
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
}
