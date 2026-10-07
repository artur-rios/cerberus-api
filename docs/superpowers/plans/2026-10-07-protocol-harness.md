# Cerberus Java/Python Protocol Harness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build an isolated, independently implemented Java/Python harness that produces real interoperability and performance evidence for the Cerberus v1 protocol without claiming security approval.

**Architecture:** Java/Bouncy Castle and Python/cryptography each own all parsing, authenticated-context construction, cryptographic calls, and validation. A Python standard-library orchestrator exchanges data-only fixtures between separate processes and checks actual results. Recovery and clock behavior are explicitly local reference models, not API implementations.

**Tech Stack:** JDK 25, Maven 3.10.0, Bouncy Castle `bcprov-jdk18on` 1.86, Jackson Core 2.22.3, JUnit Jupiter 6.1.3; Python 3.14, cryptography 50.0.2, unittest; existing .NET 10 production verification unchanged.

**Spec:** [Approved corrected design](../specs/2026-10-07-protocol-harness-design.md). Read the entire design before execution; it defines all field sets and authenticated byte arrays.

**Execution:** Preserve inline/native execution using `superpowers:executing-plans`. This plan awaits owner review; its creation is not implementation approval. Work in the existing isolated `feature/protocol-harness-design` worktree; preserve the user's original worktree changes.

## Global Constraints

- This is a non-production reference implementation; no production project references, product routes, live Heimdall calls, credentials, email, deployments, or issue closures.
- NFR-11/IR-09 and `docs/security/protocol-review.json` remain pending. Owner design approval and green tests are not independent security approval.
- AES-256-GCM: 32-byte key, 12-byte nonce, 16-byte tag. Per-envelope key salt: 32 bytes. One encryption per derived key.
- Argon2id v1.3: memory 65,536 KiB, 3 passes, 4 lanes, 16-byte salt, 32-byte output; no secret or associated-data input. Reject other profiles before KDF work.
- HKDF-SHA-256 extract-and-expand: 32-byte output for envelope keys; use the exact context-specific `info` from the spec.
- HPKE base mode: KEM `0x0010`, KDF `0x0001`, AEAD `0x0002`; one message per context; empty context AAD; separate author signature.
- ECDSA P-256/SHA-256 deterministic RFC 6979 through library APIs; P1363 signatures exactly 64 bytes. Separate asymmetric key roles.
- Recovery secret and challenge nonce: 32 secure random bytes. Never convert a recovery secret into an ECDSA scalar.
- `C(array)` is strict UTF-8 compact JSON of constrained ASCII strings, arrays, integers, booleans, and null; not an arbitrary-object canonicalizer.
- Positive protocol integers: `1..9007199254740991`; timestamps: `0..9007199254740991`. Reject bool-as-int, exponent/decimal notation and numeric strings.
- GUIDs: lowercase nonzero D form. Base64url: canonical, unpadded, strict alphabet and length. Reject duplicate/unknown fields recursively.
- Request limit: `1048576` bytes for the whole request before decoding; also test smaller configured limits. Do not invent record-size limits.
- Exact password UTF-8, no normalization, trimming, case conversion or truncation; reject invalid Unicode.
- Nonce guard consumes salt/context even on failed or unsent encryption; root/context/epoch budget is at most `2^32` encryptions. It models local behavior, not distributed coordination.
- Challenge lifetime exactly 60 seconds; reject at expiry without skew. Hash exact operation-body bytes; idempotency key is in that body.
- Offline JWS: ES256, `typ=cerberus-offline-v1+jwt`, audience `cerberus-offline-clients-v1`; default enabled duration 86400 seconds, no invented universal maximum.
- Enabled offline use fails closed on wall/monotonic regression or restart until authenticated online renewal. Cached-token import cannot establish/reset an anchor.
- Normal APIs use secure randomness; deterministic fixture material is available only through explicitly test-labeled entry points.
- Pin dependencies and download hashes; audit both resolved graphs. Missing primitives, failed audits, absent tests or incomplete cross-checks block completion, never become skips.
- Never echo inputs, proofs, private keys, decrypted plaintext or raw exception messages in diagnostics; failures use `invalid_protocol` or `unsupported_dependency`.
- Create `docs/security/interoperability-vectors.json` only after genuine successful bidirectional runs. Fixture secrets are conspicuously public, never user keys.
- Full production .NET tests remain unfiltered and coverage remains separate. Do not weaken existing CI checks or protocol gating.

## Review Focus

1. Cross-language numeric coercion and aggregate-size overflow: booleans, exponent tokens, oversized nested requests and `2^53` must reject before expensive work (Tasks 1, 4).
2. Validly encoded but invalid EC/PKCS#8 material: off-curve/infinity, wrong curve, malformed DER and key-role substitution must reject without secret-bearing exceptions (Tasks 2, 4, 5).
3. Raw-body ambiguity and signature malleability: changed byte order, BOM, duplicates and alternate valid ECDSA signatures must not bypass body binding or create a second recovery operation (Tasks 6, 7).
4. Cached lease import and disabled-expiry transitions: imports cannot reset time; missing/extra `exp`, overflow, regression and restart retain explicit policy behavior (Task 8).
5. False-green orchestration: missing implementations, empty/skipped tests, stale/corrupt artifacts, dependency outages and malformed child output must fail closed without replacing valid evidence (Tasks 9–11).

---

## File responsibilities and interface conventions

All paths below are relative to the isolated repository worktree. Use these exact
prefixes when a task lists a filename:

- `J/` = `tools/protocol-harness/java/src/main/java/cerberus/protocol/`.
- `JT/` = `tools/protocol-harness/java/src/test/java/cerberus/protocol/`.
- `P/` = `tools/protocol-harness/python/cerberus_protocol/`.
- `PT/` = `tools/protocol-harness/python/tests/`.

