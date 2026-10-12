# UC-36 SDD ledger

## Authorization and binding

- User authorization: continue the previously authorized unattended sequential
  use-case batch through design, TDD implementation, task review, final review,
  exact-head CI, normal squash merge, closure and branch cleanup.
- Issue: #37, UC-36 Change collection access.
- Base: `e9f58936eab3723ad82dfe06f44c7667585bb284`.
- Branch: `feature/uc-36-change-collection-access`.
- Project item: `PVTI_lAHOAgOUtM4BmFmuzg_Ly0U`, In Progress.

## Design rulings

1. PUT path identifies collection and grant; body is exact three required fields:
   positive safe `expectedRevision`, changed `access`, and next-revision
   `keyEnvelope`.
2. The replacement envelope is required because native authority binds the grant
   revision, which advances exactly once.
3. Only an exact active nonterminal grant changes. Revoked/hidden/wrong-collection
   grants are 404; stale, same-access, exhausted and concurrent states are 409.
4. Owner and recipient accounts are locked together in public-UUID order before
   session, selected profile, collection and exact grant.
5. The grant CAS and recipient account revocation-generation increment are one
   atomic authorization write. Existing recipient handles become stale; after a
   fresh unlock, read-write permits writes and read-only denies them.
6. Only grant access/envelope/revision/stamp and recipient generation mutate.
   Account revision/policy/protection, collection/member graphs and sessions remain
   otherwise exact.
7. Success returns the same exact six nonsecret grant/projection fields as UC-35;
   no recipient identity, generation, profile, envelope, pin or key is returned.

## Task status

- [ ] Task 1 - Domain and application contract
- [ ] Task 1 review
- [ ] Task 2 - PostgreSQL permission/generation writer
- [ ] Task 2 review
- [ ] Task 3 - HTTP/docs/OpenAPI/full verification
- [ ] Task 3 review
- [ ] Whole-branch review
- [ ] Exact-head delivery and closeout

## Review findings and evidence

Record task commits, behavioral red/green evidence, focused/full counts, review
findings, corrections, exact source hashes, CI head/base and merge audit here as
the work advances.
