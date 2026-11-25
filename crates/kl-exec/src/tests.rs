use super::*;

fn shell() -> (&'static str, Vec<String>) {
    #[cfg(windows)]
    {
        ("cmd", vec!["/C".to_owned(), "echo hello".to_owned()])
    }
    #[cfg(not(windows))]
    {
        ("sh", vec!["-c".to_owned(), "echo hello".to_owned()])
    }
}

#[test]
fn captures_stdout_and_exit_code() {
    let temp = tempfile::tempdir().unwrap();
    let (program, args) = shell();
    let spec = CommandSpec::new(program, args, temp.path());
    let output = run(&spec, &AtomicBool::new(false)).unwrap();
    assert!(output.success());
    assert!(output.stdout.contains("hello"));
}

#[test]
fn respects_output_limit() {
    let temp = tempfile::tempdir().unwrap();
    let (program, args) = shell();
    let mut spec = CommandSpec::new(program, args, temp.path());
    spec.max_output_bytes = 4;
    let output = run(&spec, &AtomicBool::new(false)).unwrap();
    assert!(output.truncated);
    assert_eq!(output.stdout.len(), 4);
}

#[test]
fn times_out_and_kills() {
    let temp = tempfile::tempdir().unwrap();
    #[cfg(windows)]
    let spec = CommandSpec::new(
        "cmd",
        vec!["/C".to_owned(), "ping 127.0.0.1 -n 5 > nul".to_owned()],
        temp.path(),
    );
    #[cfg(not(windows))]
    let spec = CommandSpec::new(
        "sh",
        vec!["-c".to_owned(), "sleep 5".to_owned()],
        temp.path(),
    );
    let mut spec = spec;
    spec.timeout = Duration::from_millis(100);
    let output = run(&spec, &AtomicBool::new(false)).unwrap();
    assert!(output.timed_out);
}
