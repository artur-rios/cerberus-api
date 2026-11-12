"""Delivery invariants; standard-library only, independent of harness success."""
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[3]


class DeliveryTests(unittest.TestCase):
    """The `test` and `docker` checks are scripts (yggdrasil.yml runs them in Jenkins); these read them."""

    def setUp(self):
        self.script = (ROOT / "ci/test.sh").read_text()
        self.docker_script = (ROOT / "ci/docker.sh").read_text()
        self.pipeline = (ROOT / "yggdrasil.yml").read_text()

    def gate(self, *arguments):
        return subprocess.run([sys.executable, "scripts/verify_protocol.py", *arguments],
                              cwd=ROOT, capture_output=True, text=True, timeout=30)

    def branch_policy(self, base):
        # The script runs `python3`: the interpreter running this test comes first on the PATH.
        path = os.pathsep.join([str(Path(sys.executable).parent), os.environ.get("PATH", "")])
        return subprocess.run(["sh", "ci/branch-policy.sh"], cwd=ROOT, capture_output=True, text=True, timeout=30,
                              env={**os.environ, "PATH": path, "HEAD_REF": "release/1.0.0", "BASE_REF": base})

    def test_GivenGreenHarnessAndPendingReview_WhenReleaseGateRuns_ThenReleaseBlocked(self):
        result = self.gate()
        self.assertEqual(1, result.returncode)
        self.assertEqual("BLOCKED: Protocol security and client review is pending.\n", result.stdout)
        self.assertEqual("pending", json.loads((ROOT / "docs/security/protocol-review.json").read_text())["status"])
        self.assertIn("--check docs/security/interoperability-vectors.json", self.script)

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
        self.assertIn("set -euo pipefail", self.script)
        # The runtimes the harness is supported on (JDK 25, CPython 3.14), each fetched by checksum: every pin is a
        # sha256 (64 hex characters), and the archive is checked before it is unpacked.
        java = (ROOT / "ci/java.sh").read_text()
        python = (ROOT / "ci/python.sh").read_text()
        self.assertRegex(java, r'TEMURIN_RELEASE="jdk-25\.')
        self.assertRegex(python, r"CPYTHON_VERSION=3\.14\.")
        for name, text in (("ci/java.sh", java), ("ci/python.sh", python)):
            with self.subTest(script=name):
                pins = re.findall(r"(?m)^\s*sha256=(\S+?)(?: ;;)?$", text)
                self.assertEqual(2, len(pins), "one pin per architecture")
                for pin in pins:
                    self.assertRegex(pin, r"\A[0-9a-f]{64}\Z")
                self.assertIn("sha256sum --check --strict", text)
                self.assertLess(text.index("sha256sum --check"), text.index("tar -xzf"))
        # The unzip shim runs on the Python the first helper makes available.
        position = {helper: re.search(r"(?m)^" + helper + "$", self.script).start()
                    for helper in ("ensure_python3", "ensure_java", "ensure_unzip")}
        self.assertLess(position["ensure_python3"], position["ensure_unzip"])
        self.assertLess(position["ensure_java"], position["ensure_unzip"])
        # What the helpers make is removed by the script's EXIT trap.
        self.assertRegex(self.script, r"(?m)^trap cleanup EXIT$")
        self.assertRegex(self.script, r"(?m)^CLEANUP_DIRS=\(\)$")
        for text in (java, python):
            self.assertEqual(text.count("mktemp -d"), text.count("CLEANUP_DIRS+=("))
        install = "pip download"
        self.assertIn("mktemp -d", self.script)
        self.assertEqual(2, self.script.count("--only-binary=:all: --require-hashes"))
        self.assertIn("--no-index", self.script)
        self.assertIn("mavenDistribution", self.script)
        self.assertIn("hashlib.sha256", self.script)
        self.assertIn("clean test dependency:tree", self.script)
        self.assertIn("MAVEN_USER_HOME", self.script)
        self.assertIn("HARNESS_MAVEN_CACHE", self.script)
        audit = self.script.index("tools/protocol-harness/audit.py")
        for argument in ("--maven-cache", "--wheels", "--maven-distribution"):
            self.assertIn(argument, self.script[audit:audit + 400])
        # The server's lease fixtures are checked right after the restore, before the vulnerability scan; they run
        # `dotnet run` and Java's source-file mode, so the restore and ensure_java come first.
        restore = self.script.index('dotnet restore "$SOLUTION"')
        leases = self.script.index('"$HARNESS_PYTHON" scripts/offline_leases.py')
        self.assertLess(restore, leases)
        self.assertLess(leases, self.script.index("python3 scripts/vulnerabilities.py"))
        self.assertLess(self.script.index("\nensure_java\n"), leases)
        self.assertLess(self.script.index('"$HARNESS_PYTHON" -m pip install'), leases)
        replay = self.script.index("verify.py --check docs/security/interoperability-vectors.json")
        self.assertIn("unittest discover -s tools/protocol-harness/tests", self.script[replay:replay + 300])
        self.assertLess(self.script.index(install), self.script.index("clean test dependency:tree"))
        self.assertLess(self.script.index("clean test dependency:tree"), audit)
        self.assertLess(audit, replay)
        # Nothing from the first harness line to the replay may fail soft.
        harness = self.script[self.script.index("python3 -m venv"):replay]
        for soft in ("|| true", "|| :", "set +e"):
            self.assertNotIn(soft, harness)
        self.assertNotIn("benchmark.py", self.script)
        self.assertNotIn("verify.py --output", self.script)
        self.assertNotIn("verify.py --write", self.script)

    @unittest.skipUnless(shutil.which("sh"), "needs a POSIX shell")
    def test_GivenHarnessCI_WhenParsed_ThenProductionChecksRetained(self):
        # The release gate: only a pull request into main runs scripts/verify_protocol.py, which blocks it while the
        # review is pending. yggdrasil.yml runs ci/branch-policy.sh as the branchPolicy; the test check does not.
        self.assertRegex(self.pipeline, r"(?m)^branchPolicy:\n(  .*\n)*?  run: sh ci/branch-policy\.sh$")
        blocked = self.branch_policy("main")
        self.assertEqual(1, blocked.returncode)
        self.assertEqual("BLOCKED: Protocol security and client review is pending.\n", blocked.stdout)
        for base in ("develop", "feature/anything"):
            with self.subTest(base=base):
                self.assertEqual(0, self.branch_policy(base).returncode)
        self.assertNotIn("python3 scripts/verify_protocol.py", self.script)
        # One unfiltered run: a test with neither Category trait still runs, and none runs twice (a unit run and a
        # functional run used to come first, and this run repeated every test they ran).
        tests = re.split(r"(?m)^dotnet test ", self.script)[1:]
        self.assertEqual(1, len(tests))
        full = tests[0].split("\n\n", 1)[0]
        self.assertIn('"$SOLUTION"', full)
        self.assertNotIn("--filter", full)
        self.assertIn("--logger trx", full)
        self.assertIn('--collect:"XPlat Code Coverage"', full)
        self.assertIn("python3 scripts/coverage.py --report-only", self.script)
        self.assertRegex(self.script, r"(?m)^SOLUTION=src/ArturRios\.Cerberus\.sln$")
        self.assertIn("docker build", self.docker_script)
        self.assertNotIn("--push", self.docker_script)
        self.assertNotIn("docker push", self.docker_script)
        self.assertRegex(self.pipeline, r"(?m)^  docker:.*\n(    .*\n)*?    docker: true\b")
        self.assertRegex(self.pipeline, r"(?m)^  test:.*\n(    .*\n)*?    run: ci/test\.sh$")
        self.assertRegex(self.pipeline, r"(?m)^  test:.*\n(    .*\n)*?    docker: true\b")

    @unittest.skipUnless(shutil.which("sh"), "needs a POSIX shell")
    def test_GivenUnzipShim_WhenExtracting_ThenModesKeptAndUnknownFlagsRejected(self):
        java = (ROOT / "ci/java.sh").read_text()
        shim = java.split("<<'SHIM'\n", 1)[1].split("\nSHIM\n", 1)[0] + "\n"
        path = os.pathsep.join([str(Path(sys.executable).parent), os.environ.get("PATH", "")])
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            script = folder / "unzip"
            script.write_text(shim)
            archive = folder / "bundle.zip"
            with zipfile.ZipFile(archive, "w") as bundle:
                for name, mode in (("apache/bin/mvn", 0o100755), ("apache/lib/a.jar", 0o100644)):
                    entry = zipfile.ZipInfo(name)
                    entry.external_attr = mode << 16
                    bundle.writestr(entry, "x")
                bundle.writestr("apache/no-mode.txt", "x")
            env = {**os.environ, "PATH": path}
            run = lambda *arguments: subprocess.run(["sh", str(script), *arguments], cwd=folder, env=env,
                                                    capture_output=True, text=True, timeout=30)
            result = run("-q", str(archive), "-d", str(folder / "out"))
            self.assertEqual(0, result.returncode, result.stderr)
            if os.name == "posix":
                self.assertEqual(0o755, (folder / "out/apache/bin/mvn").stat().st_mode & 0o777)
                self.assertEqual(0o644, (folder / "out/apache/lib/a.jar").stat().st_mode & 0o777)
            self.assertTrue((folder / "out/apache/no-mode.txt").is_file())
            for arguments in (("-o", str(archive)), ("-x", str(archive), "-d", str(folder / "other")), ()):
                with self.subTest(arguments=arguments):
                    self.assertEqual(2, run(*arguments).returncode)
            self.assertFalse((folder / "other").exists())

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
