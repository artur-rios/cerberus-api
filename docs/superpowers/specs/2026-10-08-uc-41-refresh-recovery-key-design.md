# UC41 Refresh recovery key design

Issue42 implements UC41 main/AF01–05, FRKY11/12 and BR10/12/14. Fresh specs/permissions/testing/operations and native recovery_model.py read. Explicit batch authorization covers verified develop delivery; independent security/client approval remains deferred for development and release-gated.

## Contract and architecture

PUT `/api/vault/recovery` reuses the existing strict six-field RecoverVaultCommand and four-field RecoverVaultOutput with `operation:"refresh-recovery"`. POST remains explicitly recover-only. Share the tested transition/store/handler rather than duplicate atomic recovery logic. Both wrappers must move slot epoch+1, recovery generation+1, fresh salts/nonces/KDF salt; new independent recovery verifier differs from every old role. Preserve unlock/recipient/author pins, all content bytes/epochs/revisions. Native full-wrapper refresh requires updating both wrappers; password-wrapper-preserving behavior needs a separately reviewed protocol.

Require current/fresh provider-revalidated owner identity, one account-wide X-Cerberus-Vault-Access handle plus native refresh-recovery purpose proof from the OLD registered unlock key. Header context internal only. Challenge generation null for refresh, current recovery generation for recover. Original UTF8 bytes/digest remain immutable; strict schema/headers/identifiers/proof, no secrets/private keys/decryption/signing by API.

Extend RecoveryReplacement.IsValid to both native operations; validator/handler accept only those; each HTTP method pins its expected operation. Extend RecoveryRequest with optional AccessVerifier (default null, existing callers compatible). Handler hashes access only for refresh. Data locks account then current own session; verifies account-wide, policy/revocation, issued/expiry/renewal state using fresh statement time. Native old unlock proof binds current slot/revision/purpose/body and null generation. Conditional consumption rechecks identity/challenge/session expiry together at statement time. Replace wrappers/verifier, advance protection/slot/recovery/revocation, and durable outcome in one transaction. Old sessions/challenges/old recovery generation are invalid; new unlock and recovery work.

Reuse unique(accountId,idempotencyKey) outcome table; original digest includes operation, so cross-purpose reuse conflicts. Exact completed retries return historical nonsecret result before expired/consumed/current-state/session checks, following native reference; current fresh owner/provider still required, canonical handle/header shape still required by PUT. The original handle becomes stale after commit, but exact retry performs no new transition. New operations always require current valid account-wide access. Cost: historical retry returns old counters; GET latest state before acting.

## Supporting recovery read correction

System Requirements additional-steps table specifies GET `/api/vault/recovery-material` for UC40. Existing GET protection already exposes own opaque material without a session, but the named fresh route was omitted. Add the named route using the same validated query/projection, require fresh signed iat/current provider, no query/body, no session/consumption, no-store. Record this additive UC40 integration correction in PR/report; do not rewrite merged history.

## Flows and verification

| Flow | Required assertion |
| --- | --- |
| Main | Current account-wide access+old unlock proof refreshes full wrappers/key/counters atomically; preserves content/pins; old recovery rejected and new credential works. |
| AF01 | Wrong method purpose/schema/unsafe counters/private/unknown/duplicate fields/pins/salts/header/encoding400, unchanged state. |
| AF02 | Missing/hidden/closing/erased account/protection404, foreign handle/proof cannot disclose. |
| AF03 | Current provider/fresh iat401; missing handle401, malformed400, expired/revoked/profile/foreign/policy/session denial403; wrong old key/purpose/body proof401. |
| AF04 | Stale revisions/policy/counter overflow409; concurrent changes one winner; same key/body one transition; changed bytes conflict. |
| AF05 | Real database/provider outage503, true caller cancellation; post-consumption write failure full rollback and identical proof retry. |

Real PostgreSQL tests cover unchanged natural session/challenge/identity deadlines across account and session locks, queued lifecycle/policy, concurrent winners, rollback, historical retries and cross-operation digest binding. HTTP tests exercise refresh→old recovery rejected→new unlock/new recovery, strict boundary/AFs/current provider/freshness, exact retry and named recovery-material read/no consumption. Run full unfiltered suite0fail0skip>=90%line/reportbranch, helpers/spec/OpenAPI drift, one ordinary final review, required CI, merge/Done/archive.
