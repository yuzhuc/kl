use super::*;

#[test]
fn slow_subscriber_is_removed_after_backlog() {
    let broadcaster = Broadcaster::new();
    let (id, _sender, _receiver) = broadcaster.subscribe();
    for _ in 0..=MAX_PENDING_LINES {
        broadcaster.broadcast("event\n".to_owned());
    }
    assert!(!broadcaster.subscribers.lock().unwrap().contains_key(&id));
}