| Files | Responsibility |
| --- | --- |
| `J/ProtocolJson.java`, `J/ProtocolError.java`; `P/encoding.py`, `P/errors.py` | Strict parsing, constrained array encoding, GUID/base64url/numeric boundaries; stable failure codes |
| `J/Primitives.java`, `J/Keys.java`; `P/primitives.py`, `P/keys.py` | Maintained primitive APIs and fixed-width key/signature encodings |
| `J/Context.java`, `J/Symmetric.java`, `J/NonceGuard.java`; `P/symmetric.py` | Context-bound AES envelopes and local key-use guard |
| `J/Protection.java`, `J/RecoveryBundle.java`; `P/protection.py`, `P/recovery_bundle.py` | Scoped plaintext bundle validation and password/recovery wrapping |
| `J/Recipient.java`, `J/ClientTrust.java`; `P/recipient.py`, `P/client_trust.py` | HPKE recipient envelopes, pinned author verification, client-key transitions |
| `J/Challenge.java`, `J/RecoveryModel.java`; `P/challenge.py`, `P/recovery_model.py` | Proof bytes and atomic local recovery/refresh/idempotency model |
| `J/Lease.java`, `J/OfflineClock.java`, `J/LeaseTrust.java`; `P/lease.py`, `P/offline_clock.py`, `P/lease_trust.py` | Strict JWS, trusted time and separate signing-key rotation |
| `J/Main.java`, `J/FixtureCases.java`; `P/__main__.py`, `P/fixture_cases.py` | Process interface and independently constructed positive/negative case execution |
| `tools/protocol-harness/verify.py`, `audit.py`, `benchmark.py` | Process orchestration, dependency audit, isolated measurement; no protocol crypto |
| `tools/protocol-harness/fixtures/input.json`, `known-answers.json`, `sources.json` | Public test inputs, authoritative expected outputs, license and source hashes; no fabricated execution claims |
| `tools/protocol-harness/README.md`, `dependencies.lock.json`, `python/requirements.lock`, `java/pom.xml` | Reproducible setup and isolated dependency graph |

Java uses `Map<String,Object>` for strictly validated wire objects, `byte[]` for
binary data, `long` for bounded wire integers, and named records for internal
contexts. Python uses `dict[str, object]`, `bytes`, exact `int` (not `bool`), and
dataclasses for those same internal contexts. Java signatures below are instance
methods unless marked static; Python counterparts use snake_case with the same
arguments and return shapes. Both throw their own `ProtocolError(code)` with no
input/exception text; `require_valid` in tests means asserting that error and no
partial output. No cross-language runtime imports or shared validator exist.

Test support consists of `JT/Fixtures.java` and `PT/fixtures.py`, each independently
loading only the data-only inputs. Produce `fixture(name)`/`Fixtures.load(name)`
as a dictionary/map, decoding labeled binary values into bytes only in that
language's test support. Inputs define canonical nonzero IDs
`00000000-0000-0000-0000-000000000001` through
`00000000-0000-0000-0000-000000000008`, epochs/revisions/generation `1`, fixed clock `1700000000`, public root
bytes `00..1f`, password `" vault-é "`, distinct role keys and scoped membership.
Assign IDs respectively to account, identity, profile, record, folder, collection,
grant and alternate account. Every fixture file labels its data as
`public-test-fixtures`. Each task extends its own test
support with the builders it needs; builders must call that language's API.

Run commands from the repository root. Python commands below use an isolated
virtual environment activated during Task 1; no system-wide package installs.
`tools/protocol-harness/java/mvnw` is the checksum-pinned Maven wrapper. Every task
has Java and Python tests, even when only one representative assertion is shown;
mirror that assertion and listed cases in the other suite, rather than sharing
executable test logic. Parameterized cases need distinct reported identifiers.

## Task 1: Strict encoding with reproducible test runners

**Files:** Create `J/ProtocolJson.java`, `J/ProtocolError.java`, `P/encoding.py`, `P/errors.py`, `P/__init__.py`, `PT/__init__.py`, `JT/Fixtures.java`, `PT/fixtures.py`, `tools/protocol-harness/java/pom.xml`, `java/mvnw`, `java/.mvn/wrapper/maven-wrapper.properties` under the harness, `tools/protocol-harness/dependencies.lock.json`, `tools/protocol-harness/python/requirements.lock`, `tools/protocol-harness/README.md`, `tools/protocol-harness/.gitignore`. Test `JT/EncodingTest.java`, `PT/test_encoding.py`.

**Interfaces:** Produces `ProtocolJson.context(List<Object>) -> byte[]`, `parse(byte[], Set<String> required, Set<String> optional, int maxBytes) -> Map<String,Object>`, `base64(byte[]) -> String`, `unbase64(String,int expectedLength) -> byte[]`, `guid(String) -> String`, `integer(Object,long min,long max) -> long`; Python `context`, `parse`, `base64`, `unbase64`, `guid`, `integer` with equivalent types. `ProtocolError.code` is `invalid_protocol` or `unsupported_dependency`. Contract validators call `parse` on raw inputs then recursively enforce their own exact nested schemas; generic parsing itself rejects duplicates and nonintegral number tokens recursively.

- [ ] Write `GivenCanonicalArray_WhenEncoded_ThenBytesMatch` and rejection tests in both suites:

```python
self.assertEqual(context(["cerberus-v1", 1, [], True, None]),
                 b'["cerberus-v1",1,[],true,null]')
for value in (True, 0, 9007199254740992, "1", 1.0):
    with self.assertRaises(ProtocolError): integer(value, 1, 9007199254740991)
for raw in (b'{"x":1,"x":2}', b'{"x":1e0}', b'{"x":1.0}', b'\xef\xbb\xbf{"x":1}'):
    with self.assertRaises(ProtocolError): parse(raw, {"x"}, set(), 1048576)
```

Include unknown/nested duplicate fields, invalid UTF-8/surrogates, non-ASCII
context strings, trailing JSON, reordered properties accepted, empty/uppercase/
zero GUIDs, base64 padding/alphabet/unused bits/decoded lengths, and total request
limit at/over boundary including many individually small fields (Review Focus 1).

- [ ] Bootstrap only the test runners and run RED: `tools/protocol-harness/java/mvnw -f tools/protocol-harness/java/pom.xml -Dtest=EncodingTest test`; `PYTHONPATH=tools/protocol-harness/python python -m unittest tests.test_encoding -v`. Expected missing `ProtocolJson`/`encoding`, not a network, discovery or missing-runner failure.
- [ ] Implement the listed encoding APIs using Jackson streaming strict duplicate detection and Python raw numeric-token hooks; reject nonintegral lexical forms before conversion. Bound UTF-8 bytes before parsing. Use separate constrained array writers and independent schema checks.
- [ ] Pin the declared dependencies, all resolved transitives and explicit Maven plugin versions; forbid ranges/snapshots. Pin Maven 3.10.0 wrapper distribution SHA-256 from the authenticated upstream artifact and Python wheel hashes. Record JDK/Python patch versions and graph/artifact hashes in `dependencies.lock.json`; use `/tmp` for local caches/venv. Document exact setup commands and ignore only harness build/cache outputs.
- [ ] Run both RED commands again: expected nonzero tests discovered, zero failures/errors/skips. Record counts and runner reports; do not equate an empty suite with success.
- [ ] Commit only this task's files: `git commit -m "feat: add strict harness encoding"` after explicit `git add` of the Files paths, not repository-wide staging.

