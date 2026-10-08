"""Delivery invariants; standard-library only, independent of harness success."""
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[3]
GATE = ("github.ref == 'refs/heads/main' || github.base_ref == 'main' || "
        "startsWith(github.ref, 'refs/tags/') || startsWith(github.head_ref, 'feature/uc-')")


def steps(workflow):
    """Read the named step blocks in this repository's fixed YAML indentation."""
    test_job = workflow.split("  test:\n", 1)[1].split("\n  docker:", 1)[0]
    result = {}
    for block in test_job.split("      - name: ")[1:]:
        name, _, body = block.partition("\n")
        if name in result:
            raise AssertionError("duplicate CI step")
        result[name] = body
    return result


def scalar(block, key):
    match = re.search(r"^        " + re.escape(key) + r": (.*)\n", block, re.M)
    if match is None:
        raise AssertionError("missing CI key: " + key)
    value = match[1]
    if value in ("|", ">-"):
        tail = block[match.end():]
        lines = []
        for line in tail.splitlines():
            if line and not line.startswith("          "):
                break
            lines.append(line[10:])
        value = (" " if value == ">-" else "\n").join(lines).strip()
    return value


class DeliveryTests(unittest.TestCase):
    def setUp(self):
        self.workflow = (ROOT / ".github/workflows/tests.yml").read_text()
        self.steps = steps(self.workflow)

    def gate(self, *arguments):
        return subprocess.run([sys.executable, "scripts/verify_protocol.py", *arguments],
                              cwd=ROOT, capture_output=True, text=True, timeout=30)

    def test_GivenGreenHarnessAndPendingReview_WhenGateRuns_ThenDependentWorkBlocked(self):
        result = self.gate()
        self.assertEqual(1, result.returncode)
        self.assertEqual("BLOCKED: Protocol security and client review is pending.\n", result.stdout)
        self.assertEqual("pending", json.loads((ROOT / "docs/security/protocol-review.json").read_text())["status"])
        self.assertIn("--check docs/security/interoperability-vectors.json", self.workflow)

    def test_GivenMissingOrStaleReview_WhenGateRuns_ThenBlocked(self):
        with tempfile.TemporaryDirectory() as temporary:
            record = Path(temporary) / "review.json"
            self.assertEqual(1, self.gate("--record", str(record)).returncode)
            record.write_text(json.dumps(dict(version=1, status="approved", securityReviewer="synthetic-security",
                interoperabilityReviewer="synthetic-client", reviewedAt="2026-10-08T00:00:00Z",
                reviewReference="https://example.test/synthetic-review", artifacts=[
                    dict(path=name, sha256="0" * 64) for name in
                    ("docs/security/protocol-review.md", "docs/security/interoperability-vectors.json")])))
            result = self.gate("--record", str(record))
            self.assertEqual(1, result.returncode)
            self.assertIn("Reviewed artifact digest does not match", result.stdout)

    def test_GivenHarnessCI_WhenParsed_ThenLockedAuditPrecedesFullReplay(self):
        self.assertIn("Setup protocol Java", self.steps)
        java = self.steps["Setup protocol Java"]
        python = self.steps["Setup protocol Python"]
        self.assertIn("actions/setup-java@de7274f081f381c8f8158605e0321c36c376e2e6", java)
        self.assertIn("java-version: '25'", java)
        self.assertIn("actions/setup-python@5fda3b95a4ea91299a34e894583c3862153e4b97", python)
        self.assertIn("python-version: '3.14'", python)
        install = scalar(self.steps["Install locked protocol dependencies"], "run")
        self.assertIn("mktemp -d", install)
        self.assertIn("--only-binary=:all: --require-hashes", install)
        self.assertIn("--no-index", install)
        self.assertIn("mavenDistribution", install)
        self.assertIn("hashlib.sha256", install)
        self.assertIn("clean test dependency:tree", install)
        self.assertIn("MAVEN_USER_HOME", install)
        self.assertIn("HARNESS_MAVEN_CACHE", install)
        audit = scalar(self.steps["Audit locked protocol dependencies"], "run")
        self.assertIn("tools/protocol-harness/audit.py", audit)
        self.assertIn("--maven-cache", audit)
        self.assertIn("--wheels", audit)
        self.assertIn("--maven-distribution", audit)
        replay = scalar(self.steps["Replay protocol corpus and complete native suites"], "run")
        self.assertIn("verify.py --check docs/security/interoperability-vectors.json", replay)
        self.assertIn("unittest discover -s tools/protocol-harness/tests", replay)
        order = list(self.steps)
        self.assertLess(order.index("Install locked protocol dependencies"), order.index("Audit locked protocol dependencies"))
        self.assertLess(order.index("Audit locked protocol dependencies"), order.index("Replay protocol corpus and complete native suites"))
        for name in ("Install locked protocol dependencies", "Audit locked protocol dependencies", "Replay protocol corpus and complete native suites"):
            self.assertNotIn("continue-on-error", self.steps[name])
            self.assertNotIn("        if:", self.steps[name])
        self.assertNotIn("benchmark.py", self.workflow)
        self.assertNotIn("verify.py --output", self.workflow)
        self.assertNotIn("verify.py --write", self.workflow)

    def test_GivenHarnessCI_WhenParsed_ThenProductionChecksRetained(self):
        gate = self.steps["Require reviewed protocol before dependent work or release"]
        self.assertEqual(GATE, scalar(gate, "if"))
        self.assertEqual("python3 scripts/verify_protocol.py", scalar(gate, "run"))
        self.assertNotIn("continue-on-error", gate)
        for name, category in (("Run unit tests", "Unit"), ("Run functional tests", "Functional")):
            command = scalar(self.steps[name], "run")
            self.assertIn('--filter "Category=' + category + '"', command)
            self.assertIn('--collect:"XPlat Code Coverage"', command)
        full = scalar(self.steps["Run complete unfiltered suite"], "run")
        self.assertIn("dotnet test src/ArturRios.Cerberus.sln", full)
        self.assertNotIn("--filter", full)
        self.assertIn('--collect:"XPlat Code Coverage"', full)
        self.assertEqual("python3 scripts/coverage.py --report-only",
                         scalar(self.steps["Generate coverage report and enforce the minimum"], "run"))
        docker = self.workflow.split("\n  docker:", 1)[1]
        self.assertIn("uses: docker/build-push-action@v7", docker)
        self.assertIn("push: false", docker)

    def test_GivenProductionGraphAndDocker_WhenInspected_ThenHarnessExcluded(self):
        for project in (ROOT / "src").rglob("*.csproj"):
            for reference in ET.parse(project).getroot().iter("ProjectReference"):
                target = (project.parent / reference.attrib["Include"].replace("\\", "/")).resolve()
                self.assertTrue(target.is_relative_to(ROOT / "src"))
            self.assertNotIn("protocol-harness", project.read_text())
        ignore = (ROOT / ".dockerignore").read_text().splitlines()
        self.assertEqual("**", ignore[0])
        self.assertFalse(any(line.startswith("!tools") for line in ignore))
        copies = [line for line in (ROOT / "Dockerfile").read_text().splitlines() if line.startswith("COPY ")]
        self.assertFalse(any("tools" in line or line == "COPY . ." for line in copies))

if __name__ == "__main__":
    unittest.main()
