//! Session persistence: `session.json` metadata, `state.json` message
//! projection and an append-only `events.jsonl` log.

use chrono::{SecondsFormat, Utc};
use kl_protocol::CoreEvent;
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::fs::{self, OpenOptions};
use std::io::{BufRead, BufReader, Write};
use std::path::{Path, PathBuf};

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct SessionMeta {
    pub id: String,
    pub workspace: String,
    pub provider: String,
    pub model: String,
    pub created_at: String,
    pub updated_at: String,
    #[serde(default)]
    pub message_count: usize,
}

#[derive(Debug, Clone)]
pub struct SessionData {
    pub meta: SessionMeta,
    pub messages: Vec<Value>,
}

#[derive(Debug, thiserror::Error)]
pub enum StoreError {
    #[error("session not found: {0}")]
    NotFound(String),
    #[error("io error: {0}")]
    Io(#[from] std::io::Error),
    #[error("serialization error: {0}")]
    Serialization(#[from] serde_json::Error),
}

#[derive(Debug, Clone)]
pub struct SessionStore {
    root: PathBuf,
}

impl SessionStore {
    pub fn new(root: impl AsRef<Path>) -> Result<Self, StoreError> {
        let root = root.as_ref().to_path_buf();
        fs::create_dir_all(&root)?;
        Ok(Self { root })
    }

    pub fn root(&self) -> &Path {
        &self.root
    }

    pub fn create(
        &self,
        workspace: &str,
        provider: &str,
        model: &str,
    ) -> Result<SessionData, StoreError> {
        let now = now();
        let data = SessionData {
            meta: SessionMeta {
                id: uuid::Uuid::new_v4().to_string(),
                workspace: workspace.to_owned(),
                provider: provider.to_owned(),
                model: model.to_owned(),
                created_at: now.clone(),
                updated_at: now,
                message_count: 0,
            },
            messages: Vec::new(),
        };
        let directory = self.session_dir(&data.meta.id);
        fs::create_dir_all(&directory)?;
        fs::write(directory.join("events.jsonl"), b"")?;
        self.save(&data)?;
        Ok(data)
    }

    pub fn load(&self, id: &str) -> Result<SessionData, StoreError> {
        let directory = self.session_dir(id);
        let meta_path = directory.join("session.json");
        if !meta_path.exists() {
            return Err(StoreError::NotFound(id.to_owned()));
        }
        let meta: SessionMeta = serde_json::from_slice(&fs::read(&meta_path)?)?;
        let state_path = directory.join("state.json");
        let messages = if state_path.exists() {
            let state: StateFile = serde_json::from_slice(&fs::read(&state_path)?)?;
            state.messages
        } else {
            Vec::new()
        };
        Ok(SessionData { meta, messages })
    }

    pub fn list(&self) -> Result<Vec<SessionMeta>, StoreError> {
        let mut sessions = Vec::new();
        if !self.root.exists() {
            return Ok(sessions);
        }
        for entry in fs::read_dir(&self.root)? {
            let entry = entry?;
            if !entry.file_type()?.is_dir() {
                continue;
            }
            let meta_path = entry.path().join("session.json");
            if !meta_path.exists() {
                continue;
            }
            if let Ok(bytes) = fs::read(&meta_path) {
                if let Ok(meta) = serde_json::from_slice::<SessionMeta>(&bytes) {
                    sessions.push(meta);
                }
            }
        }
        sessions.sort_by(|left, right| right.updated_at.cmp(&left.updated_at));
        Ok(sessions)
    }

    pub fn save(&self, data: &SessionData) -> Result<(), StoreError> {
        let directory = self.session_dir(&data.meta.id);
        fs::create_dir_all(&directory)?;
        let mut meta = data.meta.clone();
        meta.message_count = data.messages.len();
        meta.updated_at = now();
        write_atomic(
            &directory.join("session.json"),
            &serde_json::to_vec_pretty(&meta)?,
        )?;
        write_atomic(
            &directory.join("state.json"),
            &serde_json::to_vec_pretty(&StateFile {
                messages: data.messages.clone(),
            })?,
        )?;
        Ok(())
    }

    pub fn append_event(
        &self,
        session_id: &str,
        seq: u64,
        event: &CoreEvent,
    ) -> Result<(), StoreError> {
        let directory = self.session_dir(session_id);
        fs::create_dir_all(&directory)?;
        let record = EventRecord {
            seq,
            at: now(),
            event,
        };
        let mut line = serde_json::to_vec(&record)?;
        line.push(b'\n');
        let mut file = OpenOptions::new()
            .create(true)
            .append(true)
            .open(directory.join("events.jsonl"))?;
        file.write_all(&line)?;
        file.flush()?;
        Ok(())
    }

    pub fn event_count(&self, session_id: &str) -> Result<u64, StoreError> {
        let path = self.session_dir(session_id).join("events.jsonl");
        if !path.exists() {
            return Ok(0);
        }
        let file = fs::File::open(path)?;
        Ok(BufReader::new(file).lines().count() as u64)
    }

    fn session_dir(&self, id: &str) -> PathBuf {
        self.root.join(id)
    }
}

#[derive(Debug, Serialize, Deserialize)]
struct StateFile {
    #[serde(default)]
    messages: Vec<Value>,
}

#[derive(Debug, Serialize)]
struct EventRecord<'a> {
    seq: u64,
    at: String,
    event: &'a CoreEvent,
}

fn now() -> String {
    Utc::now().to_rfc3339_opts(SecondsFormat::Millis, true)
}

fn write_atomic(path: &Path, bytes: &[u8]) -> std::io::Result<()> {
    let temporary = path.with_extension("tmp");
    fs::write(&temporary, bytes)?;
    if path.exists() {
        fs::remove_file(path)?;
    }
    fs::rename(temporary, path)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn create_save_load_round_trip() {
        let temp = tempfile::tempdir().unwrap();
        let store = SessionStore::new(temp.path()).unwrap();
        let mut data = store.create("D:\\work", "openai", "gpt-test").unwrap();
        data.messages
            .push(serde_json::json!({"role": "user", "content": "hi"}));
        store.save(&data).unwrap();

        let loaded = store.load(&data.meta.id).unwrap();
        assert_eq!(loaded.messages.len(), 1);
        assert_eq!(loaded.meta.workspace, "D:\\work");
        assert_eq!(store.list().unwrap().len(), 1);
    }

    #[test]
    fn appends_events_and_counts_them() {
        let temp = tempfile::tempdir().unwrap();
        let store = SessionStore::new(temp.path()).unwrap();
        let data = store.create("D:\\work", "openai", "gpt-test").unwrap();
        let event = CoreEvent::TurnStarted {
            turn_id: "t".to_owned(),
        };
        store.append_event(&data.meta.id, 1, &event).unwrap();
        store.append_event(&data.meta.id, 2, &event).unwrap();
        assert_eq!(store.event_count(&data.meta.id).unwrap(), 2);
    }

    #[test]
    fn missing_session_is_not_found() {
        let temp = tempfile::tempdir().unwrap();
        let store = SessionStore::new(temp.path()).unwrap();
        assert!(matches!(
            store.load("missing"),
            Err(StoreError::NotFound(_))
        ));
    }
}
