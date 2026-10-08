# Cerberus reference protocol harness

Non-production Java/Python implementations of the approved v1 design. This is
not a real client, an API implementation, or independent security approval.
`docs/security/protocol-review.json` stays pending. Do not use public fixture
secrets for a vault. Production .NET projects and Docker packaging exclude this
directory; its tests do not count toward production coverage.

## Current implementation

Strict encoding, qualified primitives, scoped encrypted bundles, signed recipient
wrapping, exact-body proofs, atomic local recovery/refresh/retry models and strict
offline lease, clock and signing-key trust models are implemented independently.
The native suites include 160 Java tests and 140 Python tests. Known-answer selections
and source hashes/licenses are in [fixtures/sources.json](fixtures/sources.json).
The process interface exchanges 309 manifest cases, including each envelope field,
trusted binding changes, raw-body ordering, malformed encodings, policy/header
rejections and local model scenarios. These remain reference tests; production
transactions, identity/permission checks and mobile time anchors are separate obligations.

The orchestrator runs both complete native suites and all six published primitive
families, then checks each producer with both consumers. It writes evidence only after
all checks pass, and preserves previous evidence on failure. Corpus replay verifies
the original randomized ciphertexts instead of demanding fresh random bytes match.
Input, manifest, dependency and source hashes bind the evidence to the checked code.
Benchmarks remain a subsequent task in
[the approved plan](../../docs/superpowers/plans/2026-10-07-protocol-harness.md).

The fail-closed dependency audit is implemented (14 tests). The initial OSV scan
on 2026-10-08 queried 154 distinct coordinates, including Maven's bundled build
libraries, and failed. See [the recorded result](audit-failure-2026-10-08.json).
The owner approved targeted plugin dependency upgrades. Fresh resolution and a
new live audit returned no findings across 153 package versions; see
[the patched scan](audit-pass-2026-10-08.json). This is not a vulnerability assessment of the production
.NET API or independent security approval.

