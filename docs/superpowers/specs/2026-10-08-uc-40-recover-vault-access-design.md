# UC40 Recover vault access design

Issue41 implements UC40 main/AF01–07, FR-KY-08/09/10 and BR10/12/13. Existing batch authorization covers verified develop merge; independent security/client approvals remain development-deferred and release-gated. Native recovery_model.py, its concurrent/retry tests and protocol-review.md are the binding reference for transition/replay behavior.

## Architecture and alternatives

Use a separate recovery store with account-first locking, the registered old recovery public key and a durable account/idempotency-key outcome table. A preexisting unlocked session would defeat lost-password recovery, so require current/fresh identity plus native recovery proof instead. Reusing password-change authorization would deadlock recovery; identifying retries by signature would break under ECDSA malleability. Bind retries to authenticated account, canonical idempotency key and SHA256 of exact original bytes.

The client retrieves own opaque material using GET protection, decrypts with its recovery secret locally, creates new password/recovery wrappers and an independent new recovery secret/signing pair, and displays/retains the new secret locally. The API never derives, decrypts, signs, stores private keys or returns a recovery secret/session. Unlock/recipient/author pins and content ciphertext/epochs/revision remain unchanged; recovery replaces its own proof verifier and both protection wrappers.

## Contract

POST `/api/vault/recovery` strict JSON fields exactly `operation`, `idempotencyKey`, `expectedRevision`, `passwordWrapper`, `recoveryWrapper`, `newRecoveryVerifier`. Operation `recover`; idempotencyKey nonzero canonical lowercase GUID; expectedRevision positive safe integer<=9007199254740991. Wrappers/JWK follow existing strict public-only contracts. Their slot epochs equal current+1; recovery generation equals current+1; fingerprint matches new verifier, distinct from old recovery and all retained key roles. Visible password/recovery key salts/nonces and password KDF salt must be fresh relative to current wrappers. Clients validate inner roots/key correspondence and AEAD locally.

Extend the fresh signed-iat POST challenges operation to `recover`, binding current account scope/slot/protection revision and nonnull current recovery generation. Submit single canonical challenge-ID and 64-byte P1363 proof headers, signed by the OLD registered recovery private key over the existing exact native challenge array. Preserve original strict uncompressed UTF8 bytes, no BOM/query/unknown/duplicate/case-mismatched members/private key fields. Fresh signed iat age[0,60) is checked at HTTP submission and again against statement time after the account lock; actual provider has no auth_time, so no stronger authentication-event claim. Existing current identity dependency revalidation remains mandatory, including replay.

Success200 returns exactly status `committed`, protectionRevision, generation and revocationGeneration. These are the original historical committed counters on retry, even if later operations advanced current protection. GET material obtains current state. No new handle, raw secret, wrapper or usable key is returned. A lost response is retried with the SAME idempotency key/body bytes under a fresh valid bearer; canonical headers may reuse the original consumed/expired challenge/proof. The signature representation is not the idempotency identity.

## Atomic state and replay

One explicit transaction locks the own account, verifies active/non-erased owner and fresh trusted signed iat, then checks unique(accountId,idempotencyKey). A matching original-body hash returns validated stored nonsecret outcome BEFORE checking current revision/generation or consumed/expired challenge. Different body under same key returns409 without mutation. Result records cascade with account deletion and contain only account FK, public idempotency key, digest and committed counters.

For a new key: load and validate current protection, safe counter increments and complete replacement; recheck expected revision/current generation, policy/revocation and native recovery proof/raw-body equality after locks. Consume one challenge at fresh statement_timestamp with exclusive expiry, atomically replace password/recovery wrappers/public recovery verifier, increment protection revision/slot epoch/recovery generation/account revocation generation and insert outcome. Save/commit once. Old sessions/challenges are stale. Any failure rolls back ALL writes, including consumption/result. Different simultaneous operations using one current generation allow exactly one commit; exact simultaneous retries may both return the same outcome but perform one transition.

## Flow mapping

| Flow | Behavior/test evidence |
| --- | --- |
| Main | Fresh current owner/old native recovery proof replaces full wrappers/verifier, increments counters, preserves content, returns nonsecret status; new generation usable and old recovery key rejected. |
| AF01 | Strict schema/headers/encoding/safe integer/point/pin/fingerprint/epoch/fresh-context validation400; no writes. |
| AF02 | Absent/closing/erased account or protection404; own account derived from actor, foreign proof cannot disclose/mutate. |
| AF03 | Missing/invalid/revoked identity401, missing/stale/future signed iat401, wrong purpose/key/body/expired proof401; no existing vault session required. |
| AF04 | Stale expected revision/policy/generation or overflow409; same idempotency key with different body409. |
| AF05 | Provider/PostgreSQL outage503, caller cancellation, injected post-consumption save failure full rollback and identical proof retry. |
| AF06 | Real concurrent different-key recovery one winner/one consumed-generation conflict; identical-key concurrent retries one transition/outcome. |
| AF07 | Durable exact digest/key/actor replay after expiry/malleated canonical proof/restart returns stored status before consumed checks; no second transition/key returned; changed body/actor/auth fails. |

Verify unit contracts/handlers, real PostgreSQL concurrency/lock-wait expiry/freshness/lifecycle/rollback/cascade and real HTTP full init/recovery/old-key rejection/new-key recovery/retry flows. Full unfiltered zero-failure/skip suite,>=90% production line/report branch, helper/spec/OpenAPI checks, one ordinary final review and required CI before merge.
