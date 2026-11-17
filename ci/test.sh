#!/usr/bin/env bash
# The `test` check: the old Tests workflow's `test` job, step for step. yggdrasil.yml runs it in the pinned .NET SDK
# image from the repository's root, with the CI engine's socket (`docker: true`): the functional suite starts a
# PostgreSQL container per run through Testcontainers.
#
# In order: the specification check, the Python helpers' tests, the protocol harness (its locked dependencies, their
# audit, the corpus replay and both native suites), then restore, the server lease check
# (scripts/offline_leases.py), the vulnerability scan, the format check, the build, the generated
# OpenAPI contract, one run of every test, and the coverage report with its minimum. No path
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
REPORTGENERATOR_VERSION=5.6.0
SOLUTION=src/ArturRios.Cerberus.sln

# The repository's root, wherever this is run from.
cd "$(dirname "${BASH_SOURCE[0]}")/.."

export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

# The .NET images set ASPNETCORE_HTTP_PORTS=8080 (the runner did not). The functional tests start Kestrel hosts on
# port 0 and read the address back; with the variable set each host also binds [::]:8080 and the tests then try to
# connect to an unspecified address.
unset ASPNETCORE_HTTP_PORTS ASPNETCORE_HTTPS_PORTS ASPNETCORE_URLS

# Every directory this script and the helpers below make (the interpreter, the JDK, the unzip shim, the harness's
# downloads) is removed when the script ends, however it ends: ci/python.sh and ci/java.sh add theirs here.
CLEANUP_DIRS=()
cleanup() {
  if [ "${#CLEANUP_DIRS[@]}" -gt 0 ]; then
    rm -rf "${CLEANUP_DIRS[@]}"
  fi
}
trap cleanup EXIT

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
CLEANUP_DIRS+=("$HARNESS_TMP")

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

# The server's lease fixtures (tools/OfflineLeaseGen, run with `dotnet run`) checked by independent native clients: the
# harness's Python and, in Java's source-file mode, `java`, which ensure_java put on the PATH above. It needs the
# restore above and the harness's virtual environment.
"$HARNESS_PYTHON" scripts/offline_leases.py

# `dotnet list package --vulnerable` exits 0 whether or not it found anything, so the script parses what it printed.
python3 scripts/vulnerabilities.py

# Fails when `dotnet format` (whitespace, code style and analyzers, at their default severities: the repository has no
# .editorconfig) would change a file; `dotnet format src/ArturRios.Cerberus.sln` applies the same fixes.
dotnet format "$SOLUTION" --verify-no-changes --no-restore

# Built once so every test run can use --no-build.
dotnet build "$SOLUTION" --configuration Release --no-restore

python3 scripts/openapi.py

# Every test once, unit and functional alike: no Category filter, so a test with neither Category trait runs too. The
# functional tests start PostgreSQL containers through Testcontainers: the CI engine's socket (`docker: true`). Each
# run writes its .trx results and a coverage.cobertura.xml under TestResults/, which the report below merges. (A
# unit run and a functional run used to come first; this run repeated every test they ran.)
#
# The runs go side by side. WebApi.Tests runs as four processes: its fixtures configure the API through process-wide
# environment variables, so one process can only run its classes one at a time, but processes of their own (each with
# its own fixture database and identity provider) can run together. The shards split the classes by name, and the last
# is everything the first three are not, so every test runs exactly once: a new class lands in the last shard. Each
# shard runs from a copy of the build output of its own: the coverage collector instruments the assemblies in place and
# restores them afterwards, and the tests write their contract fixtures next to them.
WEBAPI_NAMESPACE=ArturRios.Cerberus.WebApi.Tests
WEBAPI_SHARDS=(
  "FullyQualifiedName~$WEBAPI_NAMESPACE.CollectionGrant"
  "FullyQualifiedName~$WEBAPI_NAMESPACE.Collection&FullyQualifiedName!~$WEBAPI_NAMESPACE.CollectionGrant"
  "FullyQualifiedName~$WEBAPI_NAMESPACE.Folder|FullyQualifiedName~$WEBAPI_NAMESPACE.Profile"
  "FullyQualifiedName!~$WEBAPI_NAMESPACE.Collection&FullyQualifiedName!~$WEBAPI_NAMESPACE.Folder&FullyQualifiedName!~$WEBAPI_NAMESPACE.Profile"
)
run_tests() { # <project> <.trx prefix> [dotnet test arguments...]
  local project=$1 prefix=$2
  shift 2
  dotnet test "$project" --configuration Release --no-build \
    --logger "trx;LogFilePrefix=$prefix" \
    --results-directory "$(dirname "$project")/TestResults" \
    --collect:"XPlat Code Coverage" "$@" >"$HARNESS_TMP/$prefix.log" 2>&1
}
runs=()
for project in $(find tests -name '*.Tests.csproj' | sort); do
  name=$(basename "$project" .csproj)
  if [ "$name" = "$WEBAPI_NAMESPACE" ]; then
    output=$(dirname "$(find "$(dirname "$project")/bin/Release" -name "$name.dll" -print -quit)")
    for shard in "${!WEBAPI_SHARDS[@]}"; do
      # The project still names the run (so the coverage collector is found); OutputPath points it at the copy.
      copy=$HARNESS_TMP/$name.$((shard + 1))
      cp -R "$output" "$copy"
      run_tests "$project" "$name.$((shard + 1))" -p:OutputPath="$copy/" -p:AppendTargetFrameworkToOutputPath=false \
        --filter "${WEBAPI_SHARDS[$shard]}" & runs+=("$!:$name.$((shard + 1))")
    done
  else
    run_tests "$project" "$name" & runs+=("$!:$name")
  fi
done
failed=0
for run in "${runs[@]}"; do
  if wait "${run%%:*}"; then status=passed; else status=FAILED; failed=1; fi
  echo "== ${run#*:}: $status"
  # Each run's output in one piece, without the per-test lines of a detailed logger.
  cat "$HARNESS_TMP/${run#*:}.log"
done
[ "$failed" -eq 0 ]

# scripts/coverage.py calls the tool by name, so it is put on the PATH.
tools=$HARNESS_TMP/tools
dotnet tool install dotnet-reportgenerator-globaltool --version "$REPORTGENERATOR_VERSION" --tool-path "$tools"
export PATH="$tools:$PATH"

# --report-only builds the HTML from the coverage files the runs above wrote and fails when merged line coverage is
# below the minimum in scripts/coverage.py, so a local run and this one answer the same question.
python3 scripts/coverage.py --report-only
