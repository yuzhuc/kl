#[test]
fn sandbox_runner_binary_is_built() {
    let runner = env!("CARGO_BIN_EXE_kl-sandbox-runner");
    assert!(
        std::path::Path::new(runner).exists(),
        "sandbox runner missing: {runner}"
    );
}
