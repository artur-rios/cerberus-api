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
  archive="$directory/jdk.tar.gz"
  echo "ci/java.sh: no java on the PATH; fetching Temurin $TEMURIN_RELEASE"
  curl --fail --silent --show-error --location --retry 3 -o "$archive" \
    "https://github.com/adoptium/temurin25-binaries/releases/download/${TEMURIN_RELEASE/+/%2B}/${asset}" \
    || { rm -rf "$directory"; return 1; }
  if ! echo "$sha256  $archive" | sha256sum --check --strict --status; then
    echo "ci/java.sh: the Temurin archive does not match its pinned checksum" >&2
    rm -rf "$directory"
    return 1
  fi
  java_home=$(mktemp -d)
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
  cat >"$bin/unzip" <<'SHIM'
#!/bin/sh
# unzip [-q] <archive> -d <directory>: the one form the Maven wrapper uses. Python's zipfile drops the permission bits,
# so the files under a bin/ directory (Maven's launchers) are made executable again.
set -eu
archive=
destination=.
while [ $# -gt 0 ]; do
  case $1 in
    -q) ;;
    -d) shift; destination=$1 ;;
    *) archive=$1 ;;
  esac
  shift
done
python3 -m zipfile -e "$archive" "$destination"
find "$destination" -path '*/bin/*' -type f -exec chmod +x {} +
SHIM
  chmod +x "$bin/unzip"
  export PATH="$bin:$PATH"
}
