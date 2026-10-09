#!/usr/bin/env python3
"""Fail-closed public-coordinate OSV audit; never accepts protocol/secret input.

Use a dedicated freshly resolved Maven cache, not a general developer cache.
Maven's ZIP hash pins its embedded libraries; every embedded Maven coordinate
is also queried. Only an explicit complete clean feed response passes.
"""
import argparse
from datetime import datetime, timezone
import hashlib
from importlib import metadata
import io
import json
import os
from pathlib import Path
import re
import urllib.request
import zipfile

FEED = "https://api.osv.dev/v1/querybatch"
LIMITATIONS = "Version-based OSV snapshot, not proof of library correctness; native runtime/OS vulnerabilities require separate review."


class AuditFailure(RuntimeError):
    def __init__(self, report=None):
        super().__init__("unsupported_dependency")
        self.report = report or {"status": "failed", "code": "unsupported_dependency"}


def _reject():
    raise AuditFailure()


def _sha256(path):
    with path.open("rb") as artifact:
        return hashlib.file_digest(artifact, "sha256").hexdigest()


def _artifact(root, relative, digest):
    if (not isinstance(relative, str) or not relative or "\\" in relative
            or Path(relative).is_absolute() or ".." in Path(relative).parts
            or not isinstance(digest, str) or not re.fullmatch(r"[0-9a-f]{64}", digest)):
        _reject()
    path = root / relative
    if not path.resolve().is_relative_to(root.resolve()) or not path.is_file() or _sha256(path) != digest:
        _reject()
    return path


def _coordinate(ecosystem, name, version):
    if (ecosystem not in ("Maven", "PyPI") or not isinstance(name, str)
            or not re.fullmatch(r"[A-Za-z0-9_.:-]+", name) or not isinstance(version, str)
            or not re.fullmatch(r"[A-Za-z0-9_.+-]+", version)):
        _reject()
    if ecosystem == "Maven" and name.count(":") != 1:
        _reject()
    return ecosystem, name, version


def _embedded_coordinates(distribution, overrides):
    coordinates = set()
    pinned = {}
    for entry in overrides:
        name = entry["relativePath"]
        if name in pinned:
            _reject()
        pinned[name] = entry
    used = set()
    with zipfile.ZipFile(distribution) as outer:
        libraries = [n for n in outer.namelist() if n.endswith(".jar")]
        if not libraries:
            _reject()
        for name in libraries:
            with zipfile.ZipFile(io.BytesIO(outer.read(name))) as jar:
                properties = [p for p in jar.namelist() if p.startswith("META-INF/maven/") and p.endswith("/pom.properties")]
                if not properties:
                    entry = pinned.get(name)
                    if entry is None or hashlib.sha256(outer.read(name)).hexdigest() != entry["sha256"]:
                        _reject()
                    coordinates.add(_coordinate("Maven", entry["name"], entry["version"]))
                    used.add(name)
                for prop in properties:
                    values = {}
                    for line in jar.read(prop).decode("utf-8").splitlines():
                        if line and not line.startswith(("#", "!")):
                            key, separator, value = line.partition("=")
                            if not separator or key.strip() in values:
                                _reject()
                            values[key.strip()] = value.strip()
                    coordinates.add(_coordinate("Maven", values["groupId"] + ":" + values["artifactId"], values["version"]))
    if used != set(pinned):
        _reject()
    return coordinates


