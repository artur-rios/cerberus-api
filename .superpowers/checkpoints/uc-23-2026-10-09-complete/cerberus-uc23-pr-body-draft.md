Creating an owned folder now validates its encrypted envelope and current permitted relationships, inserts the folder and direct profile links, then returns public metadata. `POST /api/folders` accepts required `folderId`, `envelope`, `editedAt`, `profileIds` and optional nullable `parentFolderId`; success returns exactly `folderId`, `revision`, `serverSequence`, `editedAt`.

Account-wide access supports root or nested creation with zero or multiple owned profiles. Selected access attaches only the new folder to its selected profile and requires any parent to be visible through complete owned ancestry or an active owned collection. Read/write grants cannot create folders for another owner. Creation advances direct profiles and the immediate parent atomically while preserving their content and client times; failed writes roll everything back.

Typed folder UUID reservations preserve other resource kinds. New folders appear in profile relationships, support record creation immediately and join complete protection rotation. Current access, policy, generation and exclusive database expiry are rechecked after lock waits and at the guarded insertion.

Closes #24.

## Validation

{{VALIDATION}}

## Rulings I made

{{RULINGS}}

## Deferred minors

{{MINORS}}

Independent formal security/protocol and external-client operability qualification remain development-only deferred under the owner instruction; release gates remain mandatory.
