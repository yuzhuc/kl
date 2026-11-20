//! Session persistence in a Codex-style home layout:
//! `sessions/YYYY/MM/DD/rollout-<stamp>-<id>.jsonl` files (meta line, event
//! lines, optional state line), plus `session_index.jsonl` and `history.jsonl`
//! at the home root.

use chrono::{SecondsFormat, Utc};
use kl_protocol::CoreEvent;
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::collections::HashMap;
use std::fs::{self, OpenOptions};
use std::io::{BufRead, BufReader, Read, Seek, SeekFrom, Write};
use std::path::{Path, PathBuf};
use std::sync::Mutex;

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
    pub approval_rules: Vec<String>,
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
    paths: std::sync::Arc<Mutex<HashMap<String, PathBuf>>>,
    // Serializes every rollout write and the read half of read-modify-write.
    // Lock order: sessions -> sequences -> lock -> paths; no store method
    // acquires a lock outside this struct.
    lock: std::sync::Arc<Mutex<()>>,
}

#[derive(Debug, Default)]
struct RolloutContent {
    meta: Option<SessionMeta>,
    events: Vec<StoredEvent>,
    state: Option<RolloutState>,
}

#[derive(Debug, Serialize, Deserialize)]
struct RolloutState {
    /// Highest event seq the persisted projection covers.
    #[serde(default)]
    through_seq: u64,
    #[serde(default)]
    messages: Vec<Value>,
    #[serde(default)]
    approval_rules: Vec<String>,
}

impl SessionStore {
    pub fn new(root: impl AsRef<Path>) -> Result<Self, StoreError> {
        let root = root.as_ref().to_path_buf();
        fs::create_dir_all(&root)?;
        let store = Self {
            root,
            paths: std::sync::Arc::new(Mutex::new(HashMap::new())),
            lock: std::sync::Arc::new(Mutex::new(())),
        };
        fs::create_dir_all(store.sessions_root())?;
        fs::create_dir_all(store.archived_root())?;
        store.migrate_legacy()?;
        Ok(store)
    }

    pub fn root(&self) -> &Path {
        &self.root
    }

