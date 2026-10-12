# UC-35 SDD ledger

## Authorization and binding

- User authorization: continue the previously authorized unattended sequential
  use-case batch through design, TDD implementation, task review, final review,
  exact-head CI, normal squash merge, closure and branch cleanup.
- Issue: #36, UC-35 Grant collection access.
- Base: `e6ea84cebd166d897fa5613f3e2c3ab7171f213a`.
- Branch: `feature/uc-35-grant-collection-access`.
- Project item: `PVTI_lAHOAgOUtM4BmFmuzg_LyxY`, In Progress.

## Mandatory preflight audit

- PostgreSQL 18.6 reproduced a reciprocal deadlock when each grant transaction
  locked only its owner account before insert.
- The PostgreSQL diagnostic named the implicit recipient FK check as `FOR KEY
  SHARE` on `account`.
- One `ORDER BY public_id FOR UPDATE` over both account rows allowed both reciprocal
  inserts to commit; exactly two rows remained.
- Adopted invariant: both accounts ordered and locked before session, collection
  or grant. No recipient-account lock after a collection/grant.

## Design rulings

1. Request is exact five fields with required nullable `expectedRevision`: null is
   fresh revision 1; positive current revision is explicit revoked regrant.
2. Regrant preserves grant public identity and advances revision once; active
   duplicate grants conflict and UC-36 owns active permission changes.
3. Success returns grant identity/access plus direct record/folder visibility roots,
   never profiles, wrappers, pins or keys.
4. Recipient must be distinct, active, nonterminal and protected; grant creation
   creates no recipient profile association.
5. Caller envelope binding failures are 400; malformed required stored native
   evidence is 503; stale/duplicate/reserved state is 409.
6. Only the grant row mutates. Collection/member and both accounts' protection
   graphs remain byte-for-byte and metadata exact.

## Task status

- [ ] Task 1 — Domain and application contract
- [ ] Task 1 review
- [ ] Task 2 — PostgreSQL store and concurrency
- [ ] Task 2 review
- [ ] Task 3 — HTTP/docs/OpenAPI/full verification
- [ ] Task 3 review
- [ ] Whole-branch review
- [ ] Exact-head delivery and closeout

## Review findings and evidence

Record task commits, focused/full counts, review findings, corrections, rulings,
CI head/base and merge audit here as the work advances.

