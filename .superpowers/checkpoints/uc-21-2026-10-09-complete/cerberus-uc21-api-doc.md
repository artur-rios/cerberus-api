
## Move an encrypted record (UC21)

`PUT /api/records/{id}/folder` requires the current owner, a current vault handle,
a canonical public record UUID, no query parameters and strict UTF-8 JSON. The
body has exactly two required properties:

```json
{"expectedRevision": 1, "folderId": "abcdefab-1234-4321-abcd-123456789abc"}
```

Use an explicit `"folderId": null` to remove the parent. Omitting `folderId` is
invalid. Nonnull folder IDs must be canonical lowercase, nonzero UUIDs, and
`expectedRevision` must be a positive safe integer. Envelope, client edit time,
owner, key wrappers and relationship arrays are not move inputs.

Success is 200 `record_moved`, with exactly `recordId`, `folderId` (always present,
including null), `revision` and `serverSequence`. The record advances one revision
and receives a greater safe sequence and fresh concurrency stamp. Its ID, owner,
original ciphertext bytes, content epoch and client `editedAt` stay unchanged.
Each distinct changed old/new immediate folder advances its structural revision
and sequence once; ancestors, profiles, collections and direct typed links do not
change. A same-parent or null-to-null move with the current revision succeeds and
advances only the record. A lost-success retry with the old revision returns409;
reload before retrying. Parent revision exhaustion does not block a no-op.

Account-wide access permits any active owned destination whose complete ancestry
is active. A selected handle requires the source and destination to be currently
visible. Destination visibility comes from selected profile-folder ancestors or
owned collection-folder ancestors linked to that profile; a direct record link
alone does not expose its parent. A selected move may retain or remove existing
inherited scope, but the resulting effective active owned profile and collection
sets must each be subsets of their original sets. A destination already visible
through the selected profile can still be rejected if it would introduce another
profile or collection. Use account-wide access for intentional scope expansion.

Get and list derive inherited visibility from the new ancestry immediately. Old
inherited routes disappear, new authorized routes appear and unchanged direct
links survive. The move validates every current native recipient envelope needed
by the resulting collection routes and compares that evidence in its final write.
Native content contexts bind immutable owner/kind/resource ID/epoch, not the parent
folder. Existing valid content and recipient envelope bytes are retained; the
server creates no usable key and does not claim successful client decryption.
Clients must confirm trusted roots and authenticated decryption. Additional opaque
client provisioning, if needed, and independent protocol and real-client release
qualification remain mandatory before release.

Native-visible foreign records return403 for both read-only and read/write
recipients before destination, stale revision or content probing. Hidden records
return404 before malformed native evidence is inspected. Current account, session,
selection and full source/destination ancestry are rechecked after waits and in
the guarded write. Newly contributing earlier collection/grant authority or a
changed source parent requires409 and reload; recipient pin changes cannot reuse
stale evidence. Expiry is exclusive at the database statement snapshot, with no
commit-time guarantee. Target and both immediate parent mutations commit together
or roll back together.

Invalid input returns400, missing identity or handle401, invalid current access or
visible foreign ownership403, hidden/unselected source or destination and selected
scope expansion404, stale/exhausted metadata or changed authority409, oversized
body an empty413, and dependency, relevant ancestry, native or visible content
corruption503. All responses use `Cache-Control: no-store`. Damaged opaque content
may be recoverably deleted through UC20; a move does not repair or propagate it.
Durable synchronization of inherited visibility changes remains a later integration
requirement. This operation does not create trash entries or physically purge data.
