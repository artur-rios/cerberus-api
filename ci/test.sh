#!/usr/bin/env bash
# The `test` check: the old Tests workflow's `test` job, step for step. yggdrasil.yml runs it in the pinned .NET SDK
# image from the repository's root, with the CI engine's socket (`docker: true`): the functional suite starts a
# PostgreSQL container per run through Testcontainers.
#
# In order: the specification check, the Python helpers' tests, the protocol harness (its locked dependencies, their
# audit, the corpus replay and both native suites), then restore, the vulnerability scan, the build, the generated
# OpenAPI contract, the unit, functional and complete test runs, and the coverage report with its minimum. No path
# filter: a test suite runs for every change.
#
# The release gate (scripts/verify_protocol.py, which fails while the protocol review is pending) is not here: the old
# job ran it only for pull requests into main, pushes to main and tags, and a check knows no branch. yggdrasil.yml's
# `branchPolicy` runs it for pull requests into main (ci/branch-policy.sh).
#
# The coverage report is written to docs/coverage-report (scripts/coverage.py), which the check archives with the
# .trx results of every test project. The report is written before the minimum is evaluated, so a build that fails
# the minimum still has the report that shows why.
#
# REPORTGENERATOR_LICENSE, when set and not empty, is ReportGenerator's PRO license (yggdrasil.yml binds the Jenkins
# credential reportgenerator-license to it). Without it -- a renovate/* build binds no credential, nor does a run on
# your machine -- scripts/coverage.py makes the free report and says so. It is never printed.
set -euo pipefail

# Renovate follows this version (the comment below); it is a tool, so there is no checksum to update with it.
# renovate: datasource=nuget depName=dotnet-reportgenerator-globaltool
REPORTGENERATOR_VERSION=5.5.11
SOLUTION=src/ArturRios.Cerberus.sln

# The repository's root, wherever this is run from.
cd "$(dirname "${BASH_SOURCE[0]}")/.."

export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

# The .NET images set ASPNETCORE_HTTP_PORTS=8080 (the runner did not). The functional tests start Kestrel hosts on
# port 0 and read the address back; with the variable set each host also binds [::]:8080 and the tests then try to
# connect to an unspecified address.
unset ASPNETCORE_HTTP_PORTS ASPNETCORE_HTTPS_PORTS ASPNETCORE_URLS

# shellcheck source=ci/python.sh
. ci/python.sh
# shellcheck source=ci/java.sh
. ci/java.sh
ensure_python3
ensure_java
ensure_unzip

# Everything the harness and the report download or build goes under one directory of this run's own, and goes with
# it: the check runs as whoever the CI engine runs it as, with no promise about a writable home.
HARNESS_TMP=$(mktemp -d)
trap 'rm -rf "$HARNESS_TMP"' EXIT

python3 scripts/verify_specs.py

python3 -m unittest discover -s scripts -p "test_*.py" --verbose

# The protocol harness: non-production reference implementations (tools/protocol-harness). Its Python wheels are
# installed from hash-locked files into a virtual environment, its Maven distribution is the one the lock pins, and
# the Java half builds with the Maven wrapper against a cache of this run's own.
export MAVEN_USER_HOME="$HARNESS_TMP/maven-user"
export HARNESS_MAVEN_CACHE="$HARNESS_TMP/m2"
export CERBERUS_PYTHON_WHEELS="$HARNESS_TMP/wheels"
export CERBERUS_MAVEN_DISTRIBUTION="$HARNESS_TMP/maven.zip"
python3 -m venv "$HARNESS_TMP/venv"
export HARNESS_PYTHON="$HARNESS_TMP/venv/bin/python"
"$HARNESS_PYTHON" -m pip download --only-binary=:all: --require-hashes -r tools/protocol-harness/python/requirements.lock --dest "$CERBERUS_PYTHON_WHEELS"
"$HARNESS_PYTHON" -m pip install --no-index --find-links "$CERBERUS_PYTHON_WHEELS" --only-binary=:all: --require-hashes -r tools/protocol-harness/python/requirements.lock
"$HARNESS_PYTHON" - <<'PY'
import hashlib, json, os, pathlib, urllib.request
lock = json.loads(pathlib.Path('tools/protocol-harness/dependencies.lock.json').read_text())['mavenDistribution']
target = pathlib.Path(os.environ['CERBERUS_MAVEN_DISTRIBUTION'])
with urllib.request.urlopen(lock['url'], timeout=60) as response:
    data = response.read(32 * 1024 * 1024 + 1)
if len(data) > 32 * 1024 * 1024 or hashlib.sha256(data).hexdigest() != lock['sha256']:
    raise SystemExit('unsupported_dependency')
target.write_bytes(data)
PY
tools/protocol-harness/java/mvnw -B -Dmaven.repo.local="$HARNESS_MAVEN_CACHE" -f tools/protocol-harness/java/pom.xml clean test dependency:tree

"$HARNESS_PYTHON" tools/protocol-harness/audit.py \
  --maven-cache "$HARNESS_MAVEN_CACHE" \
  --wheels "$CERBERUS_PYTHON_WHEELS" \
  --maven-distribution "$CERBERUS_MAVEN_DISTRIBUTION"

"$HARNESS_PYTHON" tools/protocol-harness/verify.py --check docs/security/interoperability-vectors.json
python3 -m unittest discover -s tools/protocol-harness/tests -v

dotnet restore "$SOLUTION"

# `dotnet list package --vulnerable` exits 0 whether or not it found anything, so the script parses what it printed.
python3 scripts/vulnerabilities.py

# Built once so every test run can use --no-build.
dotnet build "$SOLUTION" --configuration Release --no-restore

python3 scripts/openapi.py

# The Category trait comes from the [UnitFact] / [UnitTheory] attributes; both suites collect coverage (a
# coverage.cobertura.xml per project under TestResults/) and the report below merges every file.
dotnet test "$SOLUTION" --configuration Release --no-build \
  --filter "Category=Unit" \
  --collect:"XPlat Code Coverage"

# These start a real PostgreSQL container per run through Testcontainers: the CI engine's socket (`docker: true`).
dotnet test "$SOLUTION" --configuration Release --no-build \
  --filter "Category=Functional" \
  --collect:"XPlat Code Coverage"

# Unfiltered, so a test with neither trait runs somewhere, and the one that writes the .trx results.
dotnet test "$SOLUTION" --configuration Release --no-build \
  --logger trx \
  --collect:"XPlat Code Coverage"

# scripts/coverage.py calls the tool by name, so it is put on the PATH.
tools=$HARNESS_TMP/tools
dotnet tool install dotnet-reportgenerator-globaltool --version "$REPORTGENERATOR_VERSION" --tool-path "$tools"
export PATH="$tools:$PATH"

# --report-only builds the HTML from the coverage files the runs above wrote and fails when merged line coverage is
# below the minimum in scripts/coverage.py, so a local run and this one answer the same question.
python3 scripts/coverage.py --report-only
