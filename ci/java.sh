# shellcheck shell=bash
# Sourced by ci/test.sh (not run on its own): makes sure the protocol harness can run its Java half.
#
# The harness (tools/protocol-harness/java) is built and tested with its Maven wrapper on JDK 25, which the old
# workflow installed with setup-java (Temurin 25). The pinned .NET SDK image has no JDK, and a check may run as a user
# that cannot install packages, so a Temurin JDK is fetched instead: the release archive of adoptium/temurin25-binaries,
# pinned by release and by checksum, unpacked into a directory of its own, with JAVA_HOME and the PATH pointing at it.
# A JDK that is already there (a machine of your own with `java` on the PATH) is used.
#
# The wrapper unpacks the Maven distribution it downloads (a .zip, whose checksum is pinned in
# tools/protocol-harness/java/.mvn/wrapper) with `unzip`, which the image has not either. ensure_unzip puts a small
# `unzip` on the PATH, built on the Python that ci/python.sh made available, for the one form the wrapper uses.
#
# Every directory made here is added to CLEANUP_DIRS, which ci/test.sh declares and removes from its EXIT trap: the
# download directory, the JDK (it must outlive this function, for the rest of the check) and the shim's.
#
# Renovate follows the release below (the comment above it), but not the checksums: when it moves the release, copy the
# sha256 of that release's two archives (its release page lists them) into this file by hand.
# renovate: datasource=github-releases depName=adoptium/temurin25-binaries versioning=loose
TEMURIN_RELEASE="jdk-25.0.5+8"

ensure_java() {
  if command -v java >/dev/null 2>&1 && command -v javac >/dev/null 2>&1; then
    return 0
  fi

  # jdk-25.0.5+8 is the release's tag; its archive names the same version as 25.0.5_8.
  local version arch asset sha256 directory archive java_home
  version=${TEMURIN_RELEASE#jdk-}
  case $(uname -m) in
    x86_64)
      arch=x64
      sha256=68982529869257d0ee6b824ec5aa270ff8b118bb438670da0b97ebdab178d7fb ;;
    aarch64)
      arch=aarch64
      sha256=bb5b2ded252f21c6090cf780b0a408ad0d83bbb557e702955643557f13231880 ;;
    *)
      echo "ci/java.sh: no java on the PATH, and no pinned build for $(uname -m)" >&2
      return 1 ;;
  esac
  asset="OpenJDK25U-jdk_${arch}_linux_hotspot_${version/+/_}.tar.gz"

  directory=$(mktemp -d)
  CLEANUP_DIRS+=("$directory")
  archive="$directory/jdk.tar.gz"
  echo "ci/java.sh: no java on the PATH; fetching Temurin $TEMURIN_RELEASE"
  curl --fail --silent --show-error --location --retry 3 -o "$archive" \
    "https://github.com/adoptium/temurin25-binaries/releases/download/${TEMURIN_RELEASE/+/%2B}/${asset}"
  if ! echo "$sha256  $archive" | sha256sum --check --strict --status; then
    echo "ci/java.sh: the Temurin archive does not match its pinned checksum" >&2
    return 1
  fi
  java_home=$(mktemp -d)
  CLEANUP_DIRS+=("$java_home")
  # --no-same-owner: as root, tar would keep the archive's owner.
  tar -xzf "$archive" -C "$java_home" --strip-components=1 --no-same-owner
  rm -rf "$directory"
  export JAVA_HOME="$java_home"
  export PATH="$JAVA_HOME/bin:$PATH"
  java -version
}

ensure_unzip() {
  if command -v unzip >/dev/null 2>&1; then
    return 0
  fi

  local bin
  bin=$(mktemp -d)
  CLEANUP_DIRS+=("$bin")
  cat >"$bin/unzip" <<'SHIM'
#!/bin/sh
# unzip [-q] <archive> [-d <directory>]: the one form the Maven wrapper uses, and nothing else. Python's zipfile drops
# the permission bits when it extracts, so every member is extracted on its own and gets the mode its entry records
# (the upper half of external_attr) when there is one: the launchers under Maven's bin/ stay executable.
set -eu
usage() { echo "unzip (ci/java.sh's shim): usage: unzip [-q] <archive> [-d <directory>]" >&2; exit 2; }
archive=
destination=.
while [ $# -gt 0 ]; do
  case $1 in
    -q) ;;
    -d) [ $# -ge 2 ] || usage; shift; destination=$1 ;;
    -*) echo "unzip (ci/java.sh's shim): unsupported option $1" >&2; usage ;;
    *) [ -z "$archive" ] || usage; archive=$1 ;;
  esac
  shift
done
[ -n "$archive" ] || usage
exec python3 - "$archive" "$destination" <<'PY'
import os
import sys
import zipfile

archive, destination = sys.argv[1:]
directories = []
with zipfile.ZipFile(archive) as bundle:
    for entry in bundle.infolist():
        path = bundle.extract(entry, destination)
        mode = entry.external_attr >> 16
        if not mode:
            continue
        if entry.is_dir():
            directories.append((path, mode & 0o7777))  # after its members: a read-only directory can't be filled
        else:
            os.chmod(path, mode & 0o7777)
for path, mode in reversed(directories):
    os.chmod(path, mode)
PY
SHIM
  chmod +x "$bin/unzip"
  export PATH="$bin:$PATH"
}
