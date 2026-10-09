A currently visible owned folder can now move to another visible owned folder or to the root through `PUT /api/folders/{id}/parent`. The body requires `expectedRevision` and an explicit nullable `parentFolderId`; success returns exactly `folderId`, `parentFolderId`, `revision`, and `serverSequence`. Selected access rejects moves that add an effective profile or collection to any active descendant folder or contained record. Native recipients cannot move an owner's folder.

The transaction captures current subtree membership, effective routes, and required native bindings, then rechecks them after waits and at the guarded root write. It changes the root and each distinct changed immediate parent once, preserving opaque content, client times, direct links, grants, and independent trash deadlines. Cycles, stale inventory, and faults fail atomically. No schema or dependency change.

Closes #29.

## Validation

{{VALIDATION}}

## Rulings I made

{{RULINGS}}

## Deferred minors

{{MINORS}}

Independent formal security/protocol and external-client qualification remain development-only deferred under the owner's instruction; release gates remain mandatory. UC48 durable inherited visibility and UC50–53 revalidated restore/physical expiry remain later obligations. Future grant creation requires the implicit recipient-account FK audit before UC35. Scope-algebra limits and actual test-fixture corrections are recorded explicitly below.