    fn write_guard(&self) -> std::sync::MutexGuard<'_, ()> {
        self.lock.lock().unwrap_or_else(|error| error.into_inner())
    }

    fn sessions_root(&self) -> PathBuf {
        self.root.join("sessions")
    }

    fn archived_root(&self) -> PathBuf {
        self.root.join("archived_sessions")
    }

    fn index_path(&self) -> PathBuf {
        self.root.join("session_index.jsonl")
    }

    fn history_path(&self) -> PathBuf {
        self.root.join("history.jsonl")
    }

    pub fn create(
        &self,
        workspace: &str,
        provider: &str,
        model: &str,
        reasoning_effort: &str,
    ) -> Result<SessionData, StoreError> {
        let _guard = self.write_guard();
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
            approval_rules: Vec::new(),
        };
        let path = self.rollout_path(&data.meta);
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent)?;
        }
        let mut bytes = serde_json::to_vec(&serde_json::json!({
            "type": "meta",
            "meta": &data.meta,
        }))?;
        bytes.push(b'\n');
        fs::write(&path, bytes)?;
        self.paths
            .lock()
            .unwrap()
            .insert(data.meta.id.clone(), path);
        self.append_index(&data.meta)?;
        Ok(data)
    }

    pub fn load(&self, id: &str) -> Result<SessionData, StoreError> {
        let _guard = self.write_guard();
        self.load_unlocked(id)
    }

    fn load_unlocked(&self, id: &str) -> Result<SessionData, StoreError> {
        let path = self
            .resolve_path(id)
            .ok_or_else(|| StoreError::NotFound(id.to_owned()))?;
        let content = read_rollout(&path)?;
        let meta = content
            .meta
            .ok_or_else(|| StoreError::NotFound(id.to_owned()))?;
        // The snapshot is authoritative; only replay the events it does not cover.
        let (messages, approval_rules) = match content.state {
            Some(state) => {
                let newer: Vec<StoredEvent> = content
                    .events
                    .iter()
                    .filter(|event| event.seq > state.through_seq)
                    .cloned()
                    .collect();
                let mut messages = state.messages;
                // Compaction saves its state before emitting the event, so the
                // summary may already be in the snapshot.
                for message in project_messages(&newer) {
                    // The snapshot already holds the compaction summary; equal
                    // assistant/tool text is legitimate and must be kept.
                    if message.get("role").and_then(Value::as_str) == Some("system")
                        && messages.contains(&message)
                    {
                        continue;
                    }
                    messages.push(message);
                }
                (messages, state.approval_rules)
            }
            None => (project_messages(&content.events), Vec::new()),
        };
        Ok(SessionData {
            meta,
            messages,
            approval_rules,
        })
    }

    pub fn list(&self) -> Result<Vec<SessionMeta>, StoreError> {
        let mut sessions = Vec::new();
        collect_meta(&self.sessions_root(), &mut sessions)?;
        sessions.sort_by(|left, right| right.updated_at.cmp(&left.updated_at));
        Ok(sessions)
    }

    pub fn delete(&self, id: &str) -> Result<(), StoreError> {
        let _guard = self.write_guard();
        let id = uuid::Uuid::parse_str(id).map_err(|_| StoreError::NotFound(id.to_owned()))?;
        let id = id.to_string();
        let path = self
            .resolve_path(&id)
            .ok_or_else(|| StoreError::NotFound(id.clone()))?;
        fs::remove_file(path)?;
        self.paths.lock().unwrap().remove(&id);
        // Attachments belong to their session, so they go with it.
        let _ = fs::remove_dir_all(self.root.join("attachments").join(&id));
        Ok(())
    }

    pub fn rename(&self, id: &str, name: &str) -> Result<SessionMeta, StoreError> {
        let name = name.trim();
        if name.is_empty() || name.chars().count() > 160 {
            return Err(StoreError::InvalidName);
        }
        let _guard = self.write_guard();
        let mut data = self.load_unlocked(id)?;
        data.meta.name = Some(name.to_owned());
        self.save_unlocked(&data)?;
        Ok(self.load_unlocked(id)?.meta)
    }

    /// Moves a session between `sessions/` and `archived_sessions/`.
    pub fn set_archived(&self, id: &str, archived: bool) -> Result<(), StoreError> {
        let _guard = self.write_guard();
        let path = self
            .resolve_path(id)
            .ok_or_else(|| StoreError::NotFound(id.to_owned()))?;
        let target = if archived {
            self.archived_root()
                .join(path.file_name().unwrap_or_default())
        } else {
            let meta =
                read_rollout_meta(&path).ok_or_else(|| StoreError::NotFound(id.to_owned()))?;
            self.rollout_path(&meta)
        };
        if path != target {
            if let Some(parent) = target.parent() {
                fs::create_dir_all(parent)?;
            }
            fs::rename(&path, &target)?;
            self.paths.lock().unwrap().insert(id.to_owned(), target);
        }
        Ok(())
    }

    pub fn list_archived(&self) -> Result<Vec<SessionMeta>, StoreError> {
        let mut sessions = Vec::new();
        collect_meta(&self.archived_root(), &mut sessions)?;
        sessions.sort_by(|left, right| right.updated_at.cmp(&left.updated_at));
        Ok(sessions)
    }

    pub fn save(&self, data: &SessionData) -> Result<(), StoreError> {
        let _guard = self.write_guard();
        self.save_unlocked(data)
    }

    fn save_unlocked(&self, data: &SessionData) -> Result<(), StoreError> {
        let path = self
            .resolve_path(&data.meta.id)
            .unwrap_or_else(|| self.rollout_path(&data.meta));
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent)?;
        }
        let events = if path.exists() {
            read_rollout(&path)?.events
        } else {
            Vec::new()
        };
        let mut meta = data.meta.clone();
        meta.message_count = data.messages.len();
        meta.updated_at = now();
        let (task, prompt, last_message) = summarize_messages(&data.messages);
        meta.task = meta.name.clone().or(task);
        meta.prompt = prompt;
        meta.last_message = last_message;
        let through_seq = events.iter().map(|event| event.seq).max().unwrap_or(0);
        let mut bytes = serde_json::to_vec(&serde_json::json!({
            "type": "meta",
            "meta": &meta,
        }))?;
        bytes.push(b'\n');
        for event in &events {
            bytes.extend(serde_json::to_vec(event)?);
            bytes.push(b'\n');
        }
        bytes.extend(serde_json::to_vec(&serde_json::json!({
            "type": "state",
            "through_seq": through_seq,
            "messages": &data.messages,
            "approval_rules": &data.approval_rules,
        }))?);
        bytes.push(b'\n');
        write_atomic(&path, &bytes)?;
        self.paths
            .lock()
            .unwrap()
            .insert(data.meta.id.clone(), path);
        self.append_index(&meta)?;
        Ok(())
    }

    pub fn set_turn_state(
        &self,
        session_id: &str,
        turn_state: &str,
    ) -> Result<SessionMeta, StoreError> {
        let _guard = self.write_guard();
        let mut data = self.load_unlocked(session_id)?;
        data.meta.turn_state = turn_state.to_owned();
        self.save_unlocked(&data)?;
        Ok(self.load_unlocked(session_id)?.meta)
    }

    pub fn append_event(
        &self,
        session_id: &str,
        seq: u64,
        event: &CoreEvent,
    ) -> Result<(), StoreError> {
        let _guard = self.write_guard();
        let path = self
            .resolve_path(session_id)
            .ok_or_else(|| StoreError::NotFound(session_id.to_owned()))?;
        let record = EventRecord {
            seq,
            at: now(),
            schema_version: EVENT_SCHEMA_VERSION,
            event,
        };
        let mut line = serde_json::to_vec(&record)?;
        line.push(b'\n');
        // Not `append(true)`: Windows then opens the file without the write
        // access that healing a torn tail needs. The store lock serializes us.
        let mut file = OpenOptions::new()
            .create(true)
            .truncate(false)
            .read(true)
            .write(true)
            .open(&path)?;
        heal_torn_tail(&mut file, &path)?;
        file.seek(SeekFrom::End(0))?;
        file.write_all(&line)?;
        file.flush()?;
        if let CoreEvent::UserMessage { text, .. } = event {
            self.append_history(session_id, text)?;
        }
        Ok(())
    }

    pub fn read_events(&self, session_id: &str) -> Result<Vec<StoredEvent>, StoreError> {
        let _guard = self.write_guard();
        match self.resolve_path(session_id) {
            Some(path) => Ok(read_rollout(&path)?.events),
            None => Ok(Vec::new()),
        }
    }

    pub fn project_messages(&self, session_id: &str) -> Result<Vec<Value>, StoreError> {
        let events = self.read_events(session_id)?;
        Ok(project_messages(&events))
    }

    pub fn last_seq(&self, session_id: &str) -> Result<u64, StoreError> {
        let _guard = self.write_guard();
        match self.resolve_path(session_id) {
            Some(path) => Ok(read_rollout(&path)?
                .events
                .iter()
                .map(|event| event.seq)
                .max()
                .unwrap_or(0)),
            None => Ok(0),
        }
    }

    fn rollout_path(&self, meta: &SessionMeta) -> PathBuf {
        let created = meta.created_at.as_str();
        let date = created.get(0..10).unwrap_or("1970-01-01");
        let stamp = created
            .get(0..19)
            .unwrap_or("1970-01-01T00-00-00")
            .replace(':', "-");
        let mut directory = self.sessions_root();
        for part in date.split('-') {
            directory = directory.join(part);
        }
        directory.join(format!("rollout-{stamp}-{}.jsonl", meta.id))
    }

    fn resolve_path(&self, id: &str) -> Option<PathBuf> {
        if let Some(path) = self.paths.lock().unwrap().get(id) {
            if path.exists() {
                return Some(path.clone());
            }
        }
        let found = find_rollout(&self.sessions_root(), id)
            .or_else(|| find_rollout(&self.archived_root(), id))?;
        self.paths
            .lock()
            .unwrap()
            .insert(id.to_owned(), found.clone());
        Some(found)
    }

    fn append_index(&self, meta: &SessionMeta) -> Result<(), StoreError> {
        let line = serde_json::json!({
            "id": meta.id,
            "thread_name": meta.name.clone().or_else(|| meta.task.clone()).unwrap_or_default(),
            "updated_at": meta.updated_at,
        });
        append_line(&self.index_path(), &line)
    }

    fn append_history(&self, session_id: &str, text: &str) -> Result<(), StoreError> {
        let line = serde_json::json!({
            "session_id": session_id,
            "ts": Utc::now().timestamp(),
            "text": text,
        });
        append_line(&self.history_path(), &line)
    }

    /// Moves sessions written by the earlier `<id>/{session,state}.json` layout
    /// into the dated rollout layout.
    fn migrate_legacy(&self) -> Result<(), StoreError> {
        for base in [self.root.clone(), self.sessions_root()] {
            let Ok(entries) = fs::read_dir(&base) else {
                continue;
            };
            let mut legacy: Vec<PathBuf> = Vec::new();
            for entry in entries.flatten() {
                let directory = entry.path();
                if directory.is_dir() && directory.join("session.json").exists() {
                    legacy.push(directory);
                }
            }
            for directory in legacy {
                let Ok(bytes) = fs::read(directory.join("session.json")) else {
                    continue;
                };
                let Ok(meta) = serde_json::from_slice::<SessionMeta>(&bytes) else {
                    continue;
                };
                let events = read_legacy_events(&directory.join("events.jsonl"));
                let state = fs::read(directory.join("state.json"))
                    .ok()
                    .and_then(|bytes| serde_json::from_slice::<StateFile>(&bytes).ok());
                let path = self.rollout_path(&meta);
                if path.exists() {
                    continue;
                }
                if let Some(parent) = path.parent() {
                    fs::create_dir_all(parent)?;
                }
                let through_seq = events.iter().map(|event| event.seq).max().unwrap_or(0);
                let mut bytes = serde_json::to_vec(&serde_json::json!({
                    "type": "meta",
                    "meta": &meta,
                }))?;
                bytes.push(b'\n');
                for event in &events {
                    bytes.extend(serde_json::to_vec(event)?);
                    bytes.push(b'\n');
                }
                let (messages, approval_rules) = match &state {
                    Some(state) if !state.messages.is_empty() => {
                        (state.messages.clone(), state.approval_rules.clone())
                    }
                    _ => (project_messages(&events), Vec::new()),
                };
                bytes.extend(serde_json::to_vec(&serde_json::json!({
                    "type": "state",
                    "through_seq": through_seq,
                    "messages": messages,
                    "approval_rules": approval_rules,
                }))?);
                bytes.push(b'\n');
                write_atomic(&path, &bytes)?;
                self.paths.lock().unwrap().insert(meta.id.clone(), path);
                self.append_index(&meta)?;
                let _ = fs::remove_dir_all(&directory);
            }
        }
        Ok(())
    }
}

