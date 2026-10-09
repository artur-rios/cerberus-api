# Rulings I made

- Ruling: Task 11 source-text assertions conflict with TDD writing-good-tests reference — test executed verifier/gate behavior and parsed CI job obligations instead of string-presence change detectors — cost if wrong: weaker CI policy evidence until integration runs.

- Ruling: Primitive helpers and encoders will be static Java methods, matching Python module functions and plan examples, while stateful guards/trust/models remain instances — cost if wrong: method-call compatibility only, no wire change.

- Task 1 Ruling: ASCII control escapes use lowercase hexadecimal in C(array) — makes constrained bytes unambiguous across library defaults — cost if wrong: byte-level interoperability requires review of the encoding choice.

- Task 1 Ruling: create fixture builders in Task 2 with their data-only corpus instead of unused Task 1 scaffolding — cost if wrong: test-support timing only, no contract behavior change.

- Ruling: run Task 2 dependency qualification before primitive qualification — an actual failed audit is an explicit plan stop condition and must be discovered before adopting crypto — cost if wrong: order of qualification only; no test or acceptance criterion removed.

- Ruling: qualify Maven distribution dependencies from embedded pom.properties, with explicit byte-hash-pinned identities for the five metadata-free JARs — scanning only org.apache.maven:apache-maven would miss vulnerable bundled libraries — cost if wrong: those five coordinate identities require review; unknown/drifting artifacts still fail closed.

- Ruling: published RFC9180 vectors use nonempty AAD while Python public single-shot API correctly fixes AAD empty — qualify unchanged published cases using the library's native test-only _decrypt_with_aad entry point, exactly as pinned upstream tests do; protocol code still uses public Suite.encrypt/decrypt with empty AAD — cost if wrong: private test utility can change on upgrade, in which case qualification fails closed. No custom HPKE or modified expected ciphertext.

- Ruling: RFC7638's published thumbprint example is RSA — exercise its exact canonical bytes through qualified SHA256/base64 primitives, then test strict P256-only key thumbprinting separately — cost if wrong: RFC example does not alone qualify EC member selection; explicit P256 canonical-byte expectations and independent hash cover that boundary.

- Ruling: NonceGuard(initialCount) seeds only the first observed root/context/epoch budget; subsequently distinct domains start at zero — models an existing budget without assigning its history to newly rotated roots/epochs — cost if wrong: reference-model constructor semantics only, no wire change or distributed accounting claim.

- Ruling: add bounded deterministic protocol-object Wire.encode / wire.encode helpers to Task 4 — bundle plaintext needs object serialization, unlike the existing constrained C(array); sorted ASCII field names stabilize public fixture bytes without claiming general JCS or changing raw-body hashing — cost if wrong: ciphertext bytes for equivalent bundle property orders can differ, which receiving strict parsers already allow. Raw request parsers remain authoritative for original byte limits.

- Ruling: password unwrap core receives a package-local KDF collaborator and the public API supplies Primitives.argon2 — permits the required Java pre-KDF instrumentation without a global mutable hook, test-only production counter or new mock dependency — cost if wrong: internal callback seam requires review; no protocol caller chooses its derivation algorithm.

- Ruling: wrapping consumes a trusted locally provisioned bundle and checks structural/scope invariants; unwrapping always requires external allowed-root membership and the registered scoped unlock verifier — the approved wrap signature contains no authority inputs, so it cannot manufacture authorization from ciphertext contents — cost if wrong: provisioning remains a client obligation; receiver-side authorization checks must never be bypassed.

- Ruling: one root per resource-kind/GUID pair, regardless of epoch — the supplied membership identifiers omit epoch and strict increasing kind/GUID order forbids ambiguous duplicate resources — cost if wrong: a future multi-epoch key-ring format requires a versioned schema rather than accepting duplicates in v1.

- Ruling: require independently trusted accountId as the first ClientTrust constructor argument — spec section 6 requires wrong-account transition rejection, which the plan's three-argument constructor cannot enforce without an account authority input; no automatic account/key TOFU is added — cost if wrong: harness constructor callers (including Task 9 builders) must supply that trusted account, with no wire or cryptographic composition change.

