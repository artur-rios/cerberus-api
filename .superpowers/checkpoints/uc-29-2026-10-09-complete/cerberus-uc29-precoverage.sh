#!/usr/bin/env bash
set -euo pipefail
cd /tmp/cerberus-uc29-worktree
python3 /tmp/cerberus-uc29-verify-openapi.py > /tmp/cerberus-uc29-openapi-shape.log 2>&1
python3 scripts/vulnerabilities.py --quiet > /tmp/cerberus-uc29-nuget-audit.log 2>&1
MAVEN_USER_HOME=/tmp/cerberus-protocol.fCQIq1/maven-user HARNESS_MAVEN_CACHE=/tmp/cerberus-protocol.fCQIq1/m2 /tmp/cerberus-protocol.fCQIq1/venv/bin/python tools/protocol-harness/audit.py --maven-cache /tmp/cerberus-protocol.fCQIq1/m2 --wheels /tmp/cerberus-protocol.fCQIq1/wheels --maven-distribution /tmp/cerberus-protocol.fCQIq1/maven.zip > /tmp/cerberus-uc29-native-audit.log 2>&1
MAVEN_USER_HOME=/tmp/cerberus-protocol.fCQIq1/maven-user HARNESS_MAVEN_CACHE=/tmp/cerberus-protocol.fCQIq1/m2 /tmp/cerberus-protocol.fCQIq1/venv/bin/python tools/protocol-harness/verify.py --check docs/security/interoperability-vectors.json > /tmp/cerberus-uc29-native-verify.log 2>&1
python3 -m unittest discover -s tools/protocol-harness/tests > /tmp/cerberus-uc29-native-helpers.log 2>&1
python3 -m unittest discover -s scripts -p 'test_*.py' > /tmp/cerberus-uc29-python-helpers.log 2>&1
python3 scripts/verify_specs.py > /tmp/cerberus-uc29-specs.log 2>&1
git diff --check > /tmp/cerberus-uc29-diff-check.log 2>&1
