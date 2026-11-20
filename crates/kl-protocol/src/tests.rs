use super::*;

#[test]
fn negotiation_prefers_highest_common_version() {
    assert_eq!(negotiate(&[1]), Some(1));
    assert_eq!(negotiate(&[0, 1, 99]), Some(1));
    assert_eq!(negotiate(&[]), None);
    assert_eq!(negotiate(&[99]), None);
}

#[test]
fn user_message_event_round_trips() {
    let event = CoreEvent::UserMessage {
        turn_id: "turn-1".to_owned(),
        text: "fix it".to_owned(),
    };
    let encoded = serde_json::to_value(&event).unwrap();
    assert_eq!(encoded["type"], "user.message");
    let decoded: CoreEvent = serde_json::from_value(encoded).unwrap();
    assert_eq!(decoded, event);
}

#[test]
fn turn_start_agent_is_optional_and_round_trips() {
    let legacy: TurnStartParams = serde_json::from_value(serde_json::json!({
        "session_id": "session-1",
        "prompt": "fix it"
    }))
    .unwrap();
    assert_eq!(legacy.agent, None);

    let params = TurnStartParams {
        session_id: "session-1".to_owned(),
        prompt: "fix it".to_owned(),
        provider: None,
        model: None,
        quality: None,
        agent: Some("plan".to_owned()),
        rewind_from_seq: None,
    };
    let encoded = serde_json::to_value(&params).unwrap();
    assert_eq!(encoded["agent"], "plan");
    let decoded: TurnStartParams = serde_json::from_value(encoded).unwrap();
    assert_eq!(decoded, params);
}

#[test]
fn event_round_trips() {
    let event = CoreEvent::ToolCompleted {
        turn_id: "turn-1".to_owned(),
        call_id: "call-1".to_owned(),
        name: "read_file".to_owned(),
        ok: true,
        summary: "read 3 lines".to_owned(),
        output: None,
        diff: None,
    };
    let encoded = serde_json::to_value(&event).unwrap();
    assert_eq!(encoded["type"], "tool.completed");
    let decoded: CoreEvent = serde_json::from_value(encoded).unwrap();
    assert_eq!(decoded, event);
}

fn fixture(name: &str) -> serde_json::Value {
    let path = std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../../tests/fixtures/protocol")
        .join(name);
    let text = std::fs::read_to_string(&path)
        .unwrap_or_else(|error| panic!("failed to read {}: {error}", path.display()));
    serde_json::from_str(&text).expect("fixture must be valid JSON")
}

#[test]
fn r3_methods_and_error_codes_are_stable() {
    assert_eq!(methods::SERVER_INFO, "server.info");
    assert_eq!(methods::SESSION_ATTACH, "session.attach");
    assert_eq!(methods::SESSION_DETACH, "session.detach");
    assert_eq!(methods::SESSION_LEASE, "session.lease");
    assert_eq!(error_codes::SESSION_BUSY, -32008);
    assert_eq!(error_codes::LEASE_REQUIRED, -32009);
}

#[test]
fn session_attach_params_allow_a_missing_after_seq() {
    let params: SessionAttachParams =
        serde_json::from_value(json!({ "session_id": "session-1" })).unwrap();
    assert_eq!(params.session_id, "session-1");
    assert_eq!(params.after_seq, None);

    let params: SessionAttachParams =
        serde_json::from_value(json!({ "session_id": "session-1", "after_seq": 4 })).unwrap();
    assert_eq!(params.after_seq, Some(4));
}

#[test]
fn session_attach_result_replays_event_records() {
    let result: SessionAttachResult = serde_json::from_value(json!({
        "session_id": "session-1",
        "replay": [
            {
                "session_id": "session-1",
                "seq": 3,
                "event": { "type": "turn.started", "data": { "turn_id": "turn-1" } }
            }
        ],
        "last_seq": 3,
        "lease_held": true
    }))
    .unwrap();
    assert_eq!(result.replay.len(), 1);
    assert_eq!(result.replay[0].seq, 3);
    assert_eq!(
        result.replay[0].event,
        CoreEvent::TurnStarted {
            turn_id: "turn-1".to_owned()
        }
    );
    assert_eq!(result.last_seq, 3);
    assert!(result.lease_held);
}

