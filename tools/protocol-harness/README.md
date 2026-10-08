# Cerberus reference protocol harness

Non-production Java/Python implementations of the approved v1 design. This is
not a real client, an API implementation, or independent security approval.
`docs/security/protocol-review.json` stays pending. Do not use public fixture
secrets for a vault. Production .NET projects and Docker packaging exclude this
directory; its tests do not count toward production coverage.

## Current implementation

Strict encoding is implemented and tested independently. Encryption, recovery,
lease verification, cross-language evidence and benchmarks are subsequent tasks
in [the approved plan](../../docs/superpowers/plans/2026-10-07-protocol-harness.md).
There is no successful interoperability or benchmark claim at this stage.

## Reproducible local setup

Supported reference measurement/test platform: Linux x86_64, JDK 25 and CPython
3.14. The checked hashes currently cover that platform's Python wheels. Other
platforms require separately qualified artifact locks; do not drop `--require-hashes`.
Record actual patch/vendor versions with each run; this desktop environment says
nothing about future phone/browser resource budgets or key storage.

From the repository root, choose a new task-specific temporary directory:

```bash
HARNESS_TMP=$(mktemp -d /tmp/cerberus-protocol.XXXXXX)
python3.14 -m venv "$HARNESS_TMP/venv"
"$HARNESS_TMP/venv/bin/python" -m pip install --only-binary=:all: --require-hashes -r tools/protocol-harness/python/requirements.lock
export MAVEN_USER_HOME="$HARNESS_TMP/maven-user"
tools/protocol-harness/java/mvnw -Dmaven.repo.local="$HARNESS_TMP/m2" -f tools/protocol-harness/java/pom.xml test
PYTHONPATH=tools/protocol-harness/python "$HARNESS_TMP/venv/bin/python" -m unittest discover -s tools/protocol-harness/python/tests -t tools/protocol-harness/python -v
```

The official Maven wrapper is 3.3.4, with Maven distribution 3.10.0 and its SHA-256
pinned in `.mvn/wrapper/maven-wrapper.properties`. The ZIP's local SHA-256 was
recorded only after matching the official Maven Central SHA-512. Maven and Python
caches live in the selected temporary directory, not in production projects.

`dependencies.lock.json` records actual runtime versions, package/artifact hashes
and resolved Maven build/test dependencies. Before primitive adoption the next
task audits the graph and package versions against current public advisories.
No missing runtime, empty test run or failed dependency scan may be counted green.

## Encoding rules

Authenticated arrays use strict UTF-8 compact JSON with ASCII strings; required
control-character Unicode escapes use lowercase hexadecimal in both encoders.
Wire object parsers reject duplicate fields recursively, nonintegral number
tokens, invalid Unicode, unknown root fields and aggregate byte-limit violations.
Owning contract validators enforce nested field schemas and positive integer
ranges. Identifiers are canonical nonzero lowercase GUIDs; base64url is canonical
and unpadded with an exact expected decoded length.

Tests use Given–When–Then names and literal expected bytes, not one implementation
as the other's validator. Failure messages are stable codes, never raw input.
