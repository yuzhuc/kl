//! Append-only runtime audit. Every tool invocation produces exactly one
//! record, including denied and failed invocations.

use chrono::{SecondsFormat, Utc};
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
}

pub trait AuditSink: Send + Sync {
    fn record(&self, record: &AuditRecord) -> Result<(), AuditError>;
}

#[derive(Debug)]
pub struct JsonlAudit {
    path: PathBuf,
    lock: Mutex<()>,
}

impl JsonlAudit {
    pub fn open(path: impl AsRef<Path>) -> Result<Self, AuditError> {
        let path = path.as_ref().to_path_buf();
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent)?;
        }
        Ok(Self {
            path,
            lock: Mutex::new(()),
        })
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    pub fn tail(&self, limit: usize) -> Result<Vec<Value>, AuditError> {
        if !self.path.exists() {
            return Ok(Vec::new());
        }
        let file = fs::File::open(&self.path)?;
        let lines = BufReader::new(file).lines();
        let mut records: Vec<Value> = Vec::new();
        for line in lines {
            let line = line?;
            if line.trim().is_empty() {
                continue;
            }
            if let Ok(value) = serde_json::from_str::<Value>(&line) {
                records.push(value);
            }
        }
        let start = records.len().saturating_sub(limit);
        Ok(records.split_off(start))
    }
}

impl AuditSink for JsonlAudit {
    fn record(&self, record: &AuditRecord) -> Result<(), AuditError> {
        let _guard = self.lock.lock().unwrap_or_else(|error| error.into_inner());
        let mut line = serde_json::to_vec(record)?;
        line.push(b'\n');
        let mut file = OpenOptions::new()
            .create(true)
            .append(true)
            .open(&self.path)?;
        file.write_all(&line)?;
        file.flush()?;
        Ok(())
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

    #[test]
    fn appends_and_tails_records() {
        let temp = tempfile::tempdir().unwrap();
        let audit = JsonlAudit::open(temp.path().join("audit.jsonl")).unwrap();
        for index in 0..3 {
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
            audit.record(&record).unwrap();
        }
        let tail = audit.tail(1).unwrap();
        assert_eq!(tail.len(), 1);
        assert_eq!(tail[0]["call_id"], "c2");
    }

    #[test]
    fn hashes_are_stable() {
        assert_eq!(hash_text("abc"), hash_text("abc"));
        assert_ne!(hash_text("abc"), hash_text("abd"));
    }
}
