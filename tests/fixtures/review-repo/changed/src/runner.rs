pub fn run(input: &str) -> String {
    std::process::Command::new(input).output().unwrap();
    input.to_owned()
}