fn is_rollout(path: &Path) -> bool {
    path.extension().and_then(|value| value.to_str()) == Some("jsonl")
        && path
            .file_name()
            .and_then(|value| value.to_str())
            .is_some_and(|name| name.starts_with("rollout-"))
}

fn find_rollout(root: &Path, id: &str) -> Option<PathBuf> {
    let suffix = format!("-{id}.jsonl");
    let entries = fs::read_dir(root).ok()?;
    for entry in entries.flatten() {
        let path = entry.path();
        if path.is_dir() {
            if let Some(found) = find_rollout(&path, id) {
                return Some(found);
            }
        } else if is_rollout(&path)
            && path
                .file_name()
                .and_then(|value| value.to_str())
                .is_some_and(|name| name.ends_with(&suffix))
        {
            return Some(path);
        }
    }
    None
}

fn collect_meta(root: &Path, sessions: &mut Vec<SessionMeta>) -> Result<(), StoreError> {
    if !root.exists() {
        return Ok(());
    }
    for entry in fs::read_dir(root)? {
        let entry = entry?;
        let path = entry.path();
        if path.is_dir() {
            collect_meta(&path, sessions)?;
        } else if is_rollout(&path) {
            if let Some(meta) = read_rollout_meta(&path) {
                sessions.push(meta);
            }
        }
    }
    Ok(())
}

