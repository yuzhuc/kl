//! Session history: pairing stored events with snapshot messages and
//! validating rewind targets.

use crate::rpc_error;
use kl_protocol::{
    CoreEvent, RpcError, SessionHistoryEntry, SessionHistoryToolSummary, error_codes,
};
use kl_provider::{ChatMessage, Role};
use serde_json::Value;

#[derive(Debug, serde::Deserialize)]
pub(crate) struct StoredSessionEvent {
    pub(crate) seq: u64,
    pub(crate) at: String,
    pub(crate) event: CoreEvent,
}

/// A validated rewind: the target user event plus all live user events.
pub(crate) struct RewindPlan {
    pub(crate) from_seq: u64,
    pub(crate) user_events: Vec<u64>,
}

impl RewindPlan {
    /// Drops the target user message and everything after it; leading system
    /// messages (compaction summary) stay.
    pub(crate) fn truncate(
        &self,
        messages: &[ChatMessage],
    ) -> Result<(Vec<ChatMessage>, usize), RpcError> {
        let user_count = messages
            .iter()
            .filter(|message| message.role == Role::User)
            .count();
        // Live user events align to the trailing user rows: compaction drops
        // leading rows, imports (fork) add rows without events.
        let dropped = self.user_events.len().saturating_sub(user_count);
        let imported = user_count.saturating_sub(self.user_events.len());
        let target = self
            .user_events
            .iter()
            .position(|seq| *seq == self.from_seq)
            .ok_or_else(|| {
                rpc_error(
                    error_codes::INVALID_PARAMS,
                    "rewind_from_seq must be a live user.message event",
                )
            })?;
        if target < dropped {
            return Err(rpc_error(
                error_codes::INVALID_PARAMS,
                "rewind target was removed by compaction and cannot be restored",
            ));
        }
        let mut remaining = imported + target - dropped;
        let mut cut = None;
        for (index, message) in messages.iter().enumerate() {
            if message.role == Role::User {
                if remaining == 0 {
                    cut = Some(index);
                    break;
                }
                remaining -= 1;
            }
        }
        let cut = cut.ok_or_else(|| {
            rpc_error(
                error_codes::INTERNAL_ERROR,
                "rewind target is missing from the session messages",
            )
        })?;
        Ok((messages[..cut].to_vec(), cut))
    }
}

/// Rewind markers `(from_seq, marker_seq)`; events inside a dead interval are
/// hidden from history and attach replay without deleting anything on disk.
pub(crate) fn rewind_intervals(events: &[StoredSessionEvent]) -> Vec<(u64, u64)> {
    events
        .iter()
        .filter_map(|stored| match &stored.event {
            CoreEvent::ConversationRewound { from_seq } => Some((*from_seq, stored.seq)),
            _ => None,
        })
        .collect()
}

pub(crate) fn live_events(events: Vec<StoredSessionEvent>) -> Vec<StoredSessionEvent> {
    let intervals = rewind_intervals(&events);
    if intervals.is_empty() {
        return events;
    }
    events
        .into_iter()
        .filter(|stored| {
            !intervals
                .iter()
                .any(|(from, marker)| stored.seq >= *from && stored.seq < *marker)
        })
        .collect()
}

