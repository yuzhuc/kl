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
use std::io::{BufRead, BufReader, Write};
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
        };
        fs::create_dir_all(store.sessions_root())?;
        fs::create_dir_all(store.archived_root())?;
        store.migrate_legacy()?;
        Ok(store)
    }

    pub fn root(&self) -> &Path {
        &self.root
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
        let path = self
            .resolve_path(id)
            .ok_or_else(|| StoreError::NotFound(id.to_owned()))?;
        let content = read_rollout(&path)?;
        let meta = content
            .meta
            .ok_or_else(|| StoreError::NotFound(id.to_owned()))?;
        // The snapshot is authoritative; only replay the events it does not cover.
        let (mut messages, approval_rules) = match content.state {
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
                    if !messages.contains(&message) {
                        messages.push(message);
                    }
                }
                (messages, state.approval_rules)
            }
            None => (project_messages(&content.events), Vec::new()),
        };
        if messages.is_empty() {
            messages = project_messages(&content.events);
        }
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
        let mut data = self.load(id)?;
        data.meta.name = Some(name.to_owned());
        self.save(&data)?;
        Ok(self.load(id)?.meta)
    }

    /// Moves a session between `sessions/` and `archived_sessions/`.
    pub fn set_archived(&self, id: &str, archived: bool) -> Result<(), StoreError> {
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
        let mut data = self.load(session_id)?;
        data.meta.turn_state = turn_state.to_owned();
        self.save(&data)?;
        Ok(self.load(session_id)?.meta)
    }

    pub fn append_event(
        &self,
        session_id: &str,
        seq: u64,
        event: &CoreEvent,
    ) -> Result<(), StoreError> {
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
        let mut file = OpenOptions::new().create(true).append(true).open(path)?;
        file.write_all(&line)?;
        file.flush()?;
        if let CoreEvent::UserMessage { text, .. } = event {
            self.append_history(session_id, text)?;
        }
        Ok(())
    }

    pub fn read_events(&self, session_id: &str) -> Result<Vec<StoredEvent>, StoreError> {
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
    for line in BufReader::new(file).lines() {
        let line = line?;
        if line.trim().is_empty() {
            continue;
        }
        let value: Value = serde_json::from_str(&line)?;
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
    fn approval_rules_round_trip() {
        let temp = tempfile::tempdir().unwrap();
        let store = SessionStore::new(temp.path()).unwrap();
        let mut data = store
            .create("D:\\work", "openai", "gpt-test", "high")
            .unwrap();
        data.approval_rules.push("shell:echo hi".to_owned());
        store.save(&data).unwrap();

        let loaded = store.load(&data.meta.id).unwrap();
        assert_eq!(loaded.approval_rules, vec!["shell:echo hi"]);
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
        let path = store.resolve_path(&data.meta.id).unwrap();
        let text = std::fs::read_to_string(&path).unwrap();
        let mut value: Value = serde_json::from_str(text.lines().next().unwrap()).unwrap();
        value["meta"].as_object_mut().unwrap().remove("turn_state");
        std::fs::write(&path, format!("{value}\n")).unwrap();

        assert_eq!(
            store.load(&data.meta.id).unwrap().meta.turn_state,
            TURN_STATE_IDLE
        );
    }

    #[test]
    fn reports_last_event_sequence() {
        let temp = tempfile::tempdir().unwrap();
        let store = SessionStore::new(temp.path()).unwrap();
        let data = store
            .create("D:\\work", "openai", "gpt-test", "high")
            .unwrap();
        let event = CoreEvent::TurnStarted {
            turn_id: "t".to_owned(),
        };
        assert_eq!(store.last_seq(&data.meta.id).unwrap(), 0);
        store.append_event(&data.meta.id, 1, &event).unwrap();
        store.append_event(&data.meta.id, 4, &event).unwrap();
        assert_eq!(store.last_seq(&data.meta.id).unwrap(), 4);
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
        let path = store.resolve_path(&data.meta.id).unwrap();
        let line = serde_json::json!({
            "seq": 7,
            "at": "2026-10-19T00:00:00.000Z",
            "event": { "type": "turn.started", "data": { "turn_id": "old" } }
        });
        let mut bytes = std::fs::read(&path).unwrap();
        bytes.extend_from_slice(format!("{line}\n").as_bytes());
        std::fs::write(&path, bytes).unwrap();

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
    fn projects_tool_calls_and_results_as_valid_pairs() {
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
                CoreEvent::ToolStarted {
                    turn_id: "turn-1".to_owned(),
                    call_id: "call-1".to_owned(),
                    name: "read_file".to_owned(),
                    args: serde_json::json!({"path": "a.txt"}),
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
            stored_event(
                4,
                CoreEvent::TurnCompleted {
                    turn_id: "turn-1".to_owned(),
                    status: "completed".to_owned(),
                    text: "done".to_owned(),
                    input_tokens: 0,
                    output_tokens: 0,
                    cost_usd: None,
                },
            ),
        ];

        assert_eq!(
            project_messages(&events),
            vec![
                text_message("user", "fix it"),
                serde_json::json!({
                    "role": "assistant",
                    "content": [],
                    "tool_calls": [{
                        "id": "call-1",
                        "name": "read_file",
                        "arguments": "{\"path\":\"a.txt\"}"
                    }]
                }),
                serde_json::json!({
                    "role": "tool",
                    "tool_call_id": "call-1",
                    "content": [{"type": "text", "data": "file text"}]
                }),
                text_message("assistant", "done"),
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
                    input_tokens: 0,
                    output_tokens: 0,
                    cost_usd: None,
                },
            ),
            stored_event(
                3,
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
            stored_event(
                4,
                CoreEvent::TurnCompleted {
                    turn_id: "old-turn".to_owned(),
                    status: "completed".to_owned(),
                    text: "old answer".to_owned(),
                    input_tokens: 0,
                    output_tokens: 0,
                    cost_usd: None,
                },
            ),
        ];

        assert_eq!(
            project_messages(&events),
            vec![
                serde_json::json!({
                    "role": "assistant",
                    "content": [],
                    "tool_calls": [{
                        "id": "old-call",
                        "name": "grep",
                        "arguments": "{}"
                    }]
                }),
                serde_json::json!({
                    "role": "tool",
                    "tool_call_id": "old-call",
                    "content": [{"type": "text", "data": "no matches"}]
                }),
                text_message("assistant", "old answer"),
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
                    input_tokens: 0,
                    output_tokens: 0,
                    cost_usd: None,
                },
            )
            .unwrap();

        assert_eq!(
            store.project_messages(&data.meta.id).unwrap(),
            vec![
                text_message("user", "fix it"),
                text_message("assistant", "done")
            ]
        );
    }

    #[test]
    fn load_falls_back_to_paired_tools_and_compaction_summary() {
        let temp = tempfile::tempdir().unwrap();
        let store = SessionStore::new(temp.path()).unwrap();
        let data = store
            .create("D:\\work", "openai", "gpt-test", "high")
            .unwrap();
        for (seq, event) in [
            (
                1,
                CoreEvent::UserMessage {
                    turn_id: "turn-1".to_owned(),
                    text: "fix it".to_owned(),
                },
            ),
            (
                2,
                CoreEvent::ToolStarted {
                    turn_id: "turn-1".to_owned(),
                    call_id: "call-1".to_owned(),
                    name: "read_file".to_owned(),
                    args: serde_json::json!({"path": "a.txt"}),
                },
            ),
            (
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
            (
                4,
                CoreEvent::TurnCompleted {
                    turn_id: "turn-1".to_owned(),
                    status: "completed".to_owned(),
                    text: "done".to_owned(),
                    input_tokens: 0,
                    output_tokens: 0,
                    cost_usd: None,
                },
            ),
            (
                5,
                CoreEvent::ContextCompacted {
                    trigger: "auto".to_owned(),
                    omitted_messages: 2,
                    estimated_tokens: 100,
                    summary_used: true,
                    summary: Some("Earlier work summary".to_owned()),
                },
            ),
        ] {
            store.append_event(&data.meta.id, seq, &event).unwrap();
        }
        let path = store.resolve_path(&data.meta.id).unwrap();
        let text = std::fs::read_to_string(&path).unwrap();
        let kept: Vec<&str> = text
            .lines()
            .filter(|line| !line.contains("\"type\":\"state\""))
            .collect();
        std::fs::write(&path, format!("{}\n", kept.join("\n"))).unwrap();
        let messages = store.load(&data.meta.id).unwrap().messages;
        let roles: Vec<_> = messages
            .iter()
            .map(|message| message["role"].as_str().unwrap())
            .collect();
        assert_eq!(
            roles,
            vec!["user", "assistant", "tool", "assistant", "system"]
        );
        assert_eq!(
            messages[1]["tool_calls"][0]["id"],
            messages[2]["tool_call_id"]
        );
        assert_eq!(
            messages[4]["content"][0]["data"],
            "[KL conversation summary]\nEarlier work summary"
        );

        let mut bytes = std::fs::read(&path).unwrap();
        bytes.extend_from_slice(b"{\"type\":\"state\",\"through_seq\":5,\"messages\":[]}\n");
        std::fs::write(&path, bytes).unwrap();
        assert_eq!(store.load(&data.meta.id).unwrap().messages, messages);
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

    #[test]
    fn legacy_session_directories_migrate_to_rollouts() {
        let temp = tempfile::tempdir().unwrap();
        let id = "11111111-1111-4111-8111-111111111111";
        let legacy = temp.path().join("sessions").join(id);
        std::fs::create_dir_all(&legacy).unwrap();
        let meta = serde_json::json!({
            "id": id,
            "workspace": "D:\\work",
            "provider": "openai",
            "model": "gpt-test",
            "created_at": "2026-01-02T03:04:05Z",
            "updated_at": "2026-01-02T03:05:00Z",
            "message_count": 1,
            "status": "completed"
        });
        std::fs::write(legacy.join("session.json"), meta.to_string()).unwrap();
        std::fs::write(
            legacy.join("state.json"),
            serde_json::json!({
                "messages": [{
                    "role": "user",
                    "content": [{"type": "text", "data": "legacy prompt"}]
                }],
                "approval_rules": []
            })
            .to_string(),
        )
        .unwrap();
        std::fs::write(
            legacy.join("events.jsonl"),
            format!(
                "{}
",
                serde_json::json!({
                    "seq": 1,
                    "at": "2026-01-02T03:04:06Z",
                    "schema_version": 1,
                    "event": {"type": "user.message", "turn_id": "turn-1", "text": "legacy prompt"}
                })
            ),
        )
        .unwrap();

        let store = SessionStore::new(temp.path()).unwrap();
        assert!(!legacy.exists());
        let rollout = temp
            .path()
            .join("sessions/2026/01/02")
            .join(format!("rollout-2026-01-02T03-04-05-{id}.jsonl"));
        assert!(rollout.exists());
        let loaded = store.load(id).unwrap();
        assert_eq!(loaded.messages.len(), 1);
        assert_eq!(loaded.meta.workspace, "D:\\work");
        assert_eq!(store.list().unwrap().len(), 1);
        let index = std::fs::read_to_string(temp.path().join("session_index.jsonl")).unwrap();
        assert!(index.contains(id));
    }

    #[test]
    fn archived_sessions_move_between_roots() {
        let temp = tempfile::tempdir().unwrap();
        let store = SessionStore::new(temp.path()).unwrap();
        let data = store
            .create("D:/work", "openai", "gpt-test", "high")
            .unwrap();
        store.save(&data).unwrap();

        store.set_archived(&data.meta.id, true).unwrap();
        assert!(store.list().unwrap().is_empty());
        assert_eq!(store.list_archived().unwrap().len(), 1);
        assert_eq!(store.load(&data.meta.id).unwrap().meta.id, data.meta.id);
        assert!(
            temp.path()
                .join("archived_sessions")
                .read_dir()
                .unwrap()
                .any(|entry| entry.unwrap().path().is_file())
        );

        store.set_archived(&data.meta.id, false).unwrap();
        assert_eq!(store.list().unwrap().len(), 1);
        assert!(store.list_archived().unwrap().is_empty());
    }

    #[test]
    fn load_does_not_duplicate_a_summary_already_in_state() {
        let temp = tempfile::tempdir().unwrap();
        let store = SessionStore::new(temp.path()).unwrap();
        let mut data = store
            .create("D:/work", "openai", "gpt-test", "high")
            .unwrap();
        let summary = "[KL conversation summary]
old rounds";
        data.messages.push(serde_json::json!({
            "role": "user",
            "content": [{"type": "text", "data": "hi"}]
        }));
        data.messages.push(serde_json::json!({
            "role": "system",
            "content": [{"type": "text", "data": summary}]
        }));
        store.save(&data).unwrap();
        store
            .append_event(
                &data.meta.id,
                1,
                &CoreEvent::ContextCompacted {
                    trigger: "session.compact".to_owned(),
                    omitted_messages: 2,
                    estimated_tokens: 10,
                    summary_used: true,
                    summary: Some("old rounds".to_owned()),
                },
            )
            .unwrap();

        let loaded = store.load(&data.meta.id).unwrap();
        assert_eq!(loaded.messages.len(), 2);
    }

    #[test]
    fn delete_removes_the_session_attachments() {
        let temp = tempfile::tempdir().unwrap();
        let store = SessionStore::new(temp.path()).unwrap();
        let data = store
            .create("D:/work", "openai", "gpt-test", "high")
            .unwrap();
        store.save(&data).unwrap();
        let attachments = temp.path().join("attachments").join(&data.meta.id);
        std::fs::create_dir_all(&attachments).unwrap();
        std::fs::write(attachments.join("1-notes.txt"), "text").unwrap();

        store.delete(&data.meta.id).unwrap();
        assert!(!attachments.exists());
        assert!(store.load(&data.meta.id).is_err());
    }
}
