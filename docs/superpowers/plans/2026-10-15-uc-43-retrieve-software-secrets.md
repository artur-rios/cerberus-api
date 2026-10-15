# UC43 Retrieve software secrets implementation plan

> Use executing-plans with a fresh read-only whole-change reviewer. Persisted unattended batch authorization covers design, tests, PR, verified normal develop merge, issue/project transitions and owned-branch cleanup. No implementation subagents or routine human gates.

**Goal:** issue44 / UC43 main and AF01–04, FR-SW-03/04, BR10/15/19; activate the now-available Heimdall application authentication contract without exposing plaintext or human vault authority.

**Baseline:** clean synchronized develop bfd10c4a20169700670c3b875b7a2aed116b9aa4, tree74e4295db6d36a4435c2ceee0f16c1e512a57dec. UC08 PR112 merged/issue9ClosedDone; full11463 passed, fresh98.8line/89.6branch, exact3CIgreen; 48 hash-verified completion artifacts. Heimdall178closed/PR185merged bedd912cfaa89509221d089ba992499b5a7b587a; authoritative current consumer contract fetched to /tmp/cerberus-heimdall-app-auth-contract.md.

## Design and rulings

- Implement specified GET `/api/software-secrets` with required single-valued canonical `grantId`, `kind` (record|collection), `id` query fields and optional `collectionId` only for a member-record read. No body, alternate fields or human vault/proof headers. One typed target keeps disclosure bounded and avoids listing unrequested grants or targets.
- Each result requires an exact persisted software target and corresponding signed resource-root envelope. A direct record/collection read uses its own provisioned root. A record read naming `collectionId` requires that exact explicitly granted owned collection and current effective record membership (direct or folder descendants), returns its collection provisioning alongside the requested record ciphertext, and rechecks both lifecycles/epochs. This preserves existing collection-grant semantics without fabricating any independent record root; client-side collection content supplies any member-key material. No unrelated collection paths or grants are enumerated.
- New application audience `CERBERUS_AUTH_APPLICATION_AUDIENCE` required and distinct from existing person audience. Optional `CERBERUS_AUTH_VALIDATION_SECRET_PREVIOUS` supports configured signing rotation; validate syntax/separation without adding packages. Update loader, safe configuration diagnostics, isolated fixtures and provisioning docs.
- Application JWT: HS256 current/previous trusted key, configured issuer and exact application audience, zero skew, exactly one nonzero application/scope/credential ID and signed numeric exp/iat/nbf, tokenKind=application, no role/MFA/person-authority claims. Strict raw JSON rejects duplicate security fields. Human/service token validators reject any tokenKind so applications cannot enter person routes.
- Mark only the software read endpoint for application authentication; middleware validates signed application identity for this route and preserves existing human live validation elsewhere. Query handler uses original caller bearer with separate narrow application client, GET Heimdall `/api/applications/self`, for EVERY sensitive release; no positive identity cache and no fallback to person/scoped service credentials. Match all application/scope/credential/expiry fields to signed claims, explicit successful safe envelope/200 and strict bounded body. 401denial; incompatible/unavailable/timeout/redirect/rate failures503; caller cancellation propagates. No network call spans database locks.
- Data derives owning account from the exact grant, never a caller owner ID. Require same application identity, active nonterminal account/grant and current explicit target, including current exact collection-member path when requested. Lock account before collection/record and grant, recheck after waits and at final database-time read including token expiry. Validate safe revisions/epochs/structural metadata, complete record ancestry and lifecycle, native ciphertext format, independently read owner protection/author pin, stored public software recipient key, exact signed recipient binding (owner/kind/id/epoch/grant/revision/app/key fingerprints). Hidden/absent/mismatched authority404; required visible stored corruption503; no partial result.
- Return only public owner/target/grant identity and revision/epoch, requested native ciphertext, exact selected provisioning ciphertext/wrapped key and public verification/provisioning keys. No plaintext, usable keys, account envelope, session/challenge/protection wrappers or unrelated identifiers. Validate output coherence again in query handler. Every success/failure no-store, no conditional-cache validators.
- Identity live check and database authorization have separate linearization boundaries, as existing human flows; revocation after its check cannot recall an already authorized response. Every following request checks again. Token lifetime is checked against database time after waits, so an expired token cannot disclose through a delayed read.

