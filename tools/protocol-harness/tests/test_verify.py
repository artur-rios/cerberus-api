import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]


class VerifyTests(unittest.TestCase):
    def setUp(self):
        path = ROOT / "verify.py"
        if not path.exists(): self.fail("Orchestrator feature missing")
        spec = importlib.util.spec_from_file_location("harness_verify", path)
        self.api = importlib.util.module_from_spec(spec); spec.loader.exec_module(self.api)
        self.tmp = tempfile.TemporaryDirectory(); self.addCleanup(self.tmp.cleanup)
        self.target = Path(self.tmp.name) / "evidence.json"; self.target.write_bytes(b'previous-valid-evidence')
        self.manifest = {"positive": {"kind": "content", "expect": "success"}, "negative": {"kind": "content", "expect": "invalid_protocol"}}

    def reply(self, command, operation, *paths):
        impl = command[0]
        if operation == "self-test":
            return dict(schemaVersion=1, classification="public-test-fixtures", implementation=impl, version="1.0.0",
                        results=[dict(id=case, status="pass") for case in self.api.KNOWN_IDS])
        if operation == "produce":
            cases = [dict(id=identity, kind=row["kind"], expect=row["expect"], output={"public": "fixture"}) for identity, row in self.manifest.items()]
            for case in cases: case["digest"] = self.api.digest(case["output"])
            return dict(schemaVersion=1, classification="public-test-fixtures", implementation=impl, version="1.0.0", cases=cases)
        document = self.api.read_document(paths[0])
        return dict(schemaVersion=1, classification="public-test-fixtures", implementation=impl, version="1.0.0",
                    results=[dict(id=c["id"], status="pass", digest=c["digest"]) for c in document["cases"]])

    def run_verify(self, invoke=None, native=None, check=None):
        with patch.object(self.api, "manifest", return_value=self.manifest), patch.object(self.api, "invoke", side_effect=invoke or self.reply), \
             patch.object(self.api, "native_suites", return_value=native or {"java": {"tests": 1, "failures": 0, "errors": 0, "skipped": 0}, "python": {"tests": 1, "failures": 0, "errors": 0, "skipped": 0}}):
            return self.api.verify(["java"], ["python"], output=self.target if check is None else None, check=check)

    def test_GivenCompleteBidirectionalResults_WhenVerified_ThenEvidenceAtomicallyWritten(self):
        result = self.run_verify()
        self.assertEqual("public-test-fixtures", result["classification"])
        self.assertEqual(2, len(result["producers"]))
        self.assertEqual(result, self.api.read_document(self.target))
        before = self.target.read_bytes(); self.run_verify(check=self.target); self.assertEqual(before, self.target.read_bytes())

    def test_GivenMissingTimeoutNonzeroOrMalformedChild_WhenVerified_ThenPreviousEvidencePreserved(self):
        for exception in [FileNotFoundError("marker-secret"), TimeoutError("marker-secret"), RuntimeError("marker-secret")]:
            with self.subTest(exception=type(exception)), self.assertRaises(self.api.HarnessFailure) as error:
                self.run_verify(invoke=lambda *args: (_ for _ in ()).throw(exception))
            self.assertNotIn("marker-secret", str(error.exception)); self.assertEqual(b'previous-valid-evidence', self.target.read_bytes())

    def test_GivenEmptySkippedOrFailingNativeSuite_WhenVerified_ThenNoEvidenceWritten(self):
        for change in [dict(tests=0), dict(skipped=1), dict(errors=1), dict(failures=1)]:
            report = dict(tests=1, failures=0, errors=0, skipped=0); report.update(change)
            with self.subTest(change=change), self.assertRaises(self.api.HarnessFailure): self.run_verify(native={"java": report, "python": report})
            self.assertEqual(b'previous-valid-evidence', self.target.read_bytes())

    def test_GivenMissingDuplicateExtraOrWrongResultIds_WhenVerified_ThenNoEvidenceWritten(self):
        for mutation in ["empty", "duplicate", "extra", "failed", "wrong-version", "wrong-schema", "wrong-classification", "stale-digest", "bad-json"]:
            def invoke(command, operation, *paths):
                document = self.reply(command, operation, *paths)
                if operation == "consume":
                    if mutation == "empty": document["results"] = []
                    elif mutation == "duplicate": document["results"].append(document["results"][0])
                    elif mutation == "extra": document["results"][0]["id"] = "unexpected"
                    elif mutation == "failed": document["results"][0]["status"] = "fail"
                    elif mutation == "wrong-version": document["version"] = "wrong"
                    elif mutation == "wrong-schema": document["schemaVersion"] = 2
                    elif mutation == "wrong-classification": document["classification"] = "production"
                    elif mutation == "stale-digest": document["results"][0]["digest"] = "wrong"
                    elif mutation == "bad-json": return "not an object"
                return document
            with self.subTest(mutation=mutation), self.assertRaises(self.api.HarnessFailure): self.run_verify(invoke=invoke)
            self.assertEqual(b'previous-valid-evidence', self.target.read_bytes())

    def test_GivenMissingKnownAnswersOrProducerCases_WhenVerified_ThenNoEvidenceWritten(self):
        for operation in ["self-test", "produce"]:
            def invoke(command, current, *paths):
                document = self.reply(command, current, *paths)
                if current == operation: document["results" if current == "self-test" else "cases"] = []
                return document
            with self.subTest(operation=operation), self.assertRaises(self.api.HarnessFailure): self.run_verify(invoke=invoke)
            self.assertEqual(b'previous-valid-evidence', self.target.read_bytes())

    def test_GivenCorruptCorpus_WhenReplayed_ThenRejectedWithoutOverwrite(self):
        self.run_verify(); document = self.api.read_document(self.target)
        document["producers"][0]["cases"][0]["output"] = {"changed": True}
        self.target.write_text(json.dumps(document)); before = self.target.read_bytes()
        with self.assertRaises(self.api.HarnessFailure): self.run_verify(check=self.target)
        self.assertEqual(before, self.target.read_bytes())

    def test_GivenWrongProducerIdentityOrDeterministicFlag_WhenVerified_ThenNoEvidenceWritten(self):
        for mutation in ["identity", "deterministic"]:
            def invoke(command, operation, *paths):
                value = self.reply(command, operation, *paths)
                if operation == "produce":
                    if mutation == "identity": value["implementation"] = "other"
                    else: value["cases"][0]["deterministic"] = True
                return value
            with self.subTest(mutation=mutation), self.assertRaises(self.api.HarnessFailure): self.run_verify(invoke=invoke)
            self.assertEqual(b'previous-valid-evidence', self.target.read_bytes())
