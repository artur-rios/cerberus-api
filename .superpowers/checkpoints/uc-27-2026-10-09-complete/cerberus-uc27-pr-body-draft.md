A visible owned folder can now be recoverably deleted with its active descendants and contained records. `DELETE /api/folders/{id}` requires current identity/vault access and exactly `expectedRevision`, returning six deletion metadata fields. Every member retains opaque ciphertext and shares one 30-day deadline. Independently trashed items retain their own deadlines. Native RO/RW recipients cannot delete; selected owner access admits the root cascade, including directly shared records.

The transaction captures the complete active subtree and stored associations, serializes current owner writers, orders collection/content locks and rechecks authority after waits. One guarded root write authorizes an atomic cascade; all member, link, parent, trash-entry and queue faults roll back together. Typed snapshots preserve original public parent/profile/collection IDs; distinct active external parents advance once. Recipient active reads disappear while grants remain active. No schema, dependency or cryptographic algorithm change.

Closes #28.

## Validation

{{VALIDATION}}

## Rulings I made

{{RULINGS}}

## Deferred minors

{{MINORS}}

Independent formal security/protocol and external-client qualifications remain development-only deferred under the owner's instruction; release gates remain mandatory. Current missing trash handlers retry. UC50–53 listing/restore/empty/physical expiry and UC48 durable inherited visibility remain later obligations. Future grant writers require the separate implicit recipient-account FK audit before UC35.
