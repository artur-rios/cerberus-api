#!/bin/sh
# The part of the old Tests workflow that is about the branch, not the code: a pull request into main must pass
# scripts/verify_protocol.py, which fails while the protocol security and client review is pending (CONTRIBUTING.md,
# Testing). The workflow ran it for pull requests into main, pushes to main and tags; a check knows no branch, so it
# is run here, as `branchPolicy` in yggdrasil.yml, after scripts/branch-policy.sh, with BASE_REF set, from the
# checkout of the pull request's head. Pull requests into any other branch have nothing more to check.
set -eu
cd "$(dirname "$0")/.."

[ "${BASE_REF:-}" = main ] || { echo "'${HEAD_REF:-}' -> '${BASE_REF:-}': nothing more to check."; exit 0; }

exec python3 scripts/verify_protocol.py
