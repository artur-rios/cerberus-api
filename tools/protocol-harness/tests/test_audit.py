"""Execute the audit with temporary real artifacts and injected public feed replies."""
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

AUDIT = Path(__file__).resolve().parents[1] / "audit.py"


class AuditTest(unittest.TestCase):
    def setUp(self):
        self.assertTrue(AUDIT.is_file(), "dependency audit feature missing")
        spec = importlib.util.spec_from_file_location("protocol_audit", AUDIT)
        self.api = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.api)
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.cache = self.root / "maven"
        self.wheels = self.root / "wheels"
        self.cache.mkdir()
        self.wheels.mkdir()
        self.jar = self.cache / "example/library/1/library-1.jar"
        self.jar.parent.mkdir(parents=True)
        self.jar.write_bytes(b"test-only artifact")
        self.wheel = self.wheels / "example-1-py3-none-any.whl"
        self.wheel.write_bytes(b"test-only wheel")
        self.distribution = self.root / "maven.zip"
        embedded = io.BytesIO()
        with zipfile.ZipFile(embedded, "w") as archive:
            archive.writestr("META-INF/maven/example/embedded/pom.properties",
                             "groupId=example\nartifactId=embedded\nversion=2\n")
        with zipfile.ZipFile(self.distribution, "w") as archive:
            archive.writestr("apache-maven-1/lib/embedded-2.jar", embedded.getvalue())
        self.lock = self.root / "lock.json"
        self.data = {
            "schemaVersion": 1,
            "mavenDistribution": {"version": "1", "sha256": self.digest(self.distribution)},
            "packages": [
                {"ecosystem": "Maven", "name": "example:library", "version": "1",
                 "relativePath": "example/library/1/library-1.jar", "sha256": self.digest(self.jar)},
                {"ecosystem": "PyPI", "name": "example", "version": "1",
                 "filename": self.wheel.name, "sha256": self.digest(self.wheel)}]}
        self.save()
        self.sent = []

    @staticmethod
    def digest(path):
        return hashlib.sha256(path.read_bytes()).hexdigest()

    def save(self):
        self.lock.write_text(json.dumps(self.data), encoding="utf-8")

    def run_audit(self, response=None, transport=None, installed=None):
        def feed(payload):
            self.sent.append(payload)
            return response if response is not None else {"results": [{} for _ in payload["queries"]]}
        return self.api.audit(self.lock, maven_cache=self.cache, wheels=self.wheels,
                              maven_distribution=self.distribution,
                              installed_versions={"example": "1"} if installed is None else installed,
                              transport=transport or feed)

    def rejected(self, **kwargs):
        with self.assertRaises(self.api.AuditFailure) as caught:
            self.run_audit(**kwargs)
        self.assertEqual("unsupported_dependency", str(caught.exception))

    def test_GivenCompleteCleanFeed_WhenAudited_ThenEveryBuildAndRuntimeCoordinateIsQueried(self):
        result = self.run_audit()
        queries = self.sent[0]["queries"]
        self.assertEqual(4, result["queriedPackages"])
        self.assertEqual({("Maven", "example:library", "1"), ("PyPI", "example", "1"),
                          ("Maven", "example:embedded", "2"), ("Maven", "org.apache.maven:apache-maven", "1")},
                         {(q["package"]["ecosystem"], q["package"]["name"], q["version"]) for q in queries})
        self.assertTrue(all(set(q) == {"package", "version"} for q in queries))
        self.assertEqual("pass", result["status"])
        self.assertIn("scannedAtUtc", result)
        self.assertIn("limitations", result)

    def test_GivenVulnerability_WhenAudited_ThenFailClosedWithPublicFinding(self):
        with self.assertRaises(self.api.AuditFailure) as caught:
            self.run_audit(response={"results": [{"vulns": [{"id": "TEST-1", "modified": "2026-01-01T00:00:00Z"}]}, {}, {}, {}]})
        self.assertEqual("unsupported_dependency", str(caught.exception))
        self.assertEqual(["TEST-1"], caught.exception.report["findings"][0]["ids"])

    def test_GivenMalformedOrIncompleteFeed_WhenAudited_ThenFailClosed(self):
        for response in ({}, {"results": []}, {"results": [{}]}, {"results": None},
                         {"results": [None] * 4}, {"results": [{"vulns": None}] * 4},
                         {"results": [{"error": "secret diagnostic"}] * 4},
                         {"results": [{"next_page_token": "unresolved"}] * 4},
                         {"results": [{"vulns": [{}]}] * 4}):
            with self.subTest(response=response):
                self.rejected(response=response)

    def test_GivenEmptyVulnerabilityArrays_WhenAudited_ThenPass(self):
        self.assertEqual("pass", self.run_audit(response={"results": [{"vulns": []}] * 4})["status"])

    def test_GivenFeedOutage_WhenAudited_ThenRedactedFailure(self):
        def failed(_):
            raise OSError("fixture-password-must-not-leak")
        self.rejected(transport=failed)

    def test_GivenChangedOrMissingArtifact_WhenAudited_ThenNoFeedQuery(self):
        self.jar.write_bytes(b"changed")
        self.rejected()
        self.assertEqual([], self.sent)
        self.jar.unlink()
        self.rejected()

    def test_GivenExtraResolvedJar_WhenAudited_ThenNoFeedQuery(self):
        (self.cache / "unlocked.jar").write_bytes(b"unlocked")
        self.rejected()
        self.assertEqual([], self.sent)

    def test_GivenChangedWheel_WhenAudited_ThenReject(self):
        self.wheel.write_bytes(b"changed")
        self.rejected()

    def test_GivenChangedMavenDistribution_WhenAudited_ThenReject(self):
        self.distribution.write_bytes(b"changed")
        self.rejected()

    def test_GivenInstalledVersionDrift_WhenAudited_ThenReject(self):
        self.rejected(installed={"example": "2"})
        self.rejected(installed={})

    def test_GivenUnsafeOrUnresolvedLock_WhenAudited_ThenReject(self):
        for change in ({"relativePath": "../escape.jar"}, {"ecosystem": "Unknown"},
                       {"version": ""}, {"sha256": "not-a-hash"}, {"name": "example:wrong"}):
            with self.subTest(change=change):
                original = dict(self.data["packages"][0])
                self.data["packages"][0].update(change)
                self.save()
                self.rejected()
                self.data["packages"][0] = original
        self.data["packages"] = []
        self.save()
        self.rejected()

    def test_GivenDuplicateLockedCoordinate_WhenAudited_ThenReject(self):
        self.data["packages"].append(dict(self.data["packages"][0]))
        self.save()
        self.rejected()

    def test_GivenUnknownEmbeddedBuildDependency_WhenAudited_ThenReject(self):
        with zipfile.ZipFile(self.distribution, "w") as archive:
            archive.writestr("apache-maven-1/lib/unknown.jar", b"not qualified")
        self.data["mavenDistribution"]["sha256"] = self.digest(self.distribution)
        self.save()
        self.rejected()

    def test_GivenPinnedEmbeddedJarWithoutMetadata_WhenAudited_ThenItsCoordinateIsQueried(self):
        jar = io.BytesIO()
        with zipfile.ZipFile(jar, "w") as archive:
            archive.writestr("test.class", b"public test data")
        with zipfile.ZipFile(self.distribution, "a") as archive:
            archive.writestr("apache-maven-1/lib/legacy.jar", jar.getvalue())
        self.data["mavenDistribution"]["sha256"] = self.digest(self.distribution)
        self.data["mavenDistribution"]["embeddedCoordinateOverrides"] = [{
            "relativePath": "apache-maven-1/lib/legacy.jar", "name": "example:legacy", "version": "3",
            "sha256": hashlib.sha256(jar.getvalue()).hexdigest()}]
        self.save()
        self.assertEqual(5, self.run_audit()["queriedPackages"])
        self.assertIn({"package": {"ecosystem": "Maven", "name": "example:legacy"}, "version": "3"},
                      self.sent[0]["queries"])
        self.data["mavenDistribution"]["embeddedCoordinateOverrides"][0]["sha256"] = "0" * 64
        self.save()
        self.rejected()


if __name__ == "__main__":
    unittest.main()