## Gate1 flow mapping

| Flow | Implementation and proof |
|---|---|
| Main1–4 | Native signed application token + current live credential; exact active grant/target read with native signature and epoch validation; real hosted ciphertext/wrap response and no-store. |
| AF01 | Strict bounded GET transport, canonical IDs, exact fields/kind, no duplicated values/body/human headers;400 before store; invalid signatures/format in persisted required state503 rather than disclosing. |
| AF02 | Wrong app/grant, missing target, inactive account/grant, trash/terminal/hidden ancestry and missing provisioned root404; typed UUID isolation; no unrelated enumeration. |
| AF03 | Person/service/MFA/application-with-role tokens, bad issuer/audience/key/scope/credential, expiry/revocation and application/person route confusion401; forbidden human writes remain denied. |
| AF04 | Actual provider failures, malformed/mismatched/oversized live response, database outage/corrupt required metadata503; cancellation propagates, no partial data or mutation. |

## Task1 Application authentication and configuration

- [x] Missing application validator/contracts RED; implement strict current/previous signature, audience, raw duplicate/role/claim/lifetime checks; person tokenKind denial and rotation compatibility tests.
- [x] Missing live-client RED; implement separate narrow bounded `/api/applications/self` adapter, exact field/expiry binding, no caching, safe dependency/caller cancellation and representative HTTP fixture tests.
- [x] Configuration RED then required distinct audience and optional previous signing key loader/validation, update current fixture configuration without relaxing prior checks. No new service/package.

## Task2 Domain/query and PostgreSQL retrieval

- [x] RED strict typed input/output/query handling and provider/store error/data integrity tests; implement minimal output and safe status mapping.
- [x] RED real PostgreSQL exact direct record/collection grant retrieval using existing real P-256 software provisioning fixtures; implement locked current-state native binding read.
- [x] Exercise wrong app/grant/owner, revoked/terminal account/grant/targets, trash/ancestor cycles/missing ancestors, stale epochs/revisions/author/recipient/binding corruption, unprovisioned records, current collection-member projection and removed membership, typed UUID isolation and unrelated malformed state. Assert no mutation/ciphertext leakage.
- [x] Real locks crossing expiry, revocation/rotation/membership/closure winners, cancellation and persistence failure; final post-read authority/snapshot fault checks. No physical mutation by retrieval.

## Task3 HTTP, documentation and independent development review

- [x] Missing endpoint/auth RED then real hosted application read with current Heimdall fixture and actual software grant signatures. Prove live credential/app/scope denial on next release and no app writes/human-route impersonation.
- [x] All AFs through real transport, provider failure and no-store/no conditional caching; exact returned ciphertext/wrap and retained unrelated data.
- [x] Update API/auth/config/provisioning/foundation/changelog docs, generated OpenAPI with strict query/output requiredness; validate actual HTTP success JSON and negative required-field fixtures against committed contract.
- [x] Fresh read-only whole-change development review and resolve findings; release security/client qualification remains separately gated.

## Task4 Full verification and delivery

- [x] Locked restore, zero-warning Release build, direct/transitive/native audits, helper/spec, native322x4 corpus and both lease clients, OpenAPI and Docker; freeze source/tests, clear old coverage, full unfiltered Release with zero failures/skips and >=90% fresh production line coverage, report branch coverage.
- [ ] README issue44Done/M04 6of6 (total50Done6Todo after delivery), exact-head review/ownPR closes44, projectTesting after creation; required branch-policy/test/docker SUCCESS on exact head.
- [ ] Normal merge, ClosedDone, source/merged-tree equality, owned branch cleanup, hash-verified evidence archive, clean synchronized develop. Continue UC50–55 without stopping for routine approval.

**Local verification:** full unfiltered11710PASS/0FAIL/0SKIP; fresh98.8%line/89.5%branch; six TRX reports and745frozen source/test file hashes verified. Locked restore, zero-warning Release build, audits, native322x4 corpus, both native lease clients, Docker, helper23/harness36/spec/OpenAPI/actualJSON checks pass. Whole-change development review has no outstanding findings. Inherited shared native schema requiredness and independent release qualification remain documented. Exact-head PR/CI/merge delivery follows.
