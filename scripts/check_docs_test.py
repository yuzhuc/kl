from __future__ import annotations

from pathlib import Path
import unittest

from check_docs import check_docs


ROOT = Path(__file__).resolve().parent.parent


def read_repo(file: str) -> str:
    return (ROOT / file).read_text(encoding="utf-8")


class CheckDocsTests(unittest.TestCase):
    def test_current_docs_satisfy_every_claim(self) -> None:
        result = check_docs(read_repo)
        self.assertEqual(result["failures"], [])

    def test_regressed_claim_is_reported(self) -> None:
        def read(file: str) -> str:
            text = read_repo(file)
            return text.replace("`tty=true` uses portable-pty", "tty is unsupported") if file == "README.md" else text

        result = check_docs(read)
        self.assertEqual(len(result["failures"]), 1)
        self.assertEqual(result["failures"][0]["id"], "pty")
        self.assertRegex(result["failures"][0]["details"][0], r"README\.md: expected the portable-pty PTY note")

    def test_missing_file_is_reported(self) -> None:
        def read(file: str) -> str | None:
            return None if file == "README.md" else read_repo(file)

        result = check_docs(read)
        self.assertGreater(len(result["failures"]), 0)
        self.assertTrue(
            all(
                any("file is missing" in detail for detail in failure["details"])
                for failure in result["failures"]
            )
        )


if __name__ == "__main__":
    unittest.main()
