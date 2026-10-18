use regex::Regex;
use std::sync::OnceLock;

use crate::Symbol;

pub(crate) fn language_for(path: &str) -> &'static str {
    let name = path.rsplit('/').next().unwrap_or(path);
    let extension = name
        .rsplit_once('.')
        .map(|(_, extension)| extension.to_ascii_lowercase())
        .unwrap_or_default();
    match extension.as_str() {
        "rs" => "rust",
        "cs" => "csharp",
        "ts" | "tsx" | "mts" | "cts" => "typescript",
        "js" | "jsx" | "mjs" | "cjs" => "javascript",
        "py" => "python",
        "go" => "go",
        "java" => "java",
        "kt" | "kts" => "kotlin",
        "c" | "h" => "c",
        "cc" | "cpp" | "cxx" | "hpp" | "hh" => "cpp",
        "rb" => "ruby",
        "php" => "php",
        "swift" => "swift",
        "scala" => "scala",
        "sh" | "bash" | "zsh" | "ps1" => "shell",
        "sql" => "sql",
        "html" | "htm" => "html",
        "css" => "css",
        "scss" | "sass" => "scss",
        "json" => "json",
        "yaml" | "yml" => "yaml",
        "toml" => "toml",
        "md" | "markdown" => "markdown",
        "xml" => "xml",
        "csproj" | "sln" | "props" | "targets" => "msbuild",
        _ => "other",
    }
}

pub(crate) fn parse_symbols(language: &str, text: &str) -> Vec<Symbol> {
    match language {
        "rust" => rust_symbols(text),
        "csharp" => csharp_symbols(text),
        "typescript" => typescript_symbols(text),
        _ => Vec::new(),
    }
}

fn push_symbol(symbols: &mut Vec<Symbol>, name: &str, kind: &str, line: usize) {
    symbols.push(Symbol {
        name: name.to_owned(),
        kind: kind.to_owned(),
        line: line as u64,
    });
}

fn rust_pattern() -> &'static Regex {
    static PATTERN: OnceLock<Regex> = OnceLock::new();
    PATTERN.get_or_init(|| {
        Regex::new(
            r#"^\s*(?:pub(?:\([^)]*\))?\s+)?(?:const\s+)?(?:async\s+)?(?:unsafe\s+)?(?:extern\s+"[^"]*"\s+)?(?P<kind>fn|struct|enum|trait|union|type)\s+(?P<name>[A-Za-z_][A-Za-z0-9_]*)"#,
        )
        .expect("valid rust symbol regex")
    })
}

fn rust_symbols(text: &str) -> Vec<Symbol> {
    let pattern = rust_pattern();
    let mut symbols = Vec::new();
    for (index, line) in text.lines().enumerate() {
        let trimmed = line.trim_start();
        if trimmed.starts_with("//") || trimmed.starts_with('*') || trimmed.starts_with("/*") {
            continue;
        }
        let Some(captures) = pattern.captures(line) else {
            continue;
        };
        let kind = match &captures["kind"] {
            "fn" => "function",
            "struct" => "struct",
            "enum" => "enum",
            "trait" => "trait",
            "union" => "union",
            "type" => "type",
            _ => continue,
        };
        push_symbol(&mut symbols, &captures["name"], kind, index + 1);
    }
    symbols
}

fn csharp_type_pattern() -> &'static Regex {
    static PATTERN: OnceLock<Regex> = OnceLock::new();
    PATTERN.get_or_init(|| {
        Regex::new(
            r#"^\s*(?:(?:public|private|protected|internal|static|abstract|sealed|partial|readonly|unsafe|virtual|override|async|extern|new|file)\s+)*(?P<kind>class|interface|struct|record|enum)\s+(?P<name>[A-Za-z_][A-Za-z0-9_]*)"#,
        )
        .expect("valid csharp type regex")
    })
}

fn csharp_method_pattern() -> &'static Regex {
    static PATTERN: OnceLock<Regex> = OnceLock::new();
    PATTERN.get_or_init(|| {
        Regex::new(
            r#"^\s+(?:(?:public|private|protected|internal|static|abstract|sealed|partial|readonly|unsafe|virtual|override|async|extern|new|file)\s+)*(?:[A-Za-z_][A-Za-z0-9_<>\[\],\.\?]*\s+)+(?P<name>[A-Z][A-Za-z0-9_]*)\s*(?:<[^>]*>)?\s*\("#,
        )
        .expect("valid csharp method regex")
    })
}

fn csharp_symbols(text: &str) -> Vec<Symbol> {
    let type_pattern = csharp_type_pattern();
    let method_pattern = csharp_method_pattern();
    let mut symbols = Vec::new();
    for (index, line) in text.lines().enumerate() {
        let trimmed = line.trim_start();
        if trimmed.starts_with("//") || trimmed.starts_with('*') || trimmed.starts_with("/*") {
            continue;
        }
        if let Some(captures) = type_pattern.captures(line) {
            let kind = match &captures["kind"] {
                "class" => "class",
                "interface" => "interface",
                "struct" => "struct",
                "record" => "record",
                "enum" => "enum",
                _ => continue,
            };
            push_symbol(&mut symbols, &captures["name"], kind, index + 1);
            continue;
        }
        if is_csharp_control_line(trimmed) {
            continue;
        }
        if let Some(open) = line.find('(') {
            if line[..open].contains('=') {
                continue;
            }
        }
        if let Some(captures) = method_pattern.captures(line) {
            push_symbol(&mut symbols, &captures["name"], "method", index + 1);
        }
    }
    symbols
}