def _query(payload):
    request = urllib.request.Request(FEED, data=json.dumps(payload).encode("ascii"),
                                     headers={"Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(request, timeout=30) as response:
        # Reject unbounded/truncated feed content instead of interpreting it as clean.
        raw = response.read(8 * 1024 * 1024 + 1)
        if len(raw) > 8 * 1024 * 1024:
            _reject()
        return json.loads(raw)


def audit(lock: Path, *, maven_cache=None, wheels=None, maven_distribution=None,
          installed_versions=None, transport=None) -> dict:
    try:
        return _audit(Path(lock), maven_cache=maven_cache, wheels=wheels,
                      maven_distribution=maven_distribution, installed_versions=installed_versions,
                      transport=transport or _query)
    except AuditFailure:
        raise
    except Exception:
        # No filesystem, network or library exception text can contain leaked data.
        raise AuditFailure() from None


def _audit(lock, *, maven_cache, wheels, maven_distribution, installed_versions, transport):
    data = json.loads(lock.read_text(encoding="utf-8"))
    if data.get("schemaVersion") != 1 or not isinstance(data.get("packages"), list) or not data["packages"]:
        _reject()
    maven_cache = Path(maven_cache or os.environ["CERBERUS_MAVEN_CACHE"])
    wheels = Path(wheels or os.environ["CERBERUS_PYTHON_WHEELS"])
    maven_distribution = Path(maven_distribution or os.environ["CERBERUS_MAVEN_DISTRIBUTION"])
    if not maven_cache.is_dir() or not wheels.is_dir():
        _reject()
    coordinates, jars, wheel_files = set(), set(), set()
    for package in data["packages"]:
        coordinate = _coordinate(package["ecosystem"], package["name"], package["version"])
        if coordinate in coordinates:
            _reject()
        coordinates.add(coordinate)
        ecosystem, name, version = coordinate
        if ecosystem == "Maven":
            relative = package["relativePath"]
            group, artifact = name.split(":")
            expected = f"{group.replace('.', '/')}/{artifact}/{version}/{artifact}-{version}"
            if not isinstance(relative, str) or not relative.startswith(expected) or not relative.endswith(".jar"):
                _reject()
            _artifact(maven_cache, relative, package["sha256"])
            jars.add(relative)
        else:
            filename = package["filename"]
            if not isinstance(filename, str) or Path(filename).name != filename or not filename.endswith(".whl"):
                _reject()
            _artifact(wheels, filename, package["sha256"])
            wheel_files.add(filename)
            installed = metadata.version(name) if installed_versions is None else installed_versions.get(name)
            if installed != version:
                _reject()
    if jars != {p.relative_to(maven_cache).as_posix() for p in maven_cache.rglob("*.jar")}:
        _reject()
    if wheel_files != {p.name for p in wheels.glob("*.whl")}:
        _reject()
    distribution = data["mavenDistribution"]
    _artifact(maven_distribution.parent, maven_distribution.name, distribution["sha256"])
    coordinates.add(_coordinate("Maven", "org.apache.maven:apache-maven", distribution["version"]))
    coordinates.update(_embedded_coordinates(maven_distribution, distribution.get("embeddedCoordinateOverrides", [])))
    ordered = sorted(coordinates)
    payload = {"queries": [{"package": {"ecosystem": ecosystem, "name": name}, "version": version}
                           for ecosystem, name, version in ordered]}
    response = transport(payload)
    if (not isinstance(response, dict) or set(response) != {"results"}
            or not isinstance(response["results"], list) or len(response["results"]) != len(ordered)):
        _reject()
    findings = []
    for coordinate, result in zip(ordered, response["results"], strict=True):
        # Pagination is unresolved evidence, not an empty result.
        if not isinstance(result, dict) or set(result) - {"vulns"}:
            _reject()
        vulns = result.get("vulns", [])
        if not isinstance(vulns, list):
            _reject()
        ids = []
        for vuln in vulns:
            if (not isinstance(vuln, dict) or not isinstance(vuln.get("id"), str)
                    or not re.fullmatch(r"[A-Za-z0-9_.-]+", vuln["id"])
                    or not isinstance(vuln.get("modified"), str)):
                _reject()
            ids.append(vuln["id"])
        if ids:
            ecosystem, name, version = coordinate
            findings.append({"ecosystem": ecosystem, "name": name, "version": version, "ids": sorted(set(ids))})
    report = {"status": "failed" if findings else "pass", "scannedAtUtc": datetime.now(timezone.utc).isoformat(),
              "feed": FEED, "queriedPackages": len(ordered), "lockSha256": _sha256(lock),
              "findings": findings, "limitations": LIMITATIONS}
    if findings:
        raise AuditFailure(report)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--lock", type=Path, default=Path(__file__).with_name("dependencies.lock.json"))
    parser.add_argument("--maven-cache", type=Path)
    parser.add_argument("--wheels", type=Path)
    parser.add_argument("--maven-distribution", type=Path)
    args = parser.parse_args()
    try:
        report = audit(args.lock, maven_cache=args.maven_cache, wheels=args.wheels,
                       maven_distribution=args.maven_distribution)
    except AuditFailure as failure:
        print(json.dumps(failure.report, sort_keys=True))
        return 1
    print(json.dumps(report, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
