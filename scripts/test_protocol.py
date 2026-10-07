import importlib.util
import hashlib
import tempfile
import unittest
from pathlib import Path


class ProtocolGateTests(unittest.TestCase):
    def setUp(self):
        spec = importlib.util.spec_from_file_location("verify_protocol", Path(__file__).with_name("verify_protocol.py"))
        self.module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.module)

    def test_given_pending_review_when_requiring_approval_then_block(self):
        self.assertEqual(["Protocol security and client review is pending."],
                         self.module.validate({"version": 1, "status": "pending"}, Path(".")))

    def test_given_claimed_approval_without_reviewers_when_validating_then_block(self):
        errors = self.module.validate({"version": 1, "status": "approved", "artifacts": []}, Path("."))
        self.assertIn("Security and interoperability reviewers are required.", errors)
        self.assertIn("Reviewed artifacts are required.", errors)

    def test_given_changed_artifact_when_validating_then_block(self):
        with tempfile.TemporaryDirectory(prefix="cerberus-protocol-test-") as temporary:
            root = Path(temporary)
            (root / "vector.json").write_text("changed", encoding="utf-8")
            record = {"version": 1, "status": "approved", "securityReviewer": "fixture-security",
                      "interoperabilityReviewer": "fixture-client", "reviewedAt": "2026-10-07T00:00:00Z",
                      "reviewReference": "https://example.test/synthetic-review",
                      "artifacts": [{"path": "vector.json", "sha256": "0" * 64}]}
            self.assertIn("Reviewed artifact digest does not match: vector.json", self.module.validate(record, root))

    def test_given_artifact_outside_repo_when_validating_then_block(self):
        record = {"version": 1, "status": "approved", "securityReviewer": "fixture-security",
                  "interoperabilityReviewer": "fixture-client", "reviewedAt": "2026-10-07T00:00:00Z",
                  "reviewReference": "https://example.test/synthetic-review",
                  "artifacts": [{"path": "../outside.json", "sha256": "0" * 64}]}
        self.assertIn("Reviewed artifact is missing or outside the repository.", self.module.validate(record, Path(".")))

    def test_given_unrelated_reviewed_artifact_when_validating_then_require_protocol_and_vectors(self):
        with tempfile.TemporaryDirectory(prefix="cerberus-protocol-test-") as temporary:
            root = Path(temporary)
            (root / ".dockerignore").write_text("unrelated", encoding="utf-8")
            record = self.approved_fixture(root, [".dockerignore"])
            self.assertIn("Protocol document and interoperability vectors must both be reviewed.",
                          self.module.validate(record, root))

    def test_given_complete_synthetic_approval_when_validating_then_accept_unchanged_artifacts(self):
        with tempfile.TemporaryDirectory(prefix="cerberus-protocol-test-") as temporary:
            root = Path(temporary)
            (root / "docs/security").mkdir(parents=True)
            artifacts = ["docs/security/protocol-review.md", "docs/security/interoperability-vectors.json"]
            for name in artifacts:
                (root / name).write_text("synthetic test fixture, not real review evidence", encoding="utf-8")
            record = self.approved_fixture(root, artifacts)
            self.assertEqual([], self.module.validate(record, root))
            (root / artifacts[0]).write_text("changed protocol", encoding="utf-8")
            self.assertIn("Reviewed artifact digest does not match: " + artifacts[0],
                          self.module.validate(record, root))

    @staticmethod
    def approved_fixture(root, paths):
        return {"version": 1, "status": "approved", "securityReviewer": "fixture-security",
                "interoperabilityReviewer": "fixture-client", "reviewedAt": "2026-10-07T00:00:00Z",
                "reviewReference": "https://example.test/synthetic-review",
                "artifacts": [{"path": path, "sha256": hashlib.sha256((root / path).read_bytes()).hexdigest()}
                              for path in paths]}


if __name__ == "__main__":
    unittest.main()
