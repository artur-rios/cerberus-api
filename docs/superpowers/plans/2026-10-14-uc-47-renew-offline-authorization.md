# UC47 Renew offline authorization implementation plan

> Execute inline with test-driven development and one fresh independent reviewer.

**Goal:** Issue signed client-verifiable offline leases for a current permitted owned profile after online identity and vault-access revalidation.

**Architecture:** Strict POST input (profileId, expectedProfileRevision), command handler and transactional metadata store. Narrow account-wide or matching own-profile sessions to the requested profile. Bind ES256 compact JWS to current profile key epoch/revision, account policy/generation and active associated foreign collection grant revisions. Use separately provisioned issuer/signing configuration and externally established client public-key trust. No process-generated production key or cached-token renewal.

**Spec:** UC47 all flows; FR-SY04–07; protocol-review.md offline JWS and clock rules; native Python/Java lease verifiers.

## Constraints and design rulings

- Issue48, branch feature/uc-47-renew-offline-authorization from clean develop491ad5d, PR develop. Prior explicit unattended authorization remains in force. UC43 stays blocked on Heimdall178.
- Endpoint POST /api/offline-authorizations uses strict required JSON, no query, one vault handle and native JSON transport. Current own profile only; account-wide handle may narrow to a profile, profile handle must match. Identity dependency middleware revalidates the human identity.
- Native header exactly alg=ES256, typ=cerberus-offline-v1+jwt, kid=public JWK thumbprint. Claims include iss/aud/sub/accountId/scopeKind/scopeId/keyEpoch/protectionRevision/policyRevision/revocationGeneration/grantRevisions/renewalEnabled/iat/nbf/jti and conditional exp. Integer seconds, iat=nbf, sorted unique grant identities, exclusive enabled expiry, disabled omits exp. Output includes compact lease and explicit policy/time metadata.
- Lease issuer and private P-256 PEM are a separate optional configuration pair: absent disables issuance with503, partial/invalid configured pair fails startup. Existing API deployments retain availability until provisioned. Never print secret PEM or invent a production key. Client trust is provisioned independently; a lease never supplies its own trust anchor. Rotation retains outstanding verification trust, including no-expiry keys.
- Resolve/lock account ownership metadata in canonical ascending order, then session/profile and associated collection/grant/protection metadata. Fresh authority after every wait, DB statement time before signing, final recheck before returning. Preserve all persisted content and session state; no distributed Heimdall transaction claim.
- Client expiry/clock/reconnection/no-cache requirements are explicit contract obligations; server issuance alone does not certify a native platform.
- Fresh full unfiltered suite and >=90% production line coverage, native interop (including server-produced leases), OpenAPI/Docker, independent review and exact-head CI gate delivery.

### Task1: Lease claims and dedicated signing configuration

**Files:** Domain/Offline lease contracts and key helper; Shared/Offline signer; CerberusOptions/validator/configuration; Domain/Shared tests.

- [x] Add failing unit tests for native header/claims, default/custom/disabled duration, safe counters, sorted grants, key identity, signature and tampering.
- [x] Implement strict immutable snapshot/lease output and separate P-256 signer; isolate configuration failures as unavailable issuance. Validate optional configured pair, issuer normalization and private P-256 role.
- [x] Verify produced wire independently using native Python/Java pinned harness code and external expected bindings, not token-supplied trust.

### Task2: Current authority snapshot and strict HTTP pipeline

**Files:** Data/Offline lease store; Command/Offline renewal pipeline; OfflineAuthorizationsController, Startup; corresponding Command/Data/WebApi tests.

- [x] Write absent-route HTTP regression, handler context/error tests and real PostgreSQL current/stale/foreign/hidden/session/profile/grant metadata tests.
- [x] Implement metadata-only current profile/policy/grant snapshot under canonical locks with fresh post-wait revalidation and DB statement-time expiry. Expected profile revision409; hidden404; invalid access403; corruption/storage/signer503. Preserve cancellation and no-state-change semantics.
- [x] Wire strict required JSON/body middleware and transport guards; validate stored/signed output matches exact request and trusted context. Focused tests green.

### Task3: Races, contract and verification

**Files:** Data concurrency/HTTP/contract tests; offline policy/lease documentation, OpenAPI, foundation status, changelog and deployment configuration docs.

- [x] Cover owner/session/profile/policy/grant revocation and expiry after lock waits; signing/storage failures and no hidden disclosures; verify no-expiry and disconnected revocation limits.
- [x] Document exact signing/trust/configuration, scoped wire/error contract and compliant client clock/reconnection/no-cache behavior; regenerate OpenAPI.
- [x] Run locked restore/audit/zero-warning build, full unfiltered Release fresh coverage, helper/spec/native interoperability/OpenAPI and Docker checks. Resolve all Critical/Important independent whole-change review findings.

### Task4: Authorized delivery

**Files:** README backlog and delivery ledger.

- [ ] Update issue48 README/counts, commit/push/open PR, require exact-head branch-policy/test/docker CI, merge normally; close issue/projectDone, verify reviewed merge tree and clean synchronized develop before next UC.
