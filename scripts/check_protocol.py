from __future__ import annotations

import json
from pathlib import Path
import re
import sys
from typing import Any, Callable


ROOT = Path(__file__).resolve().parent.parent
ReadFile = Callable[[str], str | None]

PROTOCOL_RS = "crates/kl-protocol/src/lib.rs"
METHODS_SCHEMA = "schemas/protocol/v1/methods.schema.json"
CSHARP_PROTOCOL = "sdk/Kl.Client/Protocol.cs"

METHOD_NAME = re.compile(r"^[a-z][a-z0-9_]*(\.[a-z0-9_]+)*$")
SCHEMA_ROOT_OWNERS = {
    "setDefaultModel": "config.set_default_model",
    "defaultModel": "config.set_default_model",
    "providerKey": "provider.set_api_key",
    "providerEnabled": "provider.set_enabled",
    "pluginId": "plugin.install",
    "permissions": "permissions.get",
    "agentConfig": "agent.config.get",
    "review": "review.run",
}
UNSCHEMATIZED_METHODS = [
    "session.resume",
    "session.list",
    "session.fork",
    "turn.cancel",
    "tool.approval.respond",
    "audit.tail",
    "config.get",
    "skill.list",
    "skill.get",
    "mcp.list",
    "plugin.reload",
    "plugin.enable",
    "plugin.disable",
]


def rust_block(text: str, header: str) -> str | None:
    start = text.find(header)
    if start < 0:
        return None
    end = text.find("\n}", start)
    return text[start:] if end < 0 else text[start:end]


def extract_rust_methods(text: str) -> list[str] | None:
    block = rust_block(text, "pub mod methods {")
    if block is None:
        return None
    return re.findall(r'pub const \w+: &str = "([^"]+)";', block)


def extract_rust_events(text: str) -> list[str] | None:
    block = rust_block(text, "pub enum CoreEvent {")
    if block is None:
        return None
    return re.findall(r'#\[serde\(rename = "([^"]+)"\)\]', block)


def extract_csharp_methods(text: str) -> list[str]:
    values = re.findall(r'public const string \w+ = "([^"]+)";', text)
    return [value for value in values if METHOD_NAME.fullmatch(value)]


def extract_schema_roots(schema: dict[str, Any]) -> list[str]:
    definitions = schema.get("$defs", {})
    return [
        re.sub(r"(Params|Result)$", "", key)
        for key in definitions
        if key.endswith("Params") or key.endswith("Result")
    ]


def resolve_ref(schema: dict[str, Any], node: dict[str, Any] | None) -> dict[str, Any] | None:
    if not node or "$ref" not in node:
        return node
    key = node["$ref"].replace("#/$defs/", "")
    return schema.get("$defs", {}).get(key)


def extract_schema_events(schema: dict[str, Any]) -> list[str]:
    notification = schema.get("$defs", {}).get("eventNotification")
    params = notification.get("properties", {}).get("params", {}) if notification else {}
    event = resolve_ref(schema, params.get("properties", {}).get("event"))
    return event.get("properties", {}).get("type", {}).get("enum", []) if event else []


def upper_first(value: str) -> str:
    return value[:1].upper() + value[1:]


def schema_root_for(method: str) -> str:
    bare = method[3:] if method.startswith("kl.") else method
    segments = []
    for segment in bare.split("."):
        parts = [part for part in segment.split("_") if part]
        camel = parts[0] + "".join(upper_first(part) for part in parts[1:]) if parts else ""
        segments.append(camel)
    return "".join(upper_first(segment) if index > 0 else segment for index, segment in enumerate(segments))


def schema_covers_method(method: str, schema_roots: set[str]) -> bool:
    if schema_root_for(method) in schema_roots:
        return True
    return any(owner == method and root in schema_roots for root, owner in SCHEMA_ROOT_OWNERS.items())


def schema_root_has_owner(schema_root: str, rust_methods: list[str]) -> bool:
    if any(method != "event" and schema_root_for(method) == schema_root for method in rust_methods):
        return True
    owner = SCHEMA_ROOT_OWNERS.get(schema_root)
    return owner is not None and owner in rust_methods