## Task 2: Qualified primitives, keys and authoritative known answers

**Files:** Create `J/Primitives.java`, `J/Keys.java`, `P/primitives.py`, `P/keys.py`, `tools/protocol-harness/fixtures/input.json`, `tools/protocol-harness/fixtures/known-answers.json`, `tools/protocol-harness/fixtures/sources.json`, `tools/protocol-harness/audit.py`, `tools/protocol-harness/tests/test_audit.py`; extend test support and locks. Test `JT/PrimitivesTest.java`, `JT/KeysTest.java`, `PT/test_primitives.py`, `PT/test_keys.py`.

**Interfaces:** Consumes Task 1 `ProtocolJson.context(List<Object>) -> byte[]`, `base64(byte[]) -> String`, `unbase64(String,int) -> byte[]`. Produces `Primitives.hkdf(byte[] root,byte[] salt,byte[] info,int length) -> byte[]`, `argon2(byte[] password,byte[] salt) -> byte[]`, `gcmSeal(byte[] key,byte[] nonce,byte[] aad,byte[] plaintext) -> byte[]` (ciphertext+tag), `gcmOpen(byte[] key,byte[] nonce,byte[] aad,byte[] ciphertextAndTag) -> byte[]`, `sha256(byte[]) -> byte[]`; Python same snake_case. `Keys.generate() -> byte[]` PKCS#8, `publicJwk(byte[] privateDer) -> Map`, `thumbprint(Map jwk) -> String`, `sign(byte[] privateDer,byte[] message) -> byte[64]`, `verify(Map jwk,byte[] message,byte[] signature) -> boolean`, `validatePublic(Map jwk) -> Map`. Produce `audit(lock: Path) -> dict` in `audit.py`.

- [ ] Write `GivenRfc5869Case1_WhenDerived_ThenKnownBytesMatch`:

```python
self.assertEqual(hkdf(b'\x0b' * 22, bytes(range(13)), bytes(range(0xf0, 0xfa)), 42).hex(),
                 '3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865')
self.assertEqual(len(sign(fixture("author")["privateDer"], b"sample")), 64)
self.assertEqual(sign(fixture("author")["privateDer"], b"sample"),
                 sign(fixture("author")["privateDer"], b"sample"))
for jwk in (off_curve_jwk, infinity_encoding_jwk, wrong_curve_jwk):
    with self.assertRaises(ProtocolError): validate_public(jwk)
with self.assertRaises(ProtocolError): public_jwk(malformed_private_der)
```

