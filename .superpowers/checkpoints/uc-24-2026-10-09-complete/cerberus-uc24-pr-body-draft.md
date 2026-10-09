Folder listing now returns only currently permitted encrypted folders and opaque pagination metadata. `GET /api/folders` accepts canonical `pageSize`/`cursor` with current identity and vault access; success contains only five folder fields in `items` plus `nextCursor`.

Owned and native recipient RO/RW scope includes selected direct/member folders and descendants. A record-only grant or profile-record link cannot expose its containing folder; a member leaf cannot expose unshared parents or siblings. One SQL snapshot filters current permissions and complete active ancestry before pagination, verifies every visible foreign native contributor and applies a stable sequence high-water. Listing makes no writes or locks. No schema, dependency or cryptographic algorithm changes.

Closes #25.

## Validation

{{VALIDATION}}

## Rulings I made

{{RULINGS}}

## Deferred minors

{{MINORS}}

Independent formal security/protocol and external-client qualification remain development-only deferred under the owner instruction; release gates remain mandatory.