- Ruling: Java recipient open core receives a package-local decapsulation collaborator; its public API supplies the fixed BC implementation — proves binding/author validation precedes decapsulation without global counters or extra mock dependencies, matching the Task 4 seam — cost if wrong: internal callback seam requires review; protocol callers cannot choose a suite or bypass the checks.

- Ruling: add strict bounded generic object parsing to the existing encoding helpers and keep schema-aware parse built on it — challenge issuance must parse an arbitrary owning-operation JSON body once, while the operation's own validator (Task 7 and future routes) still rejects unknown contract fields — cost if wrong: callers must never treat generic parsing as operation-schema authorization; original body bytes remain authoritative for hashing.

- Ruling: define the local public-state schema explicitly (identity/account/scope, epoch/revisions/generation, unlock/recovery public verifiers and opaque wrappers), with registry/consumption/results included in snapshot — plan omits exact initial/snapshot keys; this makes rollback observable without storing plaintext — cost if wrong: reference-model callers must adapt, no wire change.

- Ruling: both complete-wrapper transitions increment the account protection slot epoch, generation, protection revision and revocation generation — recovery replaces password protection and refresh also receives a complete fresh password/recovery set per plan; spec section 5 requires fresh protection epoch when rewrapping password material — cost if wrong: future product refresh with an unchanged password wrapper needs a separately specified transition; inner content roots remain opaque.

- Ruling: expected lease authority includes renewalEnabled alongside all static claims — otherwise a signed disabled-expiry lease could be imported under an enabled policy; claims(expected,enabled) requires equality — cost if wrong: callers must pass the trusted policy explicitly; no new wire field.

- Ruling: launch Java CLI directly with compiled classes and the two checksum-locked runtime JARs instead of adding a packaging plugin — native suite builds classes first and no new dependency graph is required — cost if wrong: standalone JAR packaging is not provided, documented verify command supplies classpath.

- Ruling: include regenerated interoperability corpus in Task 10 because new benchmark CLI code changes source hashes; actual producer/consumer runs regenerated and replayed the evidence — avoids accepting stale corpus or masking drift — cost if wrong: randomized public ciphertext diff is larger; all cross checks still required.

- Ruling: bound benchmark sample count to 1..1000, including both CLIs — prevents unbounded accidental measurement work while retaining the specified ten-sample evidence — cost if wrong: larger studies need explicit batching or a reviewed limit change.

- Final: Ruling: independent composition/side-channel/memory-erasure/nonce-assumption approval was declined by code reviewer — preserve pending independent security gate and submit real evidence; code review cannot certify it — cost if wrong: dependent business implementation remains blocked until genuine review.

- Final: Ruling: real Heimdall/authorization/PostgreSQL/distributed nonce guarantees were declined by code reviewer — these remain owning production-use-case obligations, explicitly outside reference models — cost if wrong: local model evidence must never be promoted as production correctness.

- Final: Ruling: durable clock continuity/restart persistence/mobile/key-storage guarantees were declined by code reviewer — retain explicit process-restart block and future real-client qualification; no platform claim — cost if wrong: offline availability remains intentionally limited.

- Final: Ruling: global key-role uniqueness across separately provisioned systems was declined by code reviewer — jointly available role pairs are checked; complete provisioning governance remains a future client obligation — cost if wrong: an external provisioner could reuse roles without a global harness registry.

- Ruling: add the independently reproduced malformed embedded-key matrix as shared data-only public corpus inputs, with eleven rejecting variants and two valid controls — whole-branch review exposed a real interoperability boundary; actual producers/consumers must execute it — cost if wrong: corpus count/hash bindings change, requiring genuine regeneration and replay; no protocol suite or product route changes.

# Deferred minors

- Final: minor (deferred): Java fixture serializer accepts all Number types and longValue-coerces them; no demonstrated strict-wire parser bypass, but future fixture edits could silently truncate.

- Final: minor (deferred): fresh replay reports harness version without observed runtime/library versions; benchmarks and lock record observations, but replay metadata could improve reproducibility.