def check_protocol(read: ReadFile) -> dict[str, list[dict[str, str]]]:
    failures: list[dict[str, str]] = []
    rust_text = read(PROTOCOL_RS)
    schema_text = read(METHODS_SCHEMA)
    csharp_text = read(CSHARP_PROTOCOL)
    if rust_text is None:
        return {"failures": [{"id": "rust-missing", "detail": f"{PROTOCOL_RS}: file is missing"}]}
    if schema_text is None:
        return {"failures": [{"id": "schema-missing", "detail": f"{METHODS_SCHEMA}: file is missing"}]}
    if csharp_text is None:
        return {"failures": [{"id": "csharp-missing", "detail": f"{CSHARP_PROTOCOL}: file is missing"}]}

    try:
        schema = json.loads(schema_text)
    except json.JSONDecodeError as error:
        return {"failures": [{"id": "schema-invalid", "detail": f"{METHODS_SCHEMA}: invalid JSON ({error})"}]}

    rust_methods = extract_rust_methods(rust_text)
    rust_events = extract_rust_events(rust_text)
    if rust_methods is None or rust_events is None:
        return {
            "failures": [
                {
                    "id": "rust-unparsable",
                    "detail": f"{PROTOCOL_RS}: expected a methods module and a CoreEvent enum",
                }
            ]
        }

    schema_roots = set(extract_schema_roots(schema))
    schema_events = set(extract_schema_events(schema))
    csharp_methods = set(extract_csharp_methods(csharp_text))

    for method in rust_methods:
        if method == "event" or method in UNSCHEMATIZED_METHODS:
            continue
        if not schema_covers_method(method, schema_roots):
            failures.append({"id": "method-unschematized", "detail": f"method {method} has no entry in {METHODS_SCHEMA}"})

    for method in UNSCHEMATIZED_METHODS:
        if method not in rust_methods:
            failures.append(
                {
                    "id": "stale-unschematized",
                    "detail": f"{method} is listed as unschematized but no longer exists in {PROTOCOL_RS}",
                }
            )

    for schema_root in sorted(schema_roots):
        if not schema_root_has_owner(schema_root, rust_methods):
            failures.append(
                {
                    "id": "method-missing-in-rust",
                    "detail": f"{METHODS_SCHEMA} documents {schema_root} but {PROTOCOL_RS} has no such method",
                }
            )

    for event in rust_events:
        if event not in schema_events:
            failures.append({"id": "event-unschematized", "detail": f"event {event} is missing from {METHODS_SCHEMA}"})
    for event in sorted(schema_events):
        if event not in rust_events:
            failures.append({"id": "event-missing-in-rust", "detail": f"event {event} is missing from {PROTOCOL_RS}"})

    for method in rust_methods:
        if method not in csharp_methods:
            failures.append(
                {
                    "id": "method-missing-in-csharp",
                    "detail": f"method {method} has no constant in {CSHARP_PROTOCOL}",
                }
            )

    for method in sorted(csharp_methods):
        if method not in rust_methods:
            failures.append(
                {
                    "id": "csharp-method-unknown",
                    "detail": f"{CSHARP_PROTOCOL} declares {method} but {PROTOCOL_RS} has no such method",
                }
            )

    return {"failures": failures}


def read_from_disk(file: str) -> str | None:
    full = ROOT / file
    return full.read_text(encoding="utf-8") if full.is_file() else None


def main() -> int:
    failures = check_protocol(read_from_disk)["failures"]
    for failure in failures:
        print(f"protocol check failed: {failure['id']}", file=sys.stderr)
        print(f"  - {failure['detail']}", file=sys.stderr)
    if failures:
        print(f"protocol check: {len(failures)} mismatches", file=sys.stderr)
        return 1
    print("protocol check: Rust methods and events match the schema and C#")
    return 0


if __name__ == "__main__":
    sys.exit(main())
