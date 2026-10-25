# shellcheck shell=bash
# Sourced by ci/test.sh (not run on its own): makes sure `python3` is on the PATH.
#
# The `test` check runs Python: the helpers in scripts/ (the specification check, the vulnerability scan, the OpenAPI
# document check, the coverage gate) and the protocol harness, whose hash-locked wheels are for CPython 3.14 (the old
# workflow ran them all but the first few under setup-python's 3.14). The pinned .NET SDK image has no python3, and a
# check may run as a user that cannot install packages, so the interpreter is fetched instead: a relocatable CPython
# build from astral-sh/python-build-standalone, pinned by release and by checksum, unpacked into a directory of its
# own. A python3 that is already there is used, so on a machine of your own it must be 3.14.
#
# Renovate follows the release below (the comment above it), but not the checksums: when it moves the release, copy the
# sha256 of that release's two assets (its release page lists them) and CPYTHON_VERSION into this file by hand.
# renovate: datasource=github-releases depName=astral-sh/python-build-standalone versioning=loose
CPYTHON_RELEASE=20261021
CPYTHON_VERSION=3.14.9

ensure_python3() {
  if command -v python3 >/dev/null 2>&1; then
    return 0
  fi

  local arch asset sha256 directory archive
  arch=$(uname -m)
  case $arch in
    x86_64)
      asset="cpython-${CPYTHON_VERSION}+${CPYTHON_RELEASE}-x86_64-unknown-linux-gnu-install_only.tar.gz"
      sha256=dfa4c5a40c47f338f41658017be550c72247c391e6bfb1fa4da03ebc8451abdf ;;
    aarch64)
      asset="cpython-${CPYTHON_VERSION}+${CPYTHON_RELEASE}-aarch64-unknown-linux-gnu-install_only.tar.gz"
      sha256=847895ba727440b012207bb0192282d7e182eb991ae5c7bb5c35c6a12a99e732 ;;
    *)
      echo "ci/python.sh: no python3 on the PATH, and no pinned build for $arch" >&2
      return 1 ;;
  esac

  # The download directory goes once the archive is unpacked into a directory of its own: the interpreter stays for
  # the rest of the check (and goes with the check's container).
  directory=$(mktemp -d)
  archive="$directory/python.tar.gz"
  echo "ci/python.sh: no python3 on the PATH; fetching CPython $CPYTHON_VERSION ($CPYTHON_RELEASE)"
  curl --fail --silent --show-error --location --retry 3 -o "$archive" \
    "https://github.com/astral-sh/python-build-standalone/releases/download/${CPYTHON_RELEASE}/${asset//+/%2B}" \
    || { rm -rf "$directory"; return 1; }
  if ! echo "$sha256  $archive" | sha256sum --check --strict --status; then
    echo "ci/python.sh: the CPython archive does not match its pinned checksum" >&2
    rm -rf "$directory"
    return 1
  fi
  local python_home
  python_home=$(mktemp -d)
  tar -xzf "$archive" -C "$python_home"
  rm -rf "$directory"
  export PATH="$python_home/python/bin:$PATH"
  python3 --version
}