Pin AES-256-GCM published NIST cases, RFC 6979 P-256/SHA-256 sample signature,
RFC 7638 example thumbprint, RFC 9106 Argon2id published vector (primitive-only
test may use that vector's parameters/secret/AD; protocol API cannot), and RFC
9180 P-256/HKDF-SHA-256/AES-256-GCM base-mode cases. Preserve upstream revision,
file hash, URL and license in sources, never use self-generated expected outputs
as known answers. Add `GivenInvalidKeyMaterial_WhenImported_ThenRedactedRejection`
for off-curve/infinity, wrong curves, malformed/trailing DER, zero/out-of-range
signature scalars and incorrect lengths (Review Focus 2). Audit tests simulate
vulnerabilities, empty/malformed/missing responses, network failure and lock drift.

- [ ] Run RED: `tools/protocol-harness/java/mvnw -f tools/protocol-harness/java/pom.xml -Dtest=PrimitivesTest,KeysTest test`; `PYTHONPATH=tools/protocol-harness/python python -m unittest tests.test_primitives tests.test_keys -v`; `python3 -m unittest discover -s tools/protocol-harness/tests -p 'test_audit.py' -v`. Expected missing primitive/key/audit APIs.
- [ ] Implement the listed APIs using Bouncy Castle generators/GCM/ECDsaSigner with HMacDSAKCalculator(SHA256Digest), and cryptography Argon2id/HKDF/AESGCM/ECDSA with deterministic signing. Parse DER/curve points through libraries; never implement scalar arithmetic. Return no plaintext until tag validation succeeds. Verify actual selected library support with a smoke test; unavailable deterministic signing/Argon2/HPKE blocks adoption.
- [ ] Add native-library HPKE known-answer open/seal checks, with fixture-only supplied ephemeral material if the API permits; otherwise verify published decrypt vectors and separate randomized cross-seal later. Create distinct role-key fixture pairs through library APIs, never derive them from recovery secret/password. Record them only in the public fixture corpus.
- [ ] Implement audit using the official OSV version-query API for every resolved Maven/PyPI package plus build/test dependencies; compare installed/resolved graph and hashes to lock first. A finding, response-count mismatch, unresolved dependency or feed outage exits nonzero. Send only public package coordinates/versions, never fixture/secret input. Record scan date, feed and limitations; no scan implies proof of library correctness.
- [ ] Run all RED commands GREEN and `python3 tools/protocol-harness/audit.py`; require complete tests, valid published expected values and a successful current audit before proceeding.
- [ ] Stage the exact Files paths and commit: `git commit -m "feat: qualify harness crypto primitives"`.

## Task 3: Context-bound symmetric envelopes and consumed salt guard

**Files:** Create `J/Context.java`, `J/Symmetric.java`, `J/NonceGuard.java`, `P/symmetric.py`. Test `JT/SymmetricTest.java`, `PT/test_symmetric.py`.

**Interfaces:** Consumes `ProtocolJson.context(List<Object>) -> byte[]`, `Primitives.hkdf(byte[],byte[],byte[],int) -> byte[]`, `gcmSeal(byte[],byte[],byte[],byte[]) -> byte[]`, `gcmOpen(byte[],byte[],byte[],byte[]) -> byte[]`. Produces `Context(ownerId:String,resourceKind:String,resourceId:String,keyEpoch:long,extraContext:List<Object>)`; equivalent Python dataclass. `Symmetric.seal(byte[] root,Context expected,String format,byte[] plaintext,NonceGuard guard) -> Map`, `sealFixture(byte[] root,Context expected,String format,byte[] plaintext,NonceGuard guard,byte[] salt,byte[] nonce) -> Map` (explicit test API), `open(byte[] root,Context expected,String format,Map envelope) -> byte[]`; Python names are `seal`, `seal_fixture`, `open_envelope`. `NonceGuard.reserve(byte[] root,Context expected,byte[] salt) -> void` and constructor `NonceGuard(long initialCount)`; Python equivalent. Guard indexes a nonlogged fingerprint of root+context+salt, not a caller-supplied mutable identity.

- [ ] Write `GivenUsedSaltContext_WhenSealedAgain_ThenRejected`:

```python
env = seal_fixture(root, ctx, "cerberus-content-v1", b"public fixture", guard, salt, nonce)
self.assertEqual(open_envelope(root, ctx, "cerberus-content-v1", env), b"public fixture")
with self.assertRaises(ProtocolError):
    seal_fixture(root, ctx, "cerberus-content-v1", b"edit", guard, salt, b'\x02' * 12)
self.assertEqual(env["keyEpoch"], 1)
self.assertEqual(len(unbase64(env["tag"], 16)), 16)
```

Cover all five content kinds; changed owner/resource/epoch/format/purpose/salt/
nonce/ciphertext/tag; empty plaintext/ciphertext; input bounds; no partial output;
normal API uses fresh secure salt+nonce; failed encryption consumes reservation;
lost-response retry reuses saved bytes; budget allows final permitted reservation
but rejects the next at `2^32`; changed root/epoch starts a distinct modeled budget.

- [ ] Run RED: `tools/protocol-harness/java/mvnw -f tools/protocol-harness/java/pom.xml -Dtest=SymmetricTest test`; `PYTHONPATH=tools/protocol-harness/python python -m unittest tests.test_symmetric -v`. Expected missing Context/Symmetric.
- [ ] Implement those signatures and exact spec §5.1 info/AAD arrays. Guard reservation is atomic and occurs before encryption; fixed material accepted only by `sealFixture`/`seal_fixture`. Enforce expected epoch and strict envelope fields at open; allow format extensions only inside the following named wrapper APIs.
- [ ] Run both commands GREEN; all mutations reject and saved retries are byte-identical.
- [ ] Stage the Files paths and commit: `git commit -m "feat: add context-bound vault envelopes"`.

## Task 4: Scoped password and recovery protection bundles

**Files:** Create `J/Protection.java`, `J/RecoveryBundle.java`, `P/protection.py`, `P/recovery_bundle.py`; extend data-only inputs and builders. Test `JT/ProtectionTest.java`, `PT/test_protection.py`.

**Interfaces:** Consumes `Symmetric.seal(byte[],Context,String,byte[],NonceGuard) -> Map`, `open(byte[],Context,String,Map) -> byte[]`, `Keys.publicJwk(byte[]) -> Map`, `Primitives.argon2(byte[],byte[]) -> byte[]`. Produces `Protection.validate(Map bundle,String scopeKind,String scopeId,Set<String> allowedRoots,Map unlockJwk) -> Map`, `wrap(String password,Map bundle,Context slot,NonceGuard guard) -> Map`, `unwrap(String password,Map wrapper,Context slot,Set<String> allowedRoots,Map unlockJwk) -> Map`; `RecoveryBundle.wrap(byte[] secret,Map accountBundle,byte[] proofPrivateDer,Context slot,long generation,NonceGuard guard) -> Map`, `unwrap(byte[] secret,Map wrapper,Context slot,long generation,Map recoveryJwk,Set<String> allowedRoots,Map unlockJwk) -> Map`. Allowed-root identifiers are `resourceKind + ":" + resourceId`. Python same snake_case. Each `wrapFixture`/`wrap_fixture` takes its normal wrap arguments followed by `byte[] keySalt,byte[] nonce`; password wrapping additionally takes `byte[] passwordSalt` last.

- [ ] Write `GivenProfileSlot_WhenAccountRootIncluded_ThenRejected` and `GivenChangedKdf_WhenUnwrapped_ThenNoDerivation`:

```python
with self.assertRaises(ProtocolError):
    validate(profile_with_account_root, "profile", profile_id, profile_allowed, profile_unlock_jwk)
with mock.patch("cerberus_protocol.protection.argon2") as derive:
    with self.assertRaises(ProtocolError): unwrap(password, huge_memory_wrapper, slot, allowed, unlock_jwk)
    derive.assert_not_called()
self.assertNotEqual(argon2("é".encode(), password_salt), argon2("e\u0301".encode(), password_salt))
```

Test exact UTF-8 spaces/case/composed-vs-decomposed/non-BMP and invalid surrogates;
wrong password/salt/KDF field/type/profile; huge work and aggregate bounds (Focus
1); sorted distinct roots, account/profile membership, missing/empty roots,
unrelated profile keys, unlock verifier mismatch/malformed PKCS#8 (Focus 2);
rewrap increments slot epoch but preserves permitted inner roots/content epochs;
recovery wrong secret/generation/fingerprint/scope; server public verifier cannot
decrypt; distinct per-generation proof keys and no Argon2 on raw recovery secret.

- [ ] Run RED: `tools/protocol-harness/java/mvnw -f tools/protocol-harness/java/pom.xml -Dtest=ProtectionTest test`; `PYTHONPATH=tools/protocol-harness/python python -m unittest tests.test_protection -v`. Expected missing Protection/RecoveryBundle.
- [ ] Implement the listed bundle validators and wrappers with spec §5.2/5.3 exact field sets, contexts and inner-key/public-key matching. Validate parameters and bounded wire structure before KDF; strict parse and scope checks after authenticated decrypt, before returning any usable key bundle.
- [ ] Run both commands GREEN, including an instrumented pre-KDF rejection assertion in Java; do not merely time a fast failure.
- [ ] Stage the Files paths and commit: `git commit -m "feat: add scoped protection wrappers"`.

## Task 5: Signed HPKE recipient envelopes and pinned client-key transitions

**Files:** Create `J/Recipient.java`, `J/ClientTrust.java`, `P/recipient.py`, `P/client_trust.py`. Test `JT/RecipientTest.java`, `PT/test_recipient.py`.

**Interfaces:** Consumes `Keys.publicJwk(byte[]) -> Map`, `thumbprint(Map) -> String`, `sign(byte[],byte[]) -> byte[64]`, `verify(Map,byte[],byte[]) -> boolean`, Task 3 `Context`, and qualified native HPKE APIs from Task 2. Produces `Recipient.wrap(byte[] resourceRoot,Context resource,String grantId,long grantRevision,String recipientIdentityId,Map recipientJwk,byte[] authorPrivateDer) -> Map`; `open(Map envelope,Context resource,String grantId,long grantRevision,String recipientIdentityId,byte[] recipientPrivateDer,ClientTrust trust) -> byte[]`. Python names are `wrap`, `open_recipient`. `ClientTrust(Map recipientJwk,Map authorJwk,long directoryRevision)` holds independently provisioned pins; `transition(String accountId,String role,Map newJwk,long revision,byte[] oldAuthorSignature) -> void`. Python equivalent.

- [ ] Write `GivenPinnedAuthorAndRecipient_WhenWrapped_ThenOnlyRecipientOpens`:

```python
env = wrap(root, ctx, grant_id, 1, recipient_id, recipient_jwk, author_private)
self.assertEqual(len(unbase64(env["enc"], 65)), 65)
self.assertEqual(len(unbase64(env["ciphertext"], 48)), 48)
self.assertEqual(open_recipient(env, ctx, grant_id, 1, recipient_id, recipient_private, trust), root)
with self.assertRaises(ProtocolError):
    open_recipient(env, ctx, grant_id, 2, recipient_id, recipient_private, trust)
```

Reject every changed info field, forged/unpinned/self-introduced author, wrong
recipient/key fingerprints/epochs, malformed points (Focus 2), corrupted enc/tag/
signature, wrong lengths and unknown fields. Spy on decapsulation to prove author
and expected binding validation precede it. Test old-author-signed transition,
new-key self-signature rejection, wrong role/account/JWK hash, nonincreasing
revision and separate recipient/author roles. Model fresh grant revision and
omission of revoked recipients without claiming erasure of old copied keys.

- [ ] Run RED: `tools/protocol-harness/java/mvnw -f tools/protocol-harness/java/pom.xml -Dtest=RecipientTest test`; `PYTHONPATH=tools/protocol-harness/python python -m unittest tests.test_recipient -v`. Expected missing Recipient/ClientTrust.
- [ ] Implement exact §6 info/signedBytes and transition arrays. Java invokes BC HPKE base seal/open with empty AAD and selected suite; Python invokes `Suite(KEM.P256,KDF.HKDF_SHA256,AEAD.AES_256_GCM)` and splits/joins its `enc || ciphertext` output at 65 bytes. Use no shared HPKE transcript code; validate APIs against the pinned release, not an assumed Java/C# parity.
- [ ] Run both commands GREEN and rerun each library's published HPKE known answers from Task 2.
- [ ] Stage the Files paths and commit: `git commit -m "feat: add signed recipient key wrapping"`.

## Task 6: Exact-body challenge proofs with purpose binding

**Files:** Create `J/Challenge.java`, `P/challenge.py`. Test `JT/ChallengeTest.java`, `PT/test_challenge.py`.

**Interfaces:** Consumes `ProtocolJson.parse(byte[],Set<String>,Set<String>,int) -> Map`, `context(List<Object>) -> byte[]`, `Keys.sign(byte[],byte[]) -> byte[64]`, `verify(Map,byte[],byte[]) -> boolean`, `Primitives.sha256(byte[]) -> byte[]`. Produces `Challenge.issue(Map expectedBinding,byte[] rawBody,long now) -> Map`; `bytes(Map challenge) -> byte[]`; `sign(Map challenge,byte[] scopedPrivateDer) -> byte[]`; `verify(Map challenge,Map expectedBinding,byte[] rawBody,Map registeredJwk,byte[] signature,long now) -> void`. Expected binding contains operation, identity/account/scope IDs, key epoch, protection revision and nullable generation. Python equivalent, with secure randomness in issue; `issueFixture(Map expectedBinding,byte[] rawBody,long now,String challengeId,byte[] nonce) -> Map` is the explicit test-only fixed-material variant.

- [ ] Write `GivenBoundBody_WhenByteOrderChanges_ThenProofRejected`:

```python
challenge = issue(binding, b'{"idempotencyKey":"fixture","expectedRevision":1}', 1700000000)
self.assertEqual(challenge["expiresAt"], 1700000060)
proof = sign(challenge, scoped_private)
verify(challenge, binding, original_body, registered_jwk, proof, 1700000059)
with self.assertRaises(ProtocolError): verify(challenge, binding, original_body, registered_jwk, proof, 1700000060)
with self.assertRaises(ProtocolError): verify(challenge, binding, reordered_body, registered_jwk, proof, 1700000059)
```

Provide original/reordered bytes exactly as opposite property orders. Mutate
every proof-array field; test wrong scope/purpose/key, account scope mismatch,
generation null rules, future issue, timestamp overflow, bad length/scalars,
duplicate fields/BOM/invalid UTF-8, body at/over bound (Focus 3). Challenge request
does not create access; only registered scoped/old-generation key verifies.

- [ ] Run RED: `tools/protocol-harness/java/mvnw -f tools/protocol-harness/java/pom.xml -Dtest=ChallengeTest test`; `PYTHONPATH=tools/protocol-harness/python python -m unittest tests.test_challenge -v`. Expected missing Challenge.
- [ ] Implement exact §7 proofBytes and raw SHA-256 body binding; strict parse the same uncompressed, BOM-free bytes without reserialization. Model proposed proof headers as separate inputs, not body fields. Challenge schema is strict; only `recover` has nonnull generation as specified.
- [ ] Run both commands GREEN; confirm deterministic signature bytes against native RFC 6979 checks.
- [ ] Stage the Files paths and commit: `git commit -m "feat: add purpose-bound challenge proofs"`.

## Task 7: Atomic local recovery, refresh and exact lost-response retry

**Files:** Create `J/RecoveryModel.java`, `P/recovery_model.py`; extend fixture builders. Test `JT/RecoveryModelTest.java`, `PT/test_recovery_model.py`.

**Interfaces:** Consumes `Challenge.verify(Map,Map,byte[],Map,byte[],long) -> void`, strict envelope schemas from Tasks 3–4 (not client plaintext unwrap), and `Keys.validatePublic(Map) -> Map`. Produces `RecoveryModel(Map initialPublicState,LongSupplier clock)` with `addChallenge(Map challenge) -> void`, `execute(String identityId,boolean freshAuth,byte[] rawBody,String challengeId,byte[] proof,boolean currentVaultAccess,boolean injectCommitFailure) -> Map nonsecretOutcome`, `snapshot() -> Map`. Python takes `Callable[[],int]` and corresponding methods. Public state contains account/scope IDs, registered public verifiers, current epoch/protection revision/recovery generation/revocation generation and opaque wrappers, never recovery secret or private key. Replacement body contains exactly operation, idempotencyKey (nonempty ASCII string bounded by the whole request), expectedRevision, passwordWrapper, recoveryWrapper, newRecoveryVerifier; expected epoch/generation derive from current state and validated replacement metadata. Outcomes contain exactly status, protectionRevision, generation, revocationGeneration; status is committed or the stored committed outcome on exact retry. Rejection throws ProtocolError; race test helpers turn that into status rejected for counting.

- [ ] Write `GivenConcurrentRecovery_WhenCommitted_ThenOneTransitionWins`:

```python
before = model.snapshot()
outcomes = run_two_synchronized_distinct_operations(model, first, second)
self.assertEqual(sum(item["status"] == "committed" for item in outcomes), 1)
self.assertEqual(model.snapshot()["protectionRevision"], before["protectionRevision"] + 1)
self.assertEqual(model.snapshot()["revocationGeneration"], before["revocationGeneration"] + 1)
self.assertEqual(exact_retry(model, first_committed_request), first_committed_outcome)
self.assertEqual(model.snapshot(), snapshot_after_first_commit)
```

Implement these test helpers per language using barriers and two threads, not
sequential calls mislabeled concurrency. Test consumed/unknown/expired challenges,
stale revision/generation, old verifier vs replacement verifier, changed identity/
body/idempotency key, no fresh auth, refresh without current vault access, refresh
invalidating old recovery, and injected failure rolling back all changes. Supply
the alternate valid ECDSA signature `(r,n-s)` using the library's P-256 order in
test support: identical operation still returns one stored result (Focus 3).
Verify the stored result contains no plaintext/secrets/private keys.

- [ ] Run RED: `tools/protocol-harness/java/mvnw -f tools/protocol-harness/java/pom.xml -Dtest=RecoveryModelTest test`; `PYTHONPATH=tools/protocol-harness/python python -m unittest tests.test_recovery_model -v`. Expected missing RecoveryModel.
- [ ] Implement a single locked atomic state transition: authenticate, check stored exact operation outcome, verify old purpose key/body, compare state, prepare complete replacements, consume challenge/generation, commit revisions/verifier/wrappers/result together. Copy tentative state so injected failure leaves no effects. Idempotency identity is authenticated identity + key + original body hash, never signature hash. Explicitly document this is not PostgreSQL or production permission verification.
- [ ] Run both commands GREEN, repeat barrier races 100 times per implementation, and require all snapshots/rollback assertions, not just winner count.
- [ ] Stage the Files paths and commit: `git commit -m "feat: model atomic recovery and retries"`.

## Task 8: Strict offline JWS, trusted time and lease-key rotation

**Files:** Create `J/Lease.java`, `J/OfflineClock.java`, `J/LeaseTrust.java`, `P/lease.py`, `P/offline_clock.py`, `P/lease_trust.py`. Test `JT/LeaseTest.java`, `JT/OfflineClockTest.java`, `PT/test_lease.py`, `PT/test_offline_clock.py`.

**Interfaces:** Consumes `ProtocolJson.parse(byte[],Set<String>,Set<String>,int) -> Map`, `base64(byte[]) -> String`, `unbase64(String,int) -> byte[]`, `Keys.sign(byte[],byte[]) -> byte[64]`, `verify(Map,byte[],byte[]) -> boolean`. Produces `Lease.sign(Map claims,byte[] leasePrivateDer) -> String`, `verify(String compact,Map expected,LeaseTrust trust,long effectiveNow) -> Map`, `claims(Map expected,long issuedAt,boolean renewalEnabled,Long durationSeconds) -> Map`; nullable duration only for disabled policy. `OfflineClock(LongSupplier wall,LongSupplier monotonic)` with `renew(Map freshlyVerifiedLease,boolean authenticatedOnline) -> void`, `effectiveNow() -> long`, `restart() -> void`; time suppliers use seconds. `LeaseTrust(String issuer,Map pinnedJwk,long revision)` with `transition(Map newJwk,long revision,byte[] oldLeaseSignature) -> void`, `lookup(String kid) -> Map`. Python equivalents. Cached-token verification never calls renew.

- [ ] Write `GivenCachedLease_WhenClockRestarts_ThenOnlineRenewalRequired`:

```python
clock.renew(fresh_enabled_lease, authenticated_online=True)
self.assertEqual(clock.effective_now(), 1700000000)
wall.advance(10); monotonic.advance(10)
self.assertEqual(clock.effective_now(), 1700000010)
clock.restart()
with self.assertRaises(ProtocolError): clock.effective_now()
verify(cached_compact, expected, trust, 1700000010)  # does not renew clock
with self.assertRaises(ProtocolError): clock.effective_now()
with self.assertRaises(ProtocolError):
    claims(expected, 9007199254740991, True, 86400)
with self.assertRaises(ProtocolError):
    verify(enabled_without_exp_compact, expected, trust, 1700000010)
with self.assertRaises(ProtocolError):
    verify(disabled_with_exp_compact, expected, trust, 1700000010)
```

Test enabled/disabled policy field matrices, default 86400 and other positive
representable durations without maximum, overflow and `2^53` (Focus 4); original
JWS byte verification including reordered properties; every expected claim;
header alg/type/kid allowlist, none/HS256/token-supplied URLs/keys/crit, duplicates,
unknown fields, invalid compact/base64/signatures; grant sorting/duplicates/scope;
nbf=iat, future, exp exclusive. Test wall and monotonic regression independently,
high-water preservation on re-import, only trusted online renewal restoring use,
disabled policy without fabricated periodic expiry and reconnect requiring fresh
authorization. Test separate lease-key domain, trusted old-key transition, hash/
issuer/revision rollback and unknown key rejection; retain old verification keys
for outstanding/no-expiry leases, with no immediate-revocation claim.

- [ ] Run RED: `tools/protocol-harness/java/mvnw -f tools/protocol-harness/java/pom.xml -Dtest=LeaseTest,OfflineClockTest test`; `PYTHONPATH=tools/protocol-harness/python python -m unittest tests.test_lease tests.test_offline_clock -v`. Expected missing Lease/OfflineClock/LeaseTrust.
- [ ] Implement exact §8 claim/header sets and original signing input. Calculate checked time sums before serialization; anchor uses freshly verified signed `iat`, receipt monotonic time and remembered highest effective time. Separate clock lifecycle from token parsing; disabled-expiry policy does not silently call the enabled-clock path. Keep client and lease trust stores/domain labels separate.
- [ ] Run both commands GREEN and native signature known-answer tests.
- [ ] Stage the Files paths and commit: `git commit -m "feat: validate offline leases and time"`.

## Task 9: Fail-closed process interface and executed interoperability corpus

**Files:** Create `J/Main.java`, `J/FixtureCases.java`, `P/__main__.py`, `P/fixture_cases.py`, `tools/protocol-harness/verify.py`, `tools/protocol-harness/tests/test_verify.py`, `tools/protocol-harness/fixtures/case-manifest.json`; modify `java/pom.xml`, README. Test `JT/DiagnosticsTest.java`, `PT/test_diagnostics.py`. Generate `docs/security/interoperability-vectors.json` only at the final successful step.

**Interfaces:** Consumes Tasks 1–8 APIs through their listed Interfaces, including the explicitly test-labeled fixed-material wrappers. Both CLIs support `produce <input-file> <output-file>`, `consume <cases-file> <results-file>`, `self-test <results-file>`; arguments designate public fixtures only. `FixtureCases.produce(Map inputs) -> List<Map>`, `consume(List<Map> cases) -> List<Map>`, `selfTest(Map knownAnswers) -> List<Map>` with Python `self_test`. Orchestrator `verify(java_command:list[str],python_command:list[str],output:Path|None,check:Path|None) -> dict` runs complete native suites and both directions; define `HarnessFailure(code:str)` in `verify.py` for orchestrator/benchmark failures, with codes invalid_protocol/unsupported_dependency only, no child exception text. Command `python tools/protocol-harness/verify.py --output PATH` generates; `--check PATH` replays and checks an existing corpus without pretending fresh randomized outputs must match.

- [ ] Write `GivenMissingOrIncompleteProducer_WhenVerified_ThenNoEvidenceWritten`:

```python
for failure in (missing_java, missing_python, empty_tests, skipped_test,
                bad_child_json, wrong_case_ids, stale_digest, failed_consumer):
    with self.assertRaises(HarnessFailure): verify(**failure, output=target, check=None)
    self.assertEqual(target.read_bytes(), previous_valid_bytes)
```

Define each failure with stub subprocess replies in orchestrator tests, not
silently skipping absent Java/Python. Native diagnostics tests force exceptions
containing marker secrets; stdout/stderr contain only stable codes and case IDs,
not marker values, exception text or partial plaintext. Test child timeouts,
nonzero exits, missing/duplicate/extra result IDs, wrong implementation version,
hash mismatch, missing known answers and invalid schema/classification (Focus 5).

- [ ] Run RED: `python3 -m unittest discover -s tools/protocol-harness/tests -p 'test_verify.py' -v`; run Java DiagnosticsTest and Python tests.test_diagnostics with the earlier runner pattern. Expected absent orchestrator/CLI behavior, with discovered tests.
- [ ] Implement bounded file/process handling and independently authored fixture case construction. Each case carries ID, kind, expected context, public inputs/output, producer/version and expected success/rejection. Manifest enumerates all positive families and each mutation from Tasks 1–8; neither side chooses the verifier's expected result. Log only result codes/IDs/digests; public secret outputs go solely to labeled fixture files with explicit fixture-mode commands.
- [ ] Implement real bidirectional verification: Java-produced content/password/recovery/HPKE/proofs/JWS consumed by Python and reverse; compare deterministic encoding/KDF/HKDF/signatures; cross-decrypt actual randomized outputs; verify known answers first. Run each producer's mutations against both consumers and require the exact case-ID manifest, including model/time scenarios independently constructed in each implementation. Native suite reports must be nonempty, no skips, zero failures/errors.
- [ ] Run all native suites and orchestrator unit tests GREEN, then `python tools/protocol-harness/verify.py --output docs/security/interoperability-vectors.json`. Write via temporary file and atomic replacement only after all checks succeed. Include schema version `1`, classification `public-test-fixtures`, actual UTC execution time, implementation versions, source provenance, exact expected bytes/digests and real consumer results; do not fabricate timestamps or attestations.
- [ ] Run `python tools/protocol-harness/verify.py --check docs/security/interoperability-vectors.json`; require both fresh suites and corpus replay to pass, preserving actual randomized producer outputs. Stage exact source/test/manifest/vector paths and commit: `git commit -m "test: prove protocol interoperability"`.

## Task 10: Isolated password-derivation measurements

**Files:** Create `tools/protocol-harness/benchmark.py`, `tools/protocol-harness/tests/test_benchmark.py`, `docs/security/protocol-harness-benchmarks.json`; extend `J/Main.java`, `P/__main__.py` with benchmark child command. Modify `tools/protocol-harness/README.md`.

**Interfaces:** Consumes `Primitives.argon2(byte[],byte[]) -> byte[]`, Python `argon2(bytes,bytes) -> bytes`, and Task 9 CLIs. Both CLIs support `benchmark <samples> <results-file>` using fixed public password/salt/profile, one warm-up and default 10 measured derivations, no derived key output. `benchmark.run(java_command:list[str],python_command:list[str],samples:int,output:Path) -> dict` runs each implementation in its own fresh process.

- [ ] Write `GivenBenchmarkChildFailure_WhenMeasured_ThenNoSuccessArtifact`:

```python
with self.assertRaises(HarnessFailure): run(java_fails, python_ok, 10, target)
self.assertEqual(target.read_bytes(), previous_valid_bytes)
self.assertEqual(success["java"]["profile"]["memoryKiB"], 65536)
self.assertEqual(success["python"]["samples"], 10)
self.assertTrue(all(t > 0 for t in success["python"]["elapsedMilliseconds"]))
```

Test zero/negative samples, missing runtime, duplicate/short samples, bogus RSS/
units, missing environment/library versions and secret marker redaction (Focus 5).

- [ ] Run RED: `python3 -m unittest discover -s tools/protocol-harness/tests -p 'test_benchmark.py' -v`. Expected absent benchmark behavior, not fabricated metric assertions.
- [ ] Implement isolated child measurements with warm-up, monotonic elapsed samples, min/median/p95/max and OS/CPU architecture/runtime/library/profile metadata. On Linux use process peak RSS measured by the orchestrator via wait4, report bytes and exact measurement method; it includes runtime/JIT baseline, not isolated Argon2 allocation. An unsupported platform fails with a stated measurement limitation, never invents RSS. No latency SLO or mobile compatibility claim.
- [ ] Run the RED command GREEN, both native suites, and `python tools/protocol-harness/benchmark.py --samples 10 --output docs/security/protocol-harness-benchmarks.json`; review actual measurements and classification.
- [ ] Stage the exact Files paths and commit: `git commit -m "test: measure isolated password derivation"`.

## Task 11: CI integration and honest security-review handoff

**Files:** Create `tools/protocol-harness/tests/test_delivery.py`; modify `tools/protocol-harness/README.md`, `.github/workflows/tests.yml`, `docs/security/protocol-review.md`. Do not modify approval manifest, endpoint inventory or product requirements.

**Interfaces:** Consumes Task 9 `verify(java_command:list[str],python_command:list[str],output:Path|None,check:Path|None) -> dict` and `--check` CLI, Task 2 `audit(lock:Path) -> dict`, Task 10 benchmark artifact. Produces CI execution and review instructions; existing `scripts/verify_protocol.py` remains authoritative and independent of harness success.

- [ ] Write `GivenGreenHarnessAndPendingReview_WhenGateRuns_ThenDependentWorkBlocked`:

```python
result = subprocess.run([sys.executable, "scripts/verify_protocol.py"], capture_output=True, text=True)
self.assertEqual(result.returncode, 1)
self.assertIn("Protocol security and client review is pending", result.stdout + result.stderr)
self.assertIn("--check docs/security/interoperability-vectors.json", workflow)
self.assertIn("Run complete unfiltered suite", workflow)
```

Run existing gate tests for missing/stale review. Delivery tests inspect protocol
gate condition and production test/coverage job are unchanged; inspect project
references and Docker inclusion remain isolated. Inspect review instructions
never represent owner approval, benchmarks or cross-language agreement as an
independent security assessment (Focus 5).

- [ ] Run RED: `python3 -m unittest discover -s tools/protocol-harness/tests -p 'test_delivery.py' -v`. Expected absent harness CI integration, with discovered tests and pending gate correctly blocked.
- [ ] Integrate JDK 25/Python 3.14 setup, locked dependency installation, graph/hash audit, complete native suites and checked-corpus replay into the existing required `test` job without changing its name or gate condition. Pin new actions to verified release commit SHAs. CI must not regenerate committed evidence to hide drift or run hardware benchmarks as performance acceptance. Keep production .NET coverage and Docker job unchanged.
- [ ] Consolidate the actually implemented contract into `docs/security/protocol-review.md`, including changes from the earlier proposal (GUID registration binding, proof headers/raw hash, HPKE author signature, recovery key generation, no-expiry policy, restart tradeoff). Reference corpus, source/lock hashes, commands and honest model/platform limits; leave manifest pending. Document future real-client tests and independent review obligations. No manufactured reviewer identity, date, outcome or approval hash.
- [ ] Run the RED command GREEN and complete acceptance commands below; record actual outputs/counts before claiming harness completion.
- [ ] Stage only Files paths and commit: `git commit -m "chore: integrate protocol review evidence"`.
- [ ] Seek whole-branch code review using the applicable review skill; it is not the independent protocol security review. Resolve findings and rerun acceptance. Use normal repository PR/CI workflow, never admin-merge or waive pending approval.

## Full acceptance and stop conditions

Run from the isolated worktree with locked dependencies and the isolated Python
environment; do not run concurrent .NET builds against the same outputs:

```bash
tools/protocol-harness/java/mvnw -f tools/protocol-harness/java/pom.xml clean test
PYTHONPATH=tools/protocol-harness/python python -m unittest discover -s tools/protocol-harness/python/tests -t tools/protocol-harness/python -v
python3 -m unittest discover -s tools/protocol-harness/tests -v
python tools/protocol-harness/audit.py
python tools/protocol-harness/verify.py --check docs/security/interoperability-vectors.json
python3 -m unittest discover -s scripts -p 'test_*.py' -v
python3 scripts/verify_specs.py
python3 scripts/vulnerabilities.py
dotnet test src/ArturRios.Cerberus.sln --configuration Release --logger trx
python3 scripts/openapi.py
git diff --check
python3 scripts/verify_protocol.py
```

All commands except the final independent-review gate must pass, with actual test
counts and no skipped/missing checks. The final checker must still report
`BLOCKED: Protocol security and client review is pending.` and exit `1` while the
manifest is pending. This expected block prevents dependent business work/release;
it is not a harness test failure and must not be suppressed in release/UC CI.

Stop implementation if a primitive/dependency/vector cannot be qualified, audit
fails, protocol composition conflicts with the approved design, or results reveal
a security-contract change. Report evidence and propose the narrow correction;
never substitute homemade crypto, weaker parameters or invented success. Leave
valid previous artifacts intact after failed generation. No business backlog item
is completed by this harness alone.

## Self-review and traceability

- Spec §§1–3: isolation, threat/key roles and library qualification in Tasks 1–2; no security certification claims in Task 11.
- Spec §4: Task 1 strict encodings/bounds and recursive validators in owning contract tasks.
- Spec §5: Tasks 3–4 envelope/protection/recovery bytes; proposed registration owner-ID behavior documented in Task 11, not implemented as a product route.
- Spec §6: Task 5 HPKE/authentication/pinning/revision transitions.
- Spec §7: Tasks 6–7 proofs, exact body binding, concurrency/rollback/refresh/retries.
- Spec §8: Task 8 strict leases, clock lifecycle, policy and separate lease-key rotation.
- Spec §§9–11: Tasks 9–11 real vectors, fail-closed diagnostics, benchmarks, CI and honest handoff.
- All five Review Focus items have owning test cases. Java/Python signatures use the same parameter order, wire fields and byte layout; their implementations remain independent.
- Each task has a RED command, explicit assertions/mutations, implementation interfaces, GREEN command and focused commit. The plan contains decisions/tests, not copied implementation bodies.
- No harness code, installed dependency, interoperability result, benchmark or independent approval exists at this plan-review stage.

## Primary dependency references

Checked 2026-10-07; availability is not vulnerability or cryptographic approval.
Task 2 still qualifies the actual pinned artifacts before adoption.

- [Bouncy Castle Java downloads](https://www.bouncycastle.org/download/bouncy-castle-java/) and [Maven metadata](https://repo.maven.apache.org/maven2/org/bouncycastle/bcprov-jdk18on/maven-metadata.xml): provider 1.86.
- [Apache Maven downloads](https://maven.apache.org/download.cgi): stable Maven 3.10.0; do not use Maven 4 preview.
- [JUnit guide](https://docs.junit.org/6.1.3/overview.html) and [metadata](https://repo.maven.apache.org/maven2/org/junit/jupiter/junit-jupiter/maven-metadata.xml): 6.1.3.
- [Jackson Core metadata](https://repo.maven.apache.org/maven2/com/fasterxml/jackson/core/jackson-core/maven-metadata.xml): stable 2.22.3; streaming parser only, no databind dependency required.
- [Python cryptography HPKE](https://cryptography.io/en/stable/hazmat/primitives/hpke/): 50.0.2 documented independent API; verify runtime support through executed primitive tests.
- [OSV API](https://google.github.io/osv.dev/api/): public package/version vulnerability queries and fail-closed response validation.
