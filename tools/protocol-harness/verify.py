"""Process orchestration only. No cryptography or shared contract validator."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
HARNESS = ROOT / "tools/protocol-harness"
KNOWN_IDS = {"hkdf", "gcm", "ecdsa", "thumbprint", "argon2", "hpke"}
VERSION = "1.0.0"
LIMIT = 64 * 1024 * 1024


class HarnessFailure(Exception):
    def __init__(self, code="invalid_protocol"):
        self.code = code if code == "unsupported_dependency" else "invalid_protocol"
        super().__init__(self.code)


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False, allow_nan=False).encode()).hexdigest()


def _pairs(pairs):
    result = {}
    for key, value in pairs:
        if key in result: raise HarnessFailure()
        result[key] = value
    return result


def read_document(path):
    path = Path(path)
    if not path.is_file() or path.stat().st_size > LIMIT: raise HarnessFailure()
    try:
        value = json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=_pairs,
                           parse_constant=lambda _: (_ for _ in ()).throw(HarnessFailure()))
        if type(value) is not dict: raise HarnessFailure()
        return value
    except (ValueError, UnicodeError, OSError, RecursionError): raise HarnessFailure() from None


def atomic_write(path, value):
    path = Path(path); raw = json.dumps(value, sort_keys=True, indent=2, ensure_ascii=False, allow_nan=False).encode()+b'\n'
    if len(raw) > LIMIT: raise HarnessFailure()
    path.parent.mkdir(parents=True, exist_ok=True)
    name = None
    try:
        with tempfile.NamedTemporaryFile(dir=path.parent, prefix=".harness-", delete=False) as handle:
            name = handle.name; handle.write(raw); handle.flush(); os.fsync(handle.fileno())
        os.replace(name, path); name = None
    finally:
        if name: Path(name).unlink(missing_ok=True)


def manifest():
    document = read_document(HARNESS / "fixtures/case-manifest.json")
    if document.get("schemaVersion") != 1 or document.get("classification") != "public-test-fixtures": raise HarnessFailure()
    cases = document.get("cases")
    if type(cases) is not list or not cases: raise HarnessFailure()
    result = {}
    for case in cases:
        if type(case) is not dict or set(case) != {"id", "kind", "expect"} or case["id"] in result: raise HarnessFailure()
        if case["expect"] not in ("success", "invalid_protocol") or type(case["id"]) is not str: raise HarnessFailure()
        result[case["id"]] = dict(kind=case["kind"], expect=case["expect"])
    return result


def _run(command):
    try:
        process = subprocess.run(command, cwd=ROOT, env={**os.environ, "PYTHONPATH": str(HARNESS/"python")},
                                 stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=300)
        if process.returncode or len(process.stdout)+len(process.stderr) > LIMIT: raise HarnessFailure()
        return process.stdout
    except FileNotFoundError: raise HarnessFailure("unsupported_dependency") from None
    except (subprocess.TimeoutExpired, OSError): raise HarnessFailure() from None


def invoke(command, operation, *inputs):
    with tempfile.TemporaryDirectory(prefix="cerberus-child-") as directory:
        target = Path(directory)/"result.json"
        _run([*command, operation, *map(str, inputs), str(target)])
        return read_document(target)


def _header(document, implementation):
    if type(document) is not dict or document.get("schemaVersion") != 1 or document.get("classification") != "public-test-fixtures" \
            or document.get("implementation") != implementation or document.get("version") != VERSION: raise HarnessFailure()


def _results(document, implementation, identities, digests=None):
    _header(document, implementation); rows = document.get("results")
    if type(rows) is not list or len(rows) != len(identities): raise HarnessFailure()
    found = set()
    for row in rows:
        if type(row) is not dict or row.get("id") not in identities or row["id"] in found or row.get("status") != "pass": raise HarnessFailure()
        if digests is not None and row.get("digest") != digests[row["id"]]: raise HarnessFailure()
        found.add(row["id"])
    if found != set(identities): raise HarnessFailure()
    return rows


def _cases(document, implementation, expected):
    _header(document, implementation); rows = document.get("cases")
    if type(rows) is not list or len(rows) != len(expected): raise HarnessFailure()
    found = set()
    for case in rows:
        if type(case) is not dict or case.get("id") not in expected or case["id"] in found: raise HarnessFailure()
        rule = expected[case["id"]]
        if case.get("kind") != rule["kind"] or case.get("expect") != rule["expect"] or case.get("digest") != digest(case.get("output")): raise HarnessFailure()
        deterministic = rule["kind"] in ("encoding", "kdf", "hkdf", "signature", "clock", "model")
        if case.get("deterministic", False) is not deterministic: raise HarnessFailure()
        found.add(case["id"])
    if found != set(expected): raise HarnessFailure()
    return rows


def native_suites(java_command, python_command):
    cache = os.environ.get("HARNESS_MAVEN_CACHE", str(Path.home()/".m2/repository"))
    reports = HARNESS/"java/target/surefire-reports"
    # Remove stale report files only, then run the complete suite; no empty discovery.
    for path in reports.glob("TEST-*.xml"): path.unlink()
    _run([str(HARNESS/"java/mvnw"), "-o", "-Dmaven.repo.local="+cache, "-f", str(HARNESS/"java/pom.xml"), "test"])
    java = dict(tests=0, failures=0, errors=0, skipped=0)
    for path in reports.glob("TEST-*.xml"):
        root = ET.parse(path).getroot()
        for key in java: java[key] += int(root.attrib[key])
    runner = "import io,json,unittest; s=unittest.defaultTestLoader.discover('tools/protocol-harness/python/tests',top_level_dir='tools/protocol-harness/python'); r=unittest.TextTestRunner(stream=io.StringIO()).run(s); print(json.dumps(dict(tests=r.testsRun,failures=len(r.failures),errors=len(r.errors),skipped=len(r.skipped)))); raise SystemExit(0 if r.wasSuccessful() else 1)"
    output = _run([python_command[0], "-c", runner]); python = json.loads(output)
    return dict(java=java, python=python)


def _provenance():
    filenames = ["fixtures/input.json", "fixtures/known-answers.json", "fixtures/sources.json", "fixtures/case-manifest.json", "dependencies.lock.json"]
    filenames += [str(path.relative_to(HARNESS)) for directory, pattern in ((HARNESS/"java/src/main", "*.java"), (HARNESS/"python/cerberus_protocol", "*.py")) for path in sorted(directory.rglob(pattern))]
    filenames += ["verify.py"]
    return {name: hashlib.sha256((HARNESS/name).read_bytes()).hexdigest() for name in filenames if (HARNESS/name).exists()}


def verify(java_command, python_command, output=None, check=None):
    try:
        if not java_command or not python_command or output is not None and check is not None: raise HarnessFailure()
        expected = manifest(); native = native_suites(java_command, python_command)
        if set(native) != {"java", "python"}: raise HarnessFailure()
        for report in native.values():
            if set(report) != {"tests", "failures", "errors", "skipped"} or any(type(x) is not int or x < 0 for x in report.values()) \
                    or report["tests"] == 0 or any(report[x] for x in ("failures", "errors", "skipped")): raise HarnessFailure()
        commands = {"java": java_command, "python": python_command}
        known = {impl: _results(invoke(command, "self-test"), impl, KNOWN_IDS) for impl, command in commands.items()}
        provenance = _provenance()
        if check is not None:
            artifact = read_document(check)
            if artifact.get("schemaVersion") != 1 or artifact.get("classification") != "public-test-fixtures" or artifact.get("provenance") != provenance: raise HarnessFailure()
            producers = artifact.get("producers")
            if type(producers) is not list or len(producers) != 2 or {x.get("implementation") for x in producers} != set(commands): raise HarnessFailure()
        else:
            producers = [invoke(command, "produce", HARNESS/"fixtures/input.json") for command in commands.values()]
        if len(producers) != 2 or {item.get("implementation") for item in producers} != set(commands): raise HarnessFailure()
        checks = []
        with tempfile.TemporaryDirectory(prefix="cerberus-cross-") as directory:
            for producer in producers:
                impl = producer.get("implementation"); cases = _cases(producer, impl, expected)
                target = Path(directory)/(impl+".json"); atomic_write(target, producer)
                digests = {case["id"]: case["digest"] for case in cases}
                for consumer, command in commands.items():
                    results = _results(invoke(command, "consume", target), consumer, expected, digests)
                    checks.append(dict(producer=impl, consumer=consumer, results=results))
        # Deterministic cases must match producer bytes; randomized ciphertext is replayed.
        left = {case["id"]: case for case in producers[0]["cases"]}
        for case in producers[1]["cases"]:
            if case.get("deterministic") is True and left[case["id"]]["output"] != case["output"]: raise HarnessFailure()
        result = dict(schemaVersion=1, classification="public-test-fixtures", executedAtUtc=datetime.now(timezone.utc).isoformat(),
                      provenance=provenance, nativeSuites=native, knownAnswers=known, producers=producers, checks=checks)
        if output is not None: atomic_write(output, result)
        return result
    except HarnessFailure: raise
    except FileNotFoundError: raise HarnessFailure("unsupported_dependency") from None
    except Exception: raise HarnessFailure() from None


def default_commands():
    cache = Path(os.environ.get("HARNESS_MAVEN_CACHE", str(Path.home()/".m2/repository")))
    lock = read_document(HARNESS/"dependencies.lock.json")
    jars = [cache/p["relativePath"] for p in lock["packages"] if p["ecosystem"] == "Maven" and p["name"] in ("org.bouncycastle:bcprov-jdk18on", "com.fasterxml.jackson.core:jackson-core")]
    if len(jars) != 2 or any(not p.is_file() for p in jars): raise HarnessFailure("unsupported_dependency")
    return ["java", "-cp", os.pathsep.join(map(str, [HARNESS/"java/target/classes", *jars])), "cerberus.protocol.Main"], [sys.executable, "-m", "cerberus_protocol"]


def main():
    parser = argparse.ArgumentParser(); group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--output", type=Path); group.add_argument("--check", type=Path); args = parser.parse_args()
    try:
        java, python = default_commands(); result = verify(java, python, args.output, args.check)
        print(json.dumps(dict(status="pass", cases=len(result["producers"][0]["cases"]), directions=len(result["checks"]))))
        return 0
    except HarnessFailure as error: print(json.dumps(dict(code=error.code))); return 1


if __name__ == "__main__": raise SystemExit(main())
