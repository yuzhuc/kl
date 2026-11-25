from __future__ import annotations

import json
from pathlib import Path
import unittest

from check_protocol import (
    CSHARP_PROTOCOL,
    METHODS_SCHEMA,
    PROTOCOL_RS,
    check_protocol,
    extract_csharp_methods,
    extract_rust_methods,
    extract_schema_roots,
    schema_root_for,
)


ROOT = Path(__file__).resolve().parent.parent
R3_METHODS = ["server.info", "session.attach", "session.detach", "session.lease"]
MEMORY_METHODS = ["memory.list", "memory.read", "memory.write", "memory.delete"]


def read_repo(file: str) -> str:
    return (ROOT / file).read_text(encoding="utf-8")


class CheckProtocolTests(unittest.TestCase):
    def test_rust_protocol_matches_schema(self) -> None:
        self.assertEqual(check_protocol(read_repo)["failures"], [])

    def test_method_dropped_from_schema_is_reported(self) -> None:
        def read(file: str) -> str:
            text = read_repo(file)
            if file != METHODS_SCHEMA:
                return text
            schema = json.loads(text)
            del schema["$defs"]["sessionCreateParams"]
            return json.dumps(schema)

        failures = check_protocol(read)["failures"]
        self.assertGreater(len(failures), 0)
        self.assertTrue(any(failure["id"] == "method-unschematized" for failure in failures))

    def test_method_added_to_rust_without_schema_entry_is_reported(self) -> None:
        def read(file: str) -> str:
            text = read_repo(file)
            if file != PROTOCOL_RS:
                return text
            return text.replace(
                "pub mod methods {",
                'pub mod methods {\n    pub const KL_TEST_ONLY: &str = "kl.test.only";',
            )

        failures = check_protocol(read)["failures"]
        self.assertGreater(len(failures), 0)
        self.assertTrue(any(failure["id"] == "method-unschematized" for failure in failures))

    def test_r3_methods_are_declared_everywhere(self) -> None:
        rust_methods = extract_rust_methods(read_repo(PROTOCOL_RS))
        schema = json.loads(read_repo(METHODS_SCHEMA))
        schema_roots = set(extract_schema_roots(schema))
        csharp_methods = set(extract_csharp_methods(read_repo(CSHARP_PROTOCOL)))
        self.assertIsNotNone(rust_methods)
        for method in R3_METHODS:
            self.assertIn(method, rust_methods)
            self.assertIn(method, csharp_methods)
            self.assertIn(schema_root_for(method), schema_roots, f"{method} missing from {METHODS_SCHEMA}")

    def test_memory_methods_are_declared_everywhere(self) -> None:
        rust_methods = extract_rust_methods(read_repo(PROTOCOL_RS))
        schema = json.loads(read_repo(METHODS_SCHEMA))
        schema_roots = set(extract_schema_roots(schema))
        csharp_methods = set(extract_csharp_methods(read_repo(CSHARP_PROTOCOL)))
        self.assertIsNotNone(rust_methods)
        for method in MEMORY_METHODS:
            self.assertIn(method, rust_methods)
            self.assertIn(method, csharp_methods)
            self.assertIn(schema_root_for(method), schema_roots, f"{method} missing from {METHODS_SCHEMA}")

    def test_missing_csharp_memory_method_is_reported(self) -> None:
        def read(file: str) -> str:
            text = read_repo(file)
            if file != CSHARP_PROTOCOL:
                return text
            return text.replace('    public const string MemoryList = "memory.list";\n', "")

        failures = check_protocol(read)["failures"]
        self.assertTrue(
            any(
                failure["id"] == "method-missing-in-csharp" and "memory.list" in failure["detail"]
                for failure in failures
            )
        )

    def test_missing_csharp_r3_method_is_reported(self) -> None:
        def read(file: str) -> str:
            text = read_repo(file)
            if file != CSHARP_PROTOCOL:
                return text
            return text.replace('    public const string SessionAttach = "session.attach";\n', "")

        failures = check_protocol(read)["failures"]
        self.assertTrue(
            any(
                failure["id"] == "method-missing-in-csharp" and "session.attach" in failure["detail"]
                for failure in failures
            )
        )

    def test_unknown_csharp_method_is_reported(self) -> None:
        def read(file: str) -> str:
            text = read_repo(file)
            if file != CSHARP_PROTOCOL:
                return text
            return text.replace(
                '    public const string Event = "event";',
                '    public const string Event = "event";\n    public const string Ghost = "session.ghost";',
            )

        failures = check_protocol(read)["failures"]
        self.assertTrue(
            any(
                failure["id"] == "csharp-method-unknown" and "session.ghost" in failure["detail"]
                for failure in failures
            )
        )


if __name__ == "__main__":
    unittest.main()
