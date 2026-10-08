# UC39 Change vault protection design

Issue40 implements FR-KY-06/07, BR10/12 and UC39 main flow/all AF01–06. Existing unattended authorization covers design/implementation/review/verified develop merge. Independent security/client approvals remain pending and development-deferred; release gate unchanged.

## Scope and approach

Use the existing owner/account protection, native old-unlock-key proof and PostgreSQL account lock. Require current account-wide vault access as well as current identity. Replace the complete password/recovery protection pair atomically. Support `rewrap` (password protection-slot epoch changes, inner content roots/epochs remain unchanged) and `rotate-content` (the current complete account content inventory is replaced in the same transaction). Separate statements/partial wrapper PATCHes would admit inconsistent recovery; a new generic encrypted inventory duplicating domain content would create synchronization hazards. Keep the authoritative existing account row as the inventory source.

The current domain has account ciphertext and no profile/content/grant entities. Thus its complete rotation inventory is exactly the own account envelope and zero recipient grants; extra/foreign/missing replacements reject. Profile protection and expanded content/recipient inventories must join this invariant when their subsequent creation/sharing use cases introduce those entities. This API changes the master vault wrapper, not a nonexistent selected profile. No profile/grant implementation is invented here. Client-only inner bundle/root correspondence is validated locally using the established harness contract; the API never decrypts.

## Wire contract

PUT `/api/vault/protection` with strict JSON fields `accountId`, `expectedProtectionRevision`, `expectedAccountRevision`, `mode`, `material`, `contentReplacements`. Mode exactly `rewrap` or `rotate-content`. Replacement material uses UC38's six-field strict public-only contract. Each content replacement has exactly `resourceKind`, `resourceId`, `expectedRevision`, `envelope`; resource kind currently account. Rewrap requires an empty array; rotation requires exactly the own account entry, matching expected account revision and a valid content envelope whose content epoch is the current content epoch+1. Envelope bytes are client ciphertext only.

Both protection wrappers must use current protection-slot epoch+1. Keep current recovery generation and all four registered public keys unchanged; password changes cannot silently change recovery identity or pinned recipient/author keys. Both wrappers require new keySalt/nonce, and password KDF salt must differ from the current one. Fresh ciphertext/context validity and preservation/replacement of inner roots remain client obligations. Protection slot epoch differs from account content epoch: rewrap never changes account content/revision; content rotation increments account content epoch and account revision atomically with the protection revision. Positive counters are safe JSON integers at most9007199254740991, overflow rejects409.

Single headers `X-Cerberus-Vault-Access`, `X-Cerberus-Challenge-Id`, `X-Cerberus-Proof`. No caller actor/handle verifier/raw secrets/private JWK fields in JSON. The original bytes are captured by existing bounded strict UTF8 middleware; proof binds their SHA256 digest. No query, compressed body, BOM, duplicate/unknown/case-mismatched fields or unsupported encoding.

Extend POST `/api/vault/challenges` operation to `change-protection` as well as existing `unlock-account`. Same signed iat freshness policy, actor/account scope/current slot epoch/protection revision, generation:null, 60-second exclusive expiry. Challenge creation does not authorize a mutation or issue a session; PUT requires the live account-wide handle and proof signed by the old registered unlock key. Unlock rejects change-purpose challenges and change rejects unlock-purpose challenges.

Success200 returns only accountId, protectionRevision, keyEpoch (protection slot), recoveryGeneration, accountRevision. Existing sessions/challenges become stale by incrementing account revocation generation. No new handle is issued; obtain a fresh unlock after the change. Copied plaintext/old keys cannot be remotely destroyed. Lost response: GET current protection, compare intended wrappers/revision, and reload before retry; exact repeated expected revision returns409 and never replaces the winner.

## Persistence and races

One explicit transaction locks the own account first, then the presented own session. Recheck active/non-erased owner, policy/revocation/session scope, future issuance and exclusive expiry with statement_timestamp after locks. Match both expected revisions, complete replacement material, key pinning, fresh visible salts/nonces and inventory. Verify native purpose-bound proof against the current old unlock verifier. Consume exactly one challenge using conditional SQL which rechecks challenge AND session expiry at statement_timestamp. Write both wrappers/counters, optional complete content replacements and account revocation generation; SaveChanges/commit once. All failure paths roll back challenge consumption and every write; concurrent changes yield one complete winner. No provider call or server decrypt/sign/KDF is added.

## Flow verification

| Flow | Behavior and evidence |
| --- | --- |
| Main | Real native proof + current handle rewrap/complete rotation; new confirmation, old sessions invalid, preserved or atomically replaced content. |
| AF01 | Strict input/material/headers/encoding/integer/salt/key/inventory rejection400, no writes/consumption. |
| AF02 | Absent/foreign/closing/erased target or protection404, no hidden material. |
| AF03 | Current identity failure401/provider503; missing/invalid/stale/profile/revoked/expired handle403; wrong/consumed/expired/purpose/body proof401. |
| AF04 | Account/protection revisions or challenge policy/generation changed409; overflow409; concurrent changes one winner. |
| AF05 | Real PostgreSQL outage/wrapped timeout503, cancellation, injected post-consumption save failure rolls back all, same valid request can retry. |
| AF06 | Missing/duplicate/foreign/extra account content replacement or wrong content epoch rejects400 before any write; zero existing recipient grants, later grants must extend inventory. |

Unit tests pin transition/validation/handler projection. PostgreSQL tests pin atomic replacement, account/session lock wait expiry and lifecycle/policy changes, concurrency and rollback. HTTP tests exercise init/unlock/change/re-unlock/read, main modes and all AFs. Full unfiltered suite zero failure/skips, production line>=90/report branch, helpers/spec/OpenAPI drift, one final ordinary review, all3 required CI before merge.
