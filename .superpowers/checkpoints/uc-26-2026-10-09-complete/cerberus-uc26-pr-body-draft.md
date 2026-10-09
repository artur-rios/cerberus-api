Folder content replacement now accepts a strict encrypted envelope, edit time and expected revision. `PUT /api/folders/{id}` permits current owners and native read/write recipients in current folder scope, returning exactly folderId/revision/serverSequence/editedAt. Read-only access is denied; record-only grants cannot authorize folder edits. Parent, children, records, profiles, collections, membership and ownership stay unchanged.

The transaction reloads current authority after lock waits and embeds complete current folder ancestry/permission in the guarded write. Every eligible overlapping foreign contributor is native-verified; new unheld authority and competing edits require reload/retry. Post-write faults roll back row changes. Locks follow existing cross-owner order and never form a foreign account/profile or upward folder chain. Key epoch is preserved; changed ciphertext requires fresh salt and nonce. No schema, dependency or cryptographic algorithm change.

Closes #27.

## Validation

{{VALIDATION}}

## Rulings I made

{{RULINGS}}

## Deferred minors

{{MINORS}}

Independent formal security/protocol and external-client qualification remain development-only deferred under the owner's instruction; release gates remain mandatory. Monotonic target sequence is the synchronization foundation; durable offline visibility delivery is still a later release obligation.
