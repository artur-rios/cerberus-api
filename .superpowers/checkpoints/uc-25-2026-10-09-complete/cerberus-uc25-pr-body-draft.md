Folder detail now returns one currently permitted encrypted folder and only visible organization references. `GET /api/folders/{id}` requires current identity and vault access, a canonical public ID and no query or GET body. Success contains exactly five encrypted metadata fields plus direct profile IDs, effective collection IDs and an independently permitted immediate parent ID.

Owned and native RO/RW recipient scope includes selected direct/member folders and descendants. Record-only access cannot reveal the folder; a target-only association or member-leaf grant cannot reveal an unshared parent. One target-first SQL statement captures current permissions, complete active ancestry, references and every contributing native grant, with pins parsed once per party. Hidden targets stay nonrevealing; required corruption fails without a partial payload. Reads make no locks or mutations. No schema, dependency or cryptographic algorithm changes.

Closes #26.

## Validation

{{VALIDATION}}

## Rulings I made

{{RULINGS}}

## Deferred minors

{{MINORS}}

Independent formal security/protocol and external-client qualification remain development-only deferred under the owner instruction; release gates remain mandatory.