The resources plugin now selects Plexus 3.6.1, the clean plugin Plexus 4.0.3,
and the dependency plugin BeanUtils 1.11.0. Hashes reflect a new clean cache;
the failed scan is retained as historical evidence against its original lock.
BeanUtils 1.9.4 is pulled in by the dependency plugin's Velocity tools; its
[advisory](https://github.com/advisories/GHSA-wxr5-93ph-8wr9) identifies 1.11.0
as patched. The [Plexus advisory](https://github.com/advisories/GHSA-6fmv-xxpf-w3cw)
identifies 3.6.1 and 4.0.3 as patched within their respective major lines.
Do not suppress findings or change cryptographic parameters to pass this gate.

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
and resolved Maven build/test dependencies. The audit checks all actual JAR and
wheel hashes, installed Python versions and Maven distribution contents before
sending only public package names/versions to the official OSV batch API.
Five distribution JARs omit Maven metadata; their explicit package identities
and byte hashes are pinned in `mavenDistribution.embeddedCoordinateOverrides`.
Unknown or changed bundled JARs fail, rather than being excluded from the scan.
No missing runtime, empty test run or failed dependency scan may be counted green.

Run against the exact fresh resolved cache, downloaded locked Python wheels and
verified Maven ZIP, using the Python environment where locked packages are installed:

```bash
"$HARNESS_TMP/venv/bin/python" tools/protocol-harness/audit.py \
  --maven-cache "$HARNESS_TMP/m2" \
  --wheels "$HARNESS_TMP/wheels" \
  --maven-distribution "$HARNESS_TMP/maven.zip"
python3 -m unittest discover -s tools/protocol-harness/tests -p test_audit.py -v
```

The supplied paths must exist; this command does not download or regenerate
evidence. Missing input, drift, vulnerabilities and feed failure exit nonzero.
An OSV result with no findings is a dated database observation, not independent
security approval or a claim that all vulnerabilities are known.

## Primitive qualification

The suites check RFC 5869 HKDF, published NIST AES-256-GCM cases (including empty
plaintext and nonempty AAD), RFC 6979 deterministic P256/P1363 signatures,
RFC 7638 canonical hashing, RFC 9106 Argon2id and the selected RFC 9180 HPKE base
suite. Altered tags/AAD/messages, invalid points/scalars and malformed/trailing,
wrong-curve or inconsistent private-key DER are rejected with stable diagnostics.
Protocol Argon2 is fixed at 65536 KiB / 3 passes / 4 lanes / 32 output bytes;
the RFC primitive-only test's alternate memory, secret and AD are not public
protocol parameters. RSA is not an accepted protocol key type.

HPKE qualification calls native libraries, not custom KEM/KDF code. Java checks
the original published seal/open bytes for sequences 0 and 1. Python's public
single-shot API fixes AAD empty; its pinned upstream test-only native entry point
checks the original sequence-0 vector with nonempty AAD, and a separate public-API
smoke test checks the approved empty-AAD profile. This is primitive qualification,
not yet evidence of cross-language protocol interoperability.

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

## Content envelopes

Normal `seal` APIs obtain fresh secure 32-byte key salts and 12-byte nonces.
Only explicit `sealFixture` / `seal_fixture` test APIs accept fixed materials.
Receivers supply the expected owner, content kind, immutable resource ID and
epoch; the ciphertext's own claims never establish those trusted bindings.
Public content APIs accept only `cerberus-content-v1` and empty extra context;
the following wrapper tasks own their named extensions.

`NonceGuard` is an atomic local reference model. A valid salt reservation remains
consumed after a failed or unsent encryption. Retries reuse saved bytes. Budgets
are keyed by a nonlogged fingerprint of root and immutable deep-copied context;
each domain permits at most 2^32 reservations. Its explicit `initialCount` test
seed applies to the first observed domain only; new roots/epochs start at zero.
This does not implement distributed accounting or production persistence.

## Password and recovery bundles

Password wrapping uses exact strict UTF-8 bytes, without normalization, trimming,
case folding or truncation. KDF metadata is fixed to the qualified Argon2 profile.
Unwrap rejects invalid parameters, epoch, field sets, base64 and aggregate bounds
before invoking the KDF. Authenticated plaintext is then parsed strictly; sorted
distinct roots must belong to caller-supplied scope membership, and the embedded
unlock private key must match the independently registered scoped verifier.
Wrapping checks a trusted local provisioning bundle's structural/scope rules;
it cannot establish authorization from that bundle's own claims.

Normal wrapping obtains fresh password salt, key salt and nonce. Explicit fixture
APIs accept public test material only. A protection slot epoch is separate from
the epochs of the roots it carries; changing a password preserves permitted roots
and does not revoke copies already held elsewhere.

Recovery uses a raw 32-byte random secret directly as the wrapping root, never
Argon2 or a conversion to an ECDSA scalar. The encrypted bundle includes an
independent generation-specific proof private key and account-scope protection
bundle; unwrap verifies both against external public verifiers and membership.
Public test fixtures model this provisioning, not a complete real-client key graph.
These APIs are client-side reference code; no server route returns decrypted keys.
Managed/native memory handling still needs independent security review.

## Recipient envelopes and trusted key transitions

Recipients use native HPKE base mode with the qualified P256/HKDF-SHA256/AES-256-GCM
suite, empty AAD and one raw 32-byte resource root per fresh context. Base mode
alone does not authenticate the sender. A separate P1363 author signature binds
the full context and encoded encapsulation/ciphertext; receivers validate trusted
account, recipient, author, grant and resource bindings before decapsulation.
Envelope-supplied public keys are not accepted as authority.

`ClientTrust(accountId, recipientJwk, authorJwk, directoryRevision)` requires an
independently trusted account and separate role keys. The plan's original
three-argument constructor omitted the account input needed by its required
cross-account rejection; the implementation makes that authority explicit.
An increasing directory revision and the previously pinned author's signature
authorize each role-specific transition. New-key self-signatures, role reuse and
cross-account transitions fail without changing pins. Revocation excludes future
wrappers; it cannot erase roots already copied by an earlier recipient.

## Challenge proofs

Challenge signatures bind the exact original bounded UTF-8 JSON body hash, not a
parsed/reserialized body. Property-order or whitespace changes need a new proof.
The strict parser rejects duplicate fields recursively, malformed Unicode/BOM,
noninteger number tokens and compressed/non-JSON bytes; each owning operation
still validates its own exact body field set. Challenge/signature inputs are
separate from the operation body, where the idempotency key belongs.

The explicit operation, authenticated identity, account/scope, epoch, revision,
nullable recovery generation and exclusive 60-second window all bind the proof.
Only the caller's externally registered scoped/purpose verifier is accepted.
Issuing a challenge creates neither vault access nor a session. These stateless
helpers do not establish registry membership or consume challenges; the following
atomic recovery reference model owns those state checks.

## Executed interoperability and local state models

From the repository root, export the cache used by the locked build and run with
its isolated Python environment:

```bash
export HARNESS_MAVEN_CACHE="$HARNESS_TMP/m2"
export MAVEN_USER_HOME="$HARNESS_TMP/maven-user"
"$HARNESS_TMP/venv/bin/python" tools/protocol-harness/verify.py --check docs/security/interoperability-vectors.json
```

After deliberate reference-code changes, generate new evidence with
`--output docs/security/interoperability-vectors.json`, then replay it with `--check`.
CI replays committed evidence; it never regenerates evidence to hide drift.
The explicit `produce`, `consume` and `self-test` child commands accept public
fixtures only. Success diagnostics contain case counts/digests; failure diagnostics
contain only `invalid_protocol` or `unsupported_dependency`. Public passwords,
roots and private keys belong only in labeled fixture files.

The recovery model retains public verifiers and opaque wrapper bytes, and commits
replacement sets, revisions, challenge consumption and a nonsecret outcome under
one local lock. Fresh authentication precedes retry lookup; authenticated identity,
idempotency key and exact original-body hash determine retry identity. Signature
malleability cannot create a new operation. Refresh additionally requires current
vault access and invalidates the old recovery generation. Neither this model nor
its 100 repeated two-thread races proves PostgreSQL transaction correctness.

The lease verifier requires externally expected issuer, audience, identity, account,
scope, epochs, revisions, grant membership and renewal policy. Enabled expiration
is exclusive; explicitly disabled renewal forbids `exp`. Verification never renews
the clock. Only an explicit trusted authenticated online renewal event anchors an
enabled-expiry clock; regression or restart blocks use until renewed, while the
modeled high-water time remains retained. Disabled-expiry policy does not manufacture
periodic expiration. Lease-key rotations use a separate signed domain and retain
old verification pins for outstanding leases; they cannot instantly revoke offline
copies. A real client still needs reviewed platform time and key-storage behavior.