#[test]
fn shared_fixtures_match_the_r3_contracts() {
    let server_info: ServerInfoResult =
        serde_json::from_value(fixture("server_info_result.json")["result"].clone()).unwrap();
    assert_eq!(server_info.server_name, "kl-core");
    assert_eq!(server_info.protocol_version, PROTOCOL_VERSION);
    assert_eq!(server_info.transport, "stdio");

    let attach: SessionAttachResult =
        serde_json::from_value(fixture("session_attach_result.json")["result"].clone()).unwrap();
    assert_eq!(attach.session_id, "session-1");
    assert_eq!(attach.replay.len(), 1);
    assert_eq!(attach.last_seq, 3);
    assert!(attach.lease_held);

    let detach: SessionDetachResult =
        serde_json::from_value(fixture("session_detach_result.json")["result"].clone()).unwrap();
    assert!(detach.detached);

    let lease: SessionLeaseResult =
        serde_json::from_value(fixture("session_lease_result.json")["result"].clone()).unwrap();
    assert_eq!(lease.session_id, "session-1");
    assert_eq!(lease.expires_at_unix_ms, 1760000060000);
}

#[test]
fn memory_methods_are_stable() {
    assert_eq!(methods::MEMORY_LIST, "memory.list");
    assert_eq!(methods::MEMORY_READ, "memory.read");
    assert_eq!(methods::MEMORY_WRITE, "memory.write");
    assert_eq!(methods::MEMORY_DELETE, "memory.delete");
}

#[test]
fn memory_params_use_snake_case_and_optional_scope() {
    let params: MemoryListParams =
        serde_json::from_value(json!({ "session_id": "session-1" })).unwrap();
    assert_eq!(params.session_id, "session-1");
    assert_eq!(params.scope, None);

    let params: MemoryReadParams = serde_json::from_value(json!({
        "session_id": "session-1",
        "name": "notes.md",
        "scope": "user"
    }))
    .unwrap();
    assert_eq!(params.session_id, "session-1");
    assert_eq!(params.name, "notes.md");
    assert_eq!(params.scope.as_deref(), Some("user"));

    let params: MemoryWriteParams = serde_json::from_value(json!({
        "session_id": "session-1",
        "name": "notes.md",
        "content": "# hello"
    }))
    .unwrap();
    assert_eq!(params.session_id, "session-1");
    assert_eq!(params.content, "# hello");
    assert_eq!(params.scope, None);
}

#[test]
fn shared_fixtures_match_the_memory_contracts() {
    let list: MemoryListResult =
        serde_json::from_value(fixture("memory_list_result.json")["result"].clone()).unwrap();
    assert_eq!(list.entries.len(), 1);
    assert_eq!(list.entries[0].name, "notes.md");
    assert_eq!(list.entries[0].scope, "workspace");
    assert_eq!(list.entries[0].size_bytes, 7);

    let read: MemoryReadResult =
        serde_json::from_value(fixture("memory_read_result.json")["result"].clone()).unwrap();
    assert_eq!(read.name, "notes.md");
    assert_eq!(read.scope, "workspace");
    assert_eq!(read.content, "# hello\n");

    let write: MemoryWriteResult =
        serde_json::from_value(fixture("memory_write_result.json")["result"].clone()).unwrap();
    assert_eq!(write.name, "notes.md");
    assert_eq!(write.scope, "workspace");
    assert_eq!(write.size_bytes, 7);

    let delete: MemoryDeleteResult =
        serde_json::from_value(fixture("memory_delete_result.json")["result"].clone()).unwrap();
    assert_eq!(delete.name, "notes.md");
    assert_eq!(delete.scope, "workspace");
    assert!(delete.deleted);
}
