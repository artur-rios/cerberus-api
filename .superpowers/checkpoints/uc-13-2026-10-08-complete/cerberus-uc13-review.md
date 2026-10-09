# UC13 ordinary final review

Reviewed `/tmp/cerberus-uc13-worktree` at `2069753cbf0029bdfb109bc0bc6f0ec080ca58fe..0647dc566c4a8cdaea54f15b0d07aa0c5659107d`, against the UC13 spec, implementation plan, ledger rulings, authoritative UC13/FR-PR-05/06 and BR-03/04/20/23. This was a read-only checkout review; no implementation, index, HEAD, branch, or test environment changes were made. Only this report was written outside the checkout.

## Strengths

- `src/Infrastructure/ArturRios.Cerberus.Data/Profiles/ProfileTrashStore.cs:23` follows the existing account → session → profile lock order. It checks current database time after the profile lock before revision disclosure, and repeats current account/session/visibility/expiry predicates in the final UPDATE at line 44. Real PostgreSQL tests cover expiry while waiting on all three locks, stale revision after a profile-lock wait, and expiry immediately before the write.
- The transaction retains ciphertext, wrappers, ownership and client edit time while advancing profile revision/sequence, creating a durable operation and typed membership, queuing the exact 720-hour deadline, and revoking all owned handles selected to that profile (`ProfileTrashStore.cs:45`, `:63`, `:70`). Account-wide and unrelated selections survive. Postwrite, entry and queue failure tests demonstrate rollback and successful retry.
- Edit/delete and delete/delete races have one winning transition. Hidden targets are checked before revisions. Current account/session validation and strict HTTP body/path/header/query checks prevent forged context or scope expansion.
- The new schema matches its migration and snapshot. Existing rotation includes retained trashed profiles and accepts the older signed wrapper revision. The retention executor demonstrably rejects an absent handler, so queued work is not falsely completed.
- Audited the completed unfiltered log: **1,845 passed, 0 failed, 0 skipped**, across all six suites; production coverage is **97.9% line / 89.6% branch**. Reviewed the added command, PostgreSQL and HTTP tests. `git diff --check` passed and the checkout remained clean. Full suites were not duplicated during review.

## Critical findings

None.

## Important findings

None.

## Minor findings

1. **The retry status documentation needs a valid-session qualification.** `docs/operations/profile-trash.md:15` promises that retrying a successful deletion returns 404, and line 7 similarly promises 404 for the losing edit. A deletion performed with a selected handle revokes that handle at `src/Infrastructure/ArturRios.Cerberus.Data/Profiles/ProfileTrashStore.cs:70`; a subsequent request using it is correctly rejected with 403 at line 30 before target visibility is evaluated. The same applies to a losing concurrent request using a target-selected handle. A client following the unconditional documented promise could misclassify the response. **Fix:** document 404 for a still-valid account-wide handle and 403 for the revoked selected handle; neither creates another operation or extends the deadline. Keep revocation and authorization precedence intact. Align the equivalent wording in the design document.

2. **Known OpenAPI required-field metadata gap.** `docs/contracts/openapi.json:958` does not mark the mandatory access header required; the request body at line 965 and `DeleteProfileCommand` at line 3368 likewise omit required metadata, as does the six-field output at line 3378. Generated clients can construct requests without the required handle/body/revision or treat guaranteed output fields as optional. Runtime validation is strict. **Fix:** address the generator metadata in the existing tracked before-release follow-up and regenerate the contract; this is the acknowledged global documentation limitation.

## Declined to judge

- **Independent security/protocol approval:** explicitly deferred by the owner for development only; this ordinary correctness review supplies no such approval and does not clear the release gate.
- **Real-client cryptographic interoperability and operability approval:** explicitly deferred by the owner; the reviewed server fixtures and opaque-byte preservation are not a real-client approval.
- **Selected-profile proof minting and cryptographic key isolation:** assigned to UC15; current selected-scope enforcement and permanent revocation of existing selected handles were reviewed here.
- **Preservation and deactivation of nonempty record/folder/collection associations:** those entities and links do not yet exist, and current creation rejects nonempty sets. The actual empty snapshot is accurate. The first association/resource/sharing implementations must extend the transaction and preservation tests before those flows release.
- **Trash listing, restoration, restore-then-delete membership cleanup, and explicit purge:** assigned to UC50–52. Future restoration must reconcile the unique current typed membership and queued operation, enforce the deadline, and never un-revoke old selected handles. This review verifies durable inputs for those workflows, not their unimplemented behavior.
- **Physical purge after the retention deadline:** assigned to the UC53 typed handler. Current due work persists and retries without being marked complete; physical cleanup is not implemented or approved by this change.
- **Cross-kind terminal-erasure collision repair and erasure-preserving restore reconciliation:** already recorded prerequisites before physical purge release. Existing global ResourceId-only tombstone semantics were not redesigned in UC13.
- **Synchronization/change-feed delivery of deletion metadata to offline clients:** assigned to UC48. Current revision/sequence advancement and active read/list exclusion were reviewed, but this branch does not implement a synchronization feed.

## Assessment

**Ready to merge: Yes, into develop after the required CI succeeds.** No blocking correctness, authorization, atomicity, migration, or plan-alignment defect was found in the reviewed range. Record the two minors and the explicit follow-on obligations; independent release approvals remain pending.
