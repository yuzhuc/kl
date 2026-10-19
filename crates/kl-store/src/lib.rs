//! Session persistence: `session.json` metadata, `state.json` message
//! projection and an append-only `events.jsonl` log.

use chrono::{SecondsFormat, Utc};
use kl_protocol::CoreEvent;
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::fs::{self, OpenOptions};
use std::io::{BufRead, BufReader, Write};
use std::path::{Path, PathBuf};

pub const EVENT_SCHEMA_VERSION: u32 = 1;
pub const TURN_STATE_IDLE: &str = "idle";
pub const TURN_STATE_WORKING: &str = "working";
pub const TURN_STATE_WAITING_APPROVAL: &str = "waiting_approval";

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct SessionMeta {
    pub id: String,
    pub workspace: String,
    pub provider: String,
    pub model: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub reasoning_effort: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
    pub created_at: String,
    pub updated_at: String,
    #[serde(default)]
    pub message_count: usize,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub status: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub task: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub prompt: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub last_message: Option<String>,
    #[serde(default = "default_turn_state")]
    pub turn_state: String,
}

#[derive(Debug, Clone)]
pub struct SessionData {
    pub meta: SessionMeta,
    pub messages: Vec<Value>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StoredEvent {
    pub seq: u64,
    pub at: String,
    #[serde(default = "default_event_schema_version")]
    pub schema_version: u32,
    pub event: CoreEvent,
}

#[derive(Debug, thiserror::Error)]
pub enum StoreError {
    #[error("session not found: {0}")]
    NotFound(String),
    #[error("io error: {0}")]
    Io(#[from] std::io::Error),
    #[error("serialization error: {0}")]
    Serialization(#[from] serde_json::Error),
    #[error("invalid session name")]
    InvalidName,
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
        reasoning_effort: &str,
    ) -> Result<SessionData, StoreError> {
        let now = now();
        let data = SessionData {
            meta: SessionMeta {
                id: uuid::Uuid::new_v4().to_string(),
                workspace: workspace.to_owned(),
                provider: provider.to_owned(),
                model: model.to_owned(),
                reasoning_effort: Some(reasoning_effort.to_owned()),
                name: None,
                created_at: now.clone(),
                updated_at: now,
                message_count: 0,
                status: Some("ready".to_owned()),
                task: None,
                prompt: None,
                last_message: None,
                turn_state: default_turn_state(),
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

    pub fn delete(&self, id: &str) -> Result<(), StoreError> {
        let id = uuid::Uuid::parse_str(id).map_err(|_| StoreError::NotFound(id.to_owned()))?;
        let directory = self.root.join(id.to_string());
        if !directory.join("session.json").exists() {
            return Err(StoreError::NotFound(id.to_string()));
        }
        fs::remove_dir_all(directory)?;
        Ok(())
    }

    pub fn rename(&self, id: &str, name: &str) -> Result<SessionMeta, StoreError> {
        let name = name.trim();
        if name.is_empty() || name.chars().count() > 160 {
            return Err(StoreError::InvalidName);
        }
        let mut data = self.load(id)?;
        data.meta.name = Some(name.to_owned());
        self.save(&data)?;
        Ok(self.load(id)?.meta)
    }

    pub fn save(&self, data: &SessionData) -> Result<(), StoreError> {
        let directory = self.session_dir(&data.meta.id);
        fs::create_dir_all(&directory)?;
        let mut meta = data.meta.clone();
        meta.message_count = data.messages.len();
        meta.updated_at = now();
        let (task, prompt, last_message) = summarize_messages(&data.messages);
        meta.task = meta.name.clone().or(task);
        meta.prompt = prompt;
        meta.last_message = last_message;
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

    pub fn set_turn_state(
        &self,
        session_id: &str,
        turn_state: &str,
    ) -> Result<SessionMeta, StoreError> {
        let directory = self.session_dir(session_id);
        let meta_path = directory.join("session.json");
        if !meta_path.exists() {
            return Err(StoreError::NotFound(session_id.to_owned()));
        }
        let mut meta: SessionMeta = serde_json::from_slice(&fs::read(&meta_path)?)?;
        meta.turn_state = turn_state.to_owned();
        meta.updated_at = now();
        write_atomic(&meta_path, &serde_json::to_vec_pretty(&meta)?)?;
        Ok(meta)
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
            schema_version: EVENT_SCHEMA_VERSION,
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

    pub fn read_events(&self, session_id: &str) -> Result<Vec<StoredEvent>, StoreError> {
        let path = self.session_dir(session_id).join("events.jsonl");
        if !path.exists() {
            return Ok(Vec::new());
        }
        let file = fs::File::open(path)?;
        let mut events = Vec::new();
        for line in BufReader::new(file).lines() {
            let line = line?;
            if line.trim().is_empty() {
                continue;
            }
            events.push(serde_json::from_str(&line)?);
        }
        Ok(events)
    }

    pub fn project_messages(&self, session_id: &str) -> Result<Vec<Value>, StoreError> {
        let events = self.read_events(session_id)?;
        Ok(project_messages(&events))
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

pub fn project_messages(events: &[StoredEvent]) -> Vec<Value> {
    events
        .iter()
        .filter_map(|stored| match &stored.event {
            CoreEvent::UserMessage { text, .. } => {
                Some(serde_json::json!({"role": "user", "content": text}))
            }
            CoreEvent::TurnCompleted { text, .. } if !text.trim().is_empty() => {
                Some(serde_json::json!({"role": "assistant", "content": text}))
            }
            CoreEvent::ToolCompleted {
                call_id,
                name,
                ok,
                summary,
                output,
                ..
            } => Some(serde_json::json!({
                "role": "tool",
                "tool_call_id": call_id,
                "name": name,
                "content": output.as_deref().unwrap_or(summary.as_str()),
                "ok": ok,
            })),
            _ => None,
        })
        .collect()
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
    #[serde(default = "default_event_schema_version")]
    schema_version: u32,
    event: &'a CoreEvent,
}

fn now() -> String {
    Utc::now().to_rfc3339_opts(SecondsFormat::Millis, true)
}

fn default_event_schema_version() -> u32 {
    EVENT_SCHEMA_VERSION
}

fn default_turn_state() -> String {
    TURN_STATE_IDLE.to_owned()
}

fn summarize_messages(messages: &[Value]) -> (Option<String>, Option<String>, Option<String>) {
    let mut task = None;
    let mut prompt = None;
    let mut last_message = None;
    for message in messages {
        let text = message_text(message);
        if text.trim().is_empty() {
            continue;
        }
        match message.get("role").and_then(Value::as_str) {
            Some("user") => {
                if task.is_none() {
                    task = Some(summarize(&text));
                }
                prompt = Some(summarize(&text));
            }
            Some("assistant") => last_message = Some(summarize(&text)),
            _ => {}
        }
    }
    (task, prompt, last_message)
}

fn message_text(value: &Value) -> String {
    if let Some(text) = value.get("content").and_then(Value::as_str) {
        return text.to_owned();
    }
    value
        .get("content")
        .and_then(Value::as_array)
        .map(|parts| {
            parts
                .iter()
                .filter_map(|part| part.get("data").and_then(Value::as_str))
                .collect::<Vec<_>>()
                .join("")
        })
        .unwrap_or_default()
}

fn summarize(text: &str) -> String {
    let mut summary = text.split_whitespace().collect::<Vec<_>>().join(" ");
    if summary.chars().count() > 160 {
        summary = summary.chars().take(157).collect::<String>() + "...";
    }
    summary
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
        let mut data = store
            .create("D:\\work", "openai", "gpt-test", "high")
            .unwrap();
        assert_eq!(data.meta.status.as_deref(), Some("ready"));
        assert_eq!(data.meta.turn_state, TURN_STATE_IDLE);
        data.messages.push(serde_json::json!({
            "role": "user",
            "content": [{"type": "text", "data": "fix it"}]
        }));
        data.messages.push(serde_json::json!({
            "role": "assistant",
            "content": [{"type": "text", "data": "done"}]
        }));
        data.meta.status = Some("completed".to_owned());
        store.save(&data).unwrap();

        let loaded = store.load(&data.meta.id).unwrap();
        assert_eq!(loaded.messages.len(), 2);
        assert_eq!(loaded.meta.workspace, "D:\\work");
        assert_eq!(loaded.meta.reasoning_effort.as_deref(), Some("high"));
        assert_eq!(loaded.meta.status.as_deref(), Some("completed"));
        assert_eq!(loaded.meta.task.as_deref(), Some("fix it"));
        assert_eq!(loaded.meta.prompt.as_deref(), Some("fix it"));
        assert_eq!(loaded.meta.last_message.as_deref(), Some("done"));
        assert_eq!(loaded.meta.turn_state, TURN_STATE_IDLE);
        assert_eq!(store.list().unwrap().len(), 1);
    }

    #[test]
    fn turn_state_persists_and_updates_updated_at() {
        let temp = tempfile::tempdir().unwrap();
        let store = SessionStore::new(temp.path()).unwrap();
        let data = store
            .create("D:\\work", "openai", "gpt-test", "high")
            .unwrap();
        std::thread::sleep(std::time::Duration::from_millis(5));

        let meta = store
            .set_turn_state(&data.meta.id, TURN_STATE_WAITING_APPROVAL)
            .unwrap();
        assert_eq!(meta.turn_state, TURN_STATE_WAITING_APPROVAL);
        assert!(meta.updated_at > data.meta.updated_at);
        assert_eq!(
            store.load(&data.meta.id).unwrap().meta.turn_state,
            TURN_STATE_WAITING_APPROVAL
        );
        assert_eq!(
            store.list().unwrap()[0].turn_state,
            TURN_STATE_WAITING_APPROVAL
        );
    }

    #[test]
    fn missing_turn_state_defaults_to_idle() {
        let temp = tempfile::tempdir().unwrap();
        let store = SessionStore::new(temp.path()).unwrap();
        let data = store
            .create("D:\\work", "openai", "gpt-test", "high")
            .unwrap();
        let path = temp.path().join(&data.meta.id).join("session.json");
        let mut value: Value = serde_json::from_slice(&std::fs::read(&path).unwrap()).unwrap();
        value.as_object_mut().unwrap().remove("turn_state");
        std::fs::write(&path, serde_json::to_vec_pretty(&value).unwrap()).unwrap();

        assert_eq!(
            store.load(&data.meta.id).unwrap().meta.turn_state,
            TURN_STATE_IDLE
        );
    }

    #[test]
    fn appends_events_and_counts_them() {
        let temp = tempfile::tempdir().unwrap();
        let store = SessionStore::new(temp.path()).unwrap();
        let data = store
            .create("D:\\work", "openai", "gpt-test", "high")
            .unwrap();
        let event = CoreEvent::TurnStarted {
            turn_id: "t".to_owned(),
        };
        store.append_event(&data.meta.id, 1, &event).unwrap();
        store.append_event(&data.meta.id, 2, &event).unwrap();
        assert_eq!(store.event_count(&data.meta.id).unwrap(), 2);
    }

    #[test]
    fn reads_events_in_order_with_schema_version() {
        let temp = tempfile::tempdir().unwrap();
        let store = SessionStore::new(temp.path()).unwrap();
        let data = store
            .create("D:\\work", "openai", "gpt-test", "high")
            .unwrap();
        let first = CoreEvent::TurnStarted {
            turn_id: "first".to_owned(),
        };
        let second = CoreEvent::TurnStarted {
            turn_id: "second".to_owned(),
        };
        store.append_event(&data.meta.id, 1, &first).unwrap();
        store.append_event(&data.meta.id, 2, &second).unwrap();

        let events = store.read_events(&data.meta.id).unwrap();
        assert_eq!(
            events.iter().map(|event| event.seq).collect::<Vec<_>>(),
            vec![1, 2]
        );
        assert!(
            events
                .iter()
                .all(|event| event.schema_version == EVENT_SCHEMA_VERSION)
        );
        assert_eq!(events[0].event, first);
        assert_eq!(events[1].event, second);
    }

    #[test]
    fn reads_legacy_event_without_schema_version() {
        let temp = tempfile::tempdir().unwrap();
        let store = SessionStore::new(temp.path()).unwrap();
        let data = store
            .create("D:\\work", "openai", "gpt-test", "high")
            .unwrap();
        let path = temp.path().join(&data.meta.id).join("events.jsonl");
        let line = serde_json::json!({
            "seq": 7,
            "at": "2026-10-19T00:00:00.000Z",
            "event": { "type": "turn.started", "data": { "turn_id": "old" } }
        });
        std::fs::write(path, format!("{line}\n")).unwrap();

        let events = store.read_events(&data.meta.id).unwrap();
        assert_eq!(events.len(), 1);
        assert_eq!(events[0].schema_version, EVENT_SCHEMA_VERSION);
        assert_eq!(
            events[0].event,
            CoreEvent::TurnStarted {
                turn_id: "old".to_owned()
            }
        );
    }

    fn stored_event(seq: u64, event: CoreEvent) -> StoredEvent {
        StoredEvent {
            seq,
            at: "2026-10-19T00:00:00.000Z".to_owned(),
            schema_version: EVENT_SCHEMA_VERSION,
            event,
        }
    }

    #[test]
    fn projects_messages_in_event_order() {
        let events = vec![
            stored_event(
                1,
                CoreEvent::UserMessage {
                    turn_id: "turn-1".to_owned(),
                    text: "fix it".to_owned(),
                },
            ),
            stored_event(
                2,
                CoreEvent::TurnCompleted {
                    turn_id: "turn-1".to_owned(),
                    status: "completed".to_owned(),
                    text: "done".to_owned(),
                },
            ),
            stored_event(
                3,
                CoreEvent::ToolCompleted {
                    turn_id: "turn-1".to_owned(),
                    call_id: "call-1".to_owned(),
                    name: "read_file".to_owned(),
                    ok: true,
                    summary: "read a file".to_owned(),
                    output: Some("file text".to_owned()),
                    diff: None,
                },
            ),
        ];

        assert_eq!(
            project_messages(&events),
            vec![
                serde_json::json!({"role": "user", "content": "fix it"}),
                serde_json::json!({"role": "assistant", "content": "done"}),
                serde_json::json!({
                    "role": "tool",
                    "tool_call_id": "call-1",
                    "name": "read_file",
                    "content": "file text",
                    "ok": true,
                }),
            ]
        );
    }

    #[test]
    fn projects_legacy_events_without_user_message() {
        let events = vec![
            stored_event(
                1,
                CoreEvent::TurnStarted {
                    turn_id: "old-turn".to_owned(),
                },
            ),
            stored_event(
                2,
                CoreEvent::TurnCompleted {
                    turn_id: "old-turn".to_owned(),
                    status: "completed".to_owned(),
                    text: "   ".to_owned(),
                },
            ),
            stored_event(
                3,
                CoreEvent::TurnCompleted {
                    turn_id: "old-turn".to_owned(),
                    status: "completed".to_owned(),
                    text: "old answer".to_owned(),
                },
            ),
            stored_event(
                4,
                CoreEvent::ToolCompleted {
                    turn_id: "old-turn".to_owned(),
                    call_id: "old-call".to_owned(),
                    name: "grep".to_owned(),
                    ok: false,
                    summary: "no matches".to_owned(),
                    output: None,
                    diff: None,
                },
            ),
        ];

        assert_eq!(
            project_messages(&events),
            vec![
                serde_json::json!({"role": "assistant", "content": "old answer"}),
                serde_json::json!({
                    "role": "tool",
                    "tool_call_id": "old-call",
                    "name": "grep",
                    "content": "no matches",
                    "ok": false,
                }),
            ]
        );
    }

    #[test]
    fn reads_events_then_projects_messages() {
        let temp = tempfile::tempdir().unwrap();
        let store = SessionStore::new(temp.path()).unwrap();
        let data = store
            .create("D:\\work", "openai", "gpt-test", "high")
            .unwrap();
        store
            .append_event(
                &data.meta.id,
                1,
                &CoreEvent::UserMessage {
                    turn_id: "turn-1".to_owned(),
                    text: "fix it".to_owned(),
                },
            )
            .unwrap();
        store
            .append_event(
                &data.meta.id,
                2,
                &CoreEvent::TurnCompleted {
                    turn_id: "turn-1".to_owned(),
                    status: "completed".to_owned(),
                    text: "done".to_owned(),
                },
            )
            .unwrap();

        assert_eq!(
            store.project_messages(&data.meta.id).unwrap(),
            vec![
                serde_json::json!({"role": "user", "content": "fix it"}),
                serde_json::json!({"role": "assistant", "content": "done"}),
            ]
        );
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

    #[test]
    fn delete_removes_only_the_selected_session() {
        let temp = tempfile::tempdir().unwrap();
        let store = SessionStore::new(temp.path()).unwrap();
        let first = store
            .create("D:\\one", "openai", "gpt-test", "high")
            .unwrap();
        let second = store
            .create("D:\\two", "openai", "gpt-test", "high")
            .unwrap();

        store.delete(&first.meta.id).unwrap();

        assert!(matches!(
            store.load(&first.meta.id),
            Err(StoreError::NotFound(_))
        ));
        assert_eq!(store.list().unwrap()[0].id, second.meta.id);
        assert!(matches!(
            store.delete("..\\.."),
            Err(StoreError::NotFound(_))
        ));
    }

    #[test]
    fn rename_overrides_derived_task_and_survives_save() {
        let temp = tempfile::tempdir().unwrap();
        let store = SessionStore::new(temp.path()).unwrap();
        let mut data = store
            .create("D:\\work", "openai", "gpt-test", "high")
            .unwrap();
        data.messages.push(serde_json::json!({
            "role": "user",
            "content": [{"type": "text", "data": "original prompt"}]
        }));
        store.save(&data).unwrap();

        let meta = store.rename(&data.meta.id, "  Renamed task  ").unwrap();
        assert_eq!(meta.name.as_deref(), Some("Renamed task"));
        assert_eq!(meta.task.as_deref(), Some("Renamed task"));

        let mut data = store.load(&data.meta.id).unwrap();
        data.messages.push(serde_json::json!({
            "role": "assistant",
            "content": [{"type": "text", "data": "done"}]
        }));
        store.save(&data).unwrap();
        assert_eq!(
            store.load(&data.meta.id).unwrap().meta.task.as_deref(),
            Some("Renamed task")
        );
    }
}