fn read_rollout(path: &Path) -> Result<RolloutContent, StoreError> {
    let file = fs::File::open(path)?;
    let mut content = RolloutContent::default();
    let mut lines = BufReader::new(file).lines();
    while let Some(line) = lines.next() {
        let line = line?;
        if line.trim().is_empty() {
            continue;
        }
        let value: Value = match serde_json::from_str(&line) {
            Ok(value) => value,
            Err(error) => {
                // A crash can leave one torn final line; anything earlier is
                // real corruption.
                if lines
                    .by_ref()
                    .all(|rest| rest.is_ok_and(|line| line.trim().is_empty()))
                {
                    break;
                }
                return Err(error.into());
            }
        };
        match value.get("type").and_then(Value::as_str) {
            Some("meta") => {
                if let Some(meta) = value.get("meta") {
                    content.meta = Some(serde_json::from_value(meta.clone())?);
                }
            }
            Some("state") => content.state = Some(serde_json::from_value(value)?),
            _ => content.events.push(serde_json::from_value(value)?),
        }
    }
    Ok(content)
}

fn read_rollout_meta(path: &Path) -> Option<SessionMeta> {
    let file = fs::File::open(path).ok()?;
    for line in BufReader::new(file).lines() {
        let line = line.ok()?;
        if line.trim().is_empty() {
            continue;
        }
        let value: Value = serde_json::from_str(&line).ok()?;
        if value.get("type").and_then(Value::as_str) == Some("meta") {
            return value
                .get("meta")
                .and_then(|meta| serde_json::from_value(meta.clone()).ok());
        }
        return None;
    }
    None
}

