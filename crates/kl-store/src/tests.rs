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
    // A saved empty state is authoritative: a rewound-to-empty branch
    // must not be resurrected from the old events.
    assert!(store.load(&data.meta.id).unwrap().messages.is_empty());
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

#[test]
fn read_rollout_tolerates_a_torn_trailing_line() {
    let temp = tempfile::tempdir().unwrap();
    let store = SessionStore::new(temp.path()).unwrap();
    let data = store
        .create("D:/work", "openai", "gpt-test", "high")
        .unwrap();
    store
        .append_event(
            &data.meta.id,
            1,
            &CoreEvent::TurnStarted {
                turn_id: "t1".to_owned(),
            },
        )
        .unwrap();
    let path = store.resolve_path(&data.meta.id).unwrap();
    let mut file = OpenOptions::new().append(true).open(&path).unwrap();
    file.write_all(b"{\"seq\": 2, \"at\": ").unwrap();
    drop(file);

    assert_eq!(store.read_events(&data.meta.id).unwrap().len(), 1);
    assert_eq!(store.last_seq(&data.meta.id).unwrap(), 1);
    assert!(store.load(&data.meta.id).is_ok());

    store
        .append_event(
            &data.meta.id,
            2,
            &CoreEvent::TurnStarted {
                turn_id: "t2".to_owned(),
            },
        )
        .unwrap();
    let events = store.read_events(&data.meta.id).unwrap();
    assert_eq!(events.len(), 2);
    assert_eq!(events[1].seq, 2);
}

#[test]
fn append_keeps_a_complete_record_missing_its_newline() {
    let temp = tempfile::tempdir().unwrap();
    let store = SessionStore::new(temp.path()).unwrap();
    let data = store
        .create("D:/work", "openai", "gpt-test", "high")
        .unwrap();
    let path = store.resolve_path(&data.meta.id).unwrap();
    let record = StoredEvent {
        seq: 1,
        at: "2026-01-01T00:00:00.000Z".to_owned(),
        schema_version: EVENT_SCHEMA_VERSION,
        event: CoreEvent::TurnStarted {
            turn_id: "t1".to_owned(),
        },
    };
    fs::write(&path, serde_json::to_vec(&record).unwrap()).unwrap();

    store
        .append_event(
            &data.meta.id,
            2,
            &CoreEvent::TurnStarted {
                turn_id: "t2".to_owned(),
            },
        )
        .unwrap();
    let events = store.read_events(&data.meta.id).unwrap();
    assert_eq!(events.len(), 2);
    assert_eq!(events[0].seq, 1);
    assert_eq!(events[1].seq, 2);
}

#[test]
fn read_rollout_still_fails_on_a_corrupt_middle_line() {
    let temp = tempfile::tempdir().unwrap();
    let store = SessionStore::new(temp.path()).unwrap();
    let data = store
        .create("D:/work", "openai", "gpt-test", "high")
        .unwrap();
    store
        .append_event(
            &data.meta.id,
            1,
            &CoreEvent::TurnStarted {
                turn_id: "t1".to_owned(),
            },
        )
        .unwrap();
    let path = store.resolve_path(&data.meta.id).unwrap();
    let mut file = OpenOptions::new().append(true).open(&path).unwrap();
    file.write_all(b"not json\n").unwrap();
    drop(file);

    store
        .append_event(
            &data.meta.id,
            2,
            &CoreEvent::TurnStarted {
                turn_id: "t2".to_owned(),
            },
        )
        .unwrap();

    assert!(store.read_events(&data.meta.id).is_err());
}

#[test]
fn load_keeps_an_empty_state_empty() {
    let temp = tempfile::tempdir().unwrap();
    let store = SessionStore::new(temp.path()).unwrap();
    let data = store
        .create("D:/work", "openai", "gpt-test", "high")
        .unwrap();
    store
        .append_event(
            &data.meta.id,
            1,
            &CoreEvent::UserMessage {
                turn_id: "t1".to_owned(),
                text: "hidden".to_owned(),
            },
        )
        .unwrap();
    let mut data = store.load(&data.meta.id).unwrap();
    assert_eq!(data.messages.len(), 1);
    data.messages.clear();
    store.save(&data).unwrap();

    assert!(store.load(&data.meta.id).unwrap().messages.is_empty());
}

#[test]
fn load_keeps_repeated_assistant_text() {
    let temp = tempfile::tempdir().unwrap();
    let store = SessionStore::new(temp.path()).unwrap();
    let data = store
        .create("D:/work", "openai", "gpt-test", "high")
        .unwrap();
    let id = data.meta.id;
    store
        .append_event(
            &id,
            1,
            &CoreEvent::UserMessage {
                turn_id: "t1".to_owned(),
                text: "hi".to_owned(),
            },
        )
        .unwrap();
    store
        .append_event(
            &id,
            2,
            &CoreEvent::TurnCompleted {
                turn_id: "t1".to_owned(),
                status: "completed".to_owned(),
                text: "same".to_owned(),
                input_tokens: 1,
                output_tokens: 1,
                cost_usd: None,
            },
        )
        .unwrap();
    let data = store.load(&id).unwrap();
    assert_eq!(data.messages.len(), 2);
    store.save(&data).unwrap();

    store
        .append_event(
            &id,
            3,
            &CoreEvent::UserMessage {
                turn_id: "t2".to_owned(),
                text: "again".to_owned(),
            },
        )
        .unwrap();
    store
        .append_event(
            &id,
            4,
            &CoreEvent::TurnCompleted {
                turn_id: "t2".to_owned(),
                status: "completed".to_owned(),
                text: "same".to_owned(),
                input_tokens: 1,
                output_tokens: 1,
                cost_usd: None,
            },
        )
        .unwrap();

    let loaded = store.load(&id).unwrap();
    assert_eq!(loaded.messages.len(), 4);
    assert_eq!(message_text(&loaded.messages[3]), "same");
}

#[test]
fn concurrent_save_does_not_drop_appended_events() {
    let temp = tempfile::tempdir().unwrap();
    let store = SessionStore::new(temp.path()).unwrap();
    let data = store
        .create("D:/work", "openai", "gpt-test", "high")
        .unwrap();
    let id = data.meta.id.clone();
    std::thread::scope(|scope| {
        let writer = store.clone();
        let write_id = id.clone();
        scope.spawn(move || {
            for seq in 1..=40 {
                writer
                    .append_event(
                        &write_id,
                        seq,
                        &CoreEvent::TurnStarted {
                            turn_id: format!("t{seq}"),
                        },
                    )
                    .unwrap();
            }
        });
        let saver = store.clone();
        let save_data = data.clone();
        scope.spawn(move || {
            for _ in 0..40 {
                saver.save(&save_data).unwrap();
            }
        });
    });

    assert_eq!(store.read_events(&id).unwrap().len(), 40);
}
