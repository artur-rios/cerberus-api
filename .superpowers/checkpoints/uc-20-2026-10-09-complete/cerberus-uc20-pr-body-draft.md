Owners can recoverably delete a currently visible record with `DELETE /api/records/{id}` and the current `expectedRevision`. Success returns exactly six trash metadata fields, retains unchanged opaque ciphertext for 720 hours, snapshots all stored direct typed links, detaches the record and advances affected active immediate owned parent structural metadata atomically. Selected access stays valid; current native RO and RW recipients cannot delete.

Current selection, complete active ancestry and exclusive expiry are checked again at the database statement. Ordered parent/content/association locks and rollback guards preserve winners across recipient edits, owner rotation and association/creation races. Lost-success retries return404 without extending retention or duplicating work. No schema/dependency changes.

Closes #21.

## Validation

- Fresh unfiltered six-family suite:3878 expected; substitute actual verified totals/coverage before publication.
- Observed task RED→GREEN:116 Data,74 Command,99 HTTP cases, using actual PostgreSQL/native Master/PerProfile/unlock and collection grant fixtures. Actual update/trash and rotation races, FK/parent/association lock behavior, expiry at every lock category, statement-time ancestry/lifecycle guards, rollback after partial writes, retry/deadline and targeted analyzed query plan.
- Native OSV153packages clean, corpus322/four directions,36 native helpers,23 Python helpers, locked restore/NuGet audit clean, OpenAPI generated/drift/shape checked,107FR/55UC/29BR specs, clean diff.
- Ordinary fresh whole-branch review: pending. Exact-head required CI and verified normal merge remain mandatory.

## Development exception and remaining release prerequisites

Independent protocol/security and actual client qualification remain deferred only for development under the user's explicit instruction. Existing release gates remain. UC51 restoration must revalidate retained associations and consume/reconcile the old typed entry; UC53 must provide typed expiry handling before release. The current executor retries an unavailable handler and never claims physical purge. Before any physical purge, repair resource-kind terminal handling and never clear a selected profile session to account-wide scope. Shared OpenAPI cleanup remains a before-release prerequisite.