pub(crate) fn build_session_history(
    messages: &[Value],
    events: &[StoredSessionEvent],
) -> Vec<SessionHistoryEntry> {
    let mut entries = Vec::new();
    let mut turn_id = None;
    // Compaction drops whole rounds from the front but keeps their events; pair
    // the retained user messages with the newest user.message events and start
    // assistant/tool matching at the first retained turn so seqs stay correct.
    let user_event_indices: Vec<usize> = events
        .iter()
        .enumerate()
        .filter(|(_, stored)| matches!(stored.event, CoreEvent::UserMessage { .. }))
        .map(|(index, _)| index)
        .collect();
    let user_message_count = messages
        .iter()
        .filter(|message| message.get("role").and_then(Value::as_str) == Some("user"))
        .count();
    let first_user_event = user_event_indices.len().saturating_sub(user_message_count);
    let event_offset = user_event_indices.len() as isize - user_message_count as isize;
    let mut cursor = user_event_indices
        .get(first_user_event)
        .copied()
        .unwrap_or(events.len());
    let mut user_index = 0;
    for message in messages {
        let role = message
            .get("role")
            .and_then(Value::as_str)
            .unwrap_or_default();
        match role {
            "user" => {
                // Only the trailing rows have events; a negative slot is an
                // imported row (fork/merge) and must keep seq None.
                let slot = user_index as isize + event_offset;
                user_index += 1;
                let event = if slot >= 0 {
                    user_event_indices
                        .get(slot as usize)
                        .and_then(|index| events.get(*index))
                } else {
                    None
                };
                if event.is_none() {
                    turn_id = None;
                }
                let text = event
                    .and_then(|event| match &event.event {
                        CoreEvent::UserMessage { turn_id, text } => {
                            Some((turn_id.clone(), text.clone()))
                        }
                        _ => None,
                    })
                    .map(|(id, text)| {
                        turn_id = Some(id);
                        text
                    })
                    .unwrap_or_else(|| history_message_text(message));
                if text.trim().is_empty() {
                    continue;
                }
                entries.push(history_entry("user", text, event, None, None));
            }
            "assistant" => {
                let text = history_message_text(message);
                if text.trim().is_empty() {
                    continue;
                }
                let imported = turn_id.is_none();
                let event = if imported {
                    None
                } else {
                    next_assistant_event(events, &mut cursor)
                };
                let reasoning = if imported {
                    None
                } else {
                    turn_id
                        .as_deref()
                        .and_then(|id| history_reasoning(events, id))
                };
                entries.push(history_entry("assistant", text, event, None, reasoning));
            }
            "tool" => {
                if turn_id.is_none() {
                    // Imported rows carry no events, so there is no tool summary.
                    continue;
                }
                let event = next_history_event(events, &mut cursor, |event| {
                    matches!(event, CoreEvent::ToolCompleted { .. })
                });
                if let Some(event) = event {
                    if let CoreEvent::ToolCompleted {
                        name,
                        ok,
                        summary,
                        output,
                        diff,
                        ..
                    } = &event.event
                    {
                        entries.push(history_entry(
                            "tool",
                            String::new(),
                            Some(event),
                            Some(SessionHistoryToolSummary {
                                name: name.clone(),
                                ok: *ok,
                                summary: summary.clone(),
                                output: output.clone(),
                                diff: diff.clone(),
                            }),
                            None,
                        ));
                    }
                }
            }
            _ => {}
        }
    }
    entries
}

pub(crate) fn history_reasoning(events: &[StoredSessionEvent], turn_id: &str) -> Option<String> {
    let text = events
        .iter()
        .filter_map(|stored| match &stored.event {
            CoreEvent::TurnReasoning {
                turn_id: event_turn,
                text,
            } if event_turn == turn_id => Some(text.as_str()),
            _ => None,
        })
        .collect::<String>();
    let text = text.trim();
    (!text.is_empty()).then(|| text.to_owned())
}

pub(crate) fn next_assistant_event<'a>(
    events: &'a [StoredSessionEvent],
    cursor: &mut usize,
) -> Option<&'a StoredSessionEvent> {
    while *cursor < events.len() {
        match &events[*cursor].event {
            CoreEvent::TurnDelta { .. } => {
                let mut anchor = &events[*cursor];
                *cursor += 1;
                while *cursor < events.len() {
                    match &events[*cursor].event {
                        CoreEvent::TurnDelta { .. } => {
                            anchor = &events[*cursor];
                            *cursor += 1;
                        }
                        CoreEvent::TurnReasoning { .. } => {
                            *cursor += 1;
                        }
                        _ => break,
                    }
                }
                return Some(anchor);
            }
            CoreEvent::TurnCompleted { .. } => {
                let anchor = &events[*cursor];
                *cursor += 1;
                return Some(anchor);
            }
            _ => *cursor += 1,
        }
    }
    None
}

pub(crate) fn next_history_event<'a>(
    events: &'a [StoredSessionEvent],
    cursor: &mut usize,
    is_match: impl Fn(&CoreEvent) -> bool,
) -> Option<&'a StoredSessionEvent> {
    while *cursor < events.len() {
        let index = *cursor;
        *cursor += 1;
        if is_match(&events[index].event) {
            return Some(&events[index]);
        }
    }
    None
}

pub(crate) fn history_entry(
    role: &str,
    text: String,
    event: Option<&StoredSessionEvent>,
    tool: Option<SessionHistoryToolSummary>,
    reasoning: Option<String>,
) -> SessionHistoryEntry {
    SessionHistoryEntry {
        role: role.to_owned(),
        text,
        timestamp: event.map(|event| event.at.clone()),
        seq: event.map(|event| event.seq),
        tool,
        reasoning,
    }
}

pub(crate) fn history_message_text(value: &Value) -> String {
    if let Some(text) = value.get("content").and_then(Value::as_str) {
        return text.to_owned();
    }
    value
        .get("content")
        .and_then(Value::as_array)
        .map(|parts| {
            parts
                .iter()
                .filter(|part| part.get("type").and_then(Value::as_str) == Some("text"))
                .filter_map(|part| part.get("data").and_then(Value::as_str))
                .collect::<Vec<_>>()
                .join("")
        })
        .unwrap_or_default()
}