fn read_legacy_events(path: &Path) -> Vec<StoredEvent> {
    let Ok(file) = fs::File::open(path) else {
        return Vec::new();
    };
    BufReader::new(file)
        .lines()
        .map_while(Result::ok)
        .filter(|line| !line.trim().is_empty())
        .filter_map(|line| serde_json::from_str::<StoredEvent>(&line).ok())
        .collect()
}

fn append_line(path: &Path, line: &Value) -> Result<(), StoreError> {
    let mut bytes = serde_json::to_vec(line)?;
    bytes.push(b'\n');
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent)?;
    }
    let mut file = OpenOptions::new().create(true).append(true).open(path)?;
    file.write_all(&bytes)?;
    file.flush()?;
    Ok(())
}

pub fn project_messages(events: &[StoredEvent]) -> Vec<Value> {
    let mut messages = Vec::new();
    let mut turn = None;
    for stored in events {
        match &stored.event {
            CoreEvent::UserMessage { text, .. } => {
                flush_projected_turn(&mut turn, &mut messages);
                messages.push(text_message("user", text));
                turn = Some(ProjectedTurn::default());
            }
            CoreEvent::TurnStarted { .. } => {
                turn.get_or_insert_with(ProjectedTurn::default);
            }
            CoreEvent::ToolStarted {
                call_id,
                name,
                args,
                ..
            } => {
                turn.get_or_insert_with(ProjectedTurn::default)
                    .calls
                    .push(ProjectedCall {
                        id: call_id.clone(),
                        name: name.clone(),
                        arguments: serde_json::to_string(args).unwrap_or_else(|_| "{}".to_owned()),
                    });
            }
            CoreEvent::ToolCompleted {
                call_id,
                name,
                summary,
                output,
                ..
            } => {
                turn.get_or_insert_with(ProjectedTurn::default)
                    .results
                    .push(ProjectedResult {
                        call_id: call_id.clone(),
                        name: name.clone(),
                        text: output
                            .as_deref()
                            .filter(|value| !value.is_empty())
                            .unwrap_or(summary)
                            .to_owned(),
                    });
            }
            CoreEvent::TurnCompleted { text, .. } if !text.trim().is_empty() => {
                turn.get_or_insert_with(ProjectedTurn::default).final_text = text.clone();
            }
            CoreEvent::ContextCompacted {
                summary: Some(summary),
                ..
            } if !summary.trim().is_empty() => {
                flush_projected_turn(&mut turn, &mut messages);
                messages.push(text_message(
                    "system",
                    &format!("[KL conversation summary]\n{summary}"),
                ));
            }
            _ => {}
        }
    }
    flush_projected_turn(&mut turn, &mut messages);
    messages
}

