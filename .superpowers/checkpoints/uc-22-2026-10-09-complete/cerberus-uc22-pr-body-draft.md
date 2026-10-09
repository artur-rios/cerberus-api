Permanently deleting an owned active or trashed record now commits a terminal intent, flushes its external erasure event, removes its ciphertext and associations, then returns confirmation. A ledger outage after intent leaves the record inaccessible with durable purge work; it does not roll deletion back. Selected trash access revalidates restricted history against current owned scope.

Terminal IDs and ledger files now use resource kind plus UUID. Existing readers, writers, native grants and ID reservation use that identity, so deleting a record preserves a folder, collection or other kind sharing its UUID. The migration preserves legitimate legacy markers, the ledger still reads legacy files, and record replay physically removes restored ciphertext before traffic opens. Pending terminal records leave key-rotation inventory while recoverable trash remains.

`DELETE /api/records/{id}/permanent` accepts exactly `expectedRevision` and returns only `recordId` and `deletedAt`. Current owners can erase damaged opaque content; both native recipient access modes are denied. Persisted work IDs, tokens, keys and exclusive database-time expiry fence every purge mutation and completion.

Closes #23.

## Validation

{{VALIDATION}}

## Rulings I made

{{RULINGS}}

## Deferred minors

{{MINORS}}

Independent formal security/protocol and real-client qualification remain development-only deferred under the owner's instruction; main/tag/release gates remain mandatory. No production deployment or live customer deletion is performed.