fn is_csharp_control_line(line: &str) -> bool {
    let first = line
        .split(|character: char| character.is_whitespace() || character == '(')
        .next()
        .unwrap_or("");
    matches!(
        first,
        "if" | "for"
            | "foreach"
            | "while"
            | "switch"
            | "catch"
            | "using"
            | "lock"
            | "return"
            | "throw"
            | "new"
            | "await"
            | "yield"
            | "case"
            | "when"
            | "else"
            | "do"
            | "fixed"
            | "checked"
            | "unchecked"
            | "nameof"
            | "typeof"
            | "sizeof"
            | "default"
            | "base"
            | "this"
    )
}

fn typescript_function_pattern() -> &'static Regex {
    static PATTERN: OnceLock<Regex> = OnceLock::new();
    PATTERN.get_or_init(|| {
        Regex::new(
            r#"^\s*(?:export\s+)?(?:default\s+)?(?:declare\s+)?(?:async\s+)?function\s*\*?\s*(?P<name>[A-Za-z_$][A-Za-z0-9_$]*)"#,
        )
        .expect("valid typescript function regex")
    })
}

fn typescript_type_pattern() -> &'static Regex {
    static PATTERN: OnceLock<Regex> = OnceLock::new();
    PATTERN.get_or_init(|| {
        Regex::new(
            r#"^\s*(?:export\s+)?(?:default\s+)?(?:declare\s+)?(?:abstract\s+)?(?P<kind>class|interface|enum|type)\s+(?P<name>[A-Za-z_$][A-Za-z0-9_$]*)"#,
        )
        .expect("valid typescript type regex")
    })
}

fn typescript_arrow_pattern() -> &'static Regex {
    static PATTERN: OnceLock<Regex> = OnceLock::new();
    PATTERN.get_or_init(|| {
        Regex::new(
            r#"^\s*(?:export\s+)?(?:const|let|var)\s+(?P<name>[A-Za-z_$][A-Za-z0-9_$]*)\s*(?::[^=]+)?=\s*(?:async\s*)?(?:\([^)]*\)|[A-Za-z_$][A-Za-z0-9_$]*)\s*=>"#,
        )
        .expect("valid typescript arrow regex")
    })
}

fn typescript_expression_pattern() -> &'static Regex {
    static PATTERN: OnceLock<Regex> = OnceLock::new();
    PATTERN.get_or_init(|| {
        Regex::new(
            r#"^\s*(?:export\s+)?(?:const|let|var)\s+(?P<name>[A-Za-z_$][A-Za-z0-9_$]*)\s*=\s*(?:async\s*)?function\b"#,
        )
        .expect("valid typescript expression regex")
    })
}

fn typescript_method_pattern() -> &'static Regex {
    static PATTERN: OnceLock<Regex> = OnceLock::new();
    PATTERN.get_or_init(|| {
        Regex::new(
            r#"^\s+(?:(?:public|private|protected|static|async|readonly|abstract|override|get|set|declare)\s+)*(?P<name>[A-Za-z_$][A-Za-z0-9_$]*)\s*(?:<[^>]*>)?\s*\("#,
        )
        .expect("valid typescript method regex")
    })
}

fn typescript_symbols(text: &str) -> Vec<Symbol> {
    let function_pattern = typescript_function_pattern();
    let type_pattern = typescript_type_pattern();
    let arrow_pattern = typescript_arrow_pattern();
    let expression_pattern = typescript_expression_pattern();
    let method_pattern = typescript_method_pattern();
    let mut symbols = Vec::new();
    for (index, line) in text.lines().enumerate() {
        let trimmed = line.trim_start();
        if trimmed.starts_with("//") || trimmed.starts_with('*') || trimmed.starts_with("/*") {
            continue;
        }
        if let Some(captures) = function_pattern.captures(line) {
            push_symbol(&mut symbols, &captures["name"], "function", index + 1);
            continue;
        }
        if let Some(captures) = type_pattern.captures(line) {
            let kind = match &captures["kind"] {
                "class" => "class",
                "interface" => "interface",
                "enum" => "enum",
                "type" => "type",
                _ => continue,
            };
            push_symbol(&mut symbols, &captures["name"], kind, index + 1);
            continue;
        }
        if let Some(captures) = arrow_pattern.captures(line) {
            push_symbol(&mut symbols, &captures["name"], "function", index + 1);
            continue;
        }
        if let Some(captures) = expression_pattern.captures(line) {
            push_symbol(&mut symbols, &captures["name"], "function", index + 1);
            continue;
        }
        if is_typescript_control_line(trimmed) {
            continue;
        }
        if let Some(open) = line.find('(') {
            if line[..open].contains('=') || line[..open].contains("=>") {
                continue;
            }
        }
        if let Some(captures) = method_pattern.captures(line) {
            push_symbol(&mut symbols, &captures["name"], "method", index + 1);
        }
    }
    symbols
}

fn is_typescript_control_line(line: &str) -> bool {
    let first = line
        .split(|character: char| character.is_whitespace() || character == '(')
        .next()
        .unwrap_or("");
    matches!(
        first,
        "if" | "for"
            | "foreach"
            | "while"
            | "switch"
            | "catch"
            | "return"
            | "throw"
            | "new"
            | "await"
            | "yield"
            | "else"
            | "do"
            | "case"
            | "when"
            | "typeof"
            | "delete"
            | "void"
            | "in"
            | "of"
            | "instanceof"
            | "super"
            | "this"
            | "describe"
            | "it"
            | "test"
            | "expect"
    )
}