#[derive(Debug, Default)]
struct ProjectedTurn {
    calls: Vec<ProjectedCall>,
    results: Vec<ProjectedResult>,
    final_text: String,
}

#[derive(Debug)]
struct ProjectedCall {
    id: String,
    name: String,
    arguments: String,
}

#[derive(Debug)]
struct ProjectedResult {
    call_id: String,
    name: String,
    text: String,
}

fn flush_projected_turn(turn: &mut Option<ProjectedTurn>, messages: &mut Vec<Value>) {
    let Some(mut turn) = turn.take() else {
        return;
    };
    for result in &turn.results {
        if !turn.calls.iter().any(|call| call.id == result.call_id) {
            turn.calls.push(ProjectedCall {
                id: result.call_id.clone(),
                name: result.name.clone(),
                arguments: "{}".to_owned(),
            });
        }
    }
    turn.calls
        .retain(|call| turn.results.iter().any(|result| result.call_id == call.id));
    if !turn.calls.is_empty() {
        messages.push(serde_json::json!({
            "role": "assistant",
            "content": [],
            "tool_calls": turn.calls.iter().map(|call| serde_json::json!({
                "id": call.id,
                "name": call.name,
                "arguments": call.arguments,
            })).collect::<Vec<_>>(),
        }));
        for result in &turn.results {
            messages.push(serde_json::json!({
                "role": "tool",
                "tool_call_id": result.call_id,
                "content": [{"type": "text", "data": result.text}],
            }));
        }
    }
    if !turn.final_text.trim().is_empty() {
        messages.push(text_message("assistant", &turn.final_text));
    }
}

fn text_message(role: &str, text: &str) -> Value {
    serde_json::json!({
        "role": role,
        "content": [{"type": "text", "data": text}],
    })
}

#[derive(Debug, Serialize, Deserialize)]
struct StateFile {
    #[serde(default)]
    messages: Vec<Value>,
    #[serde(default)]
    approval_rules: Vec<String>,
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

/// Repairs a rollout whose last append was torn so the next event cannot be
/// concatenated onto an unterminated record.
fn heal_torn_tail(file: &mut fs::File, path: &Path) -> std::io::Result<()> {
    if file.metadata()?.len() == 0 {
        return Ok(());
    }
    file.seek(SeekFrom::End(-1))?;
    let mut last = [0u8; 1];
    file.read_exact(&mut last)?;
    if last[0] == b'\n' {
        return Ok(());
    }
    // Only reached after a crash, so reading the file once is acceptable.
    let bytes = fs::read(path)?;
    let cut = bytes
        .iter()
        .rposition(|byte| *byte == b'\n')
        .map_or(0, |index| index + 1);
    if serde_json::from_slice::<StoredEvent>(&bytes[cut..]).is_ok() {
        file.write_all(b"\n")?;
    } else {
        file.set_len(cut as u64)?;
    }
    Ok(())
}

fn write_atomic(path: &Path, bytes: &[u8]) -> std::io::Result<()> {
    // The fixed temp name is safe because saves of one session are serialized;
    // concurrent processes on the same home are not supported.
    let temporary = path.with_extension("tmp");
    let mut file = fs::File::create(&temporary)?;
    file.write_all(bytes)?;
    file.sync_all()?;
    drop(file);
    // rename replaces the destination atomically on Unix and Windows.
    fs::rename(temporary, path)
}

#[cfg(test)]
mod tests;
