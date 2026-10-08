# Profile associations (UC14)

`PUT /api/profiles/{id}/associations` replaces all three direct link sets on an active
owned profile. Send its canonical lowercase nonzero public GUID, a current identity
bearer, one `X-Cerberus-Vault-Access` header and UTF-8 JSON with exactly four required
fields: `expectedRevision`, `recordIds`, `folderIds`, `collectionIds`. Arrays must be
nonnull and contain unique nonzero canonical public GUIDs within each kind; the same
GUID across different kinds is allowed. Empty arrays remove all links of that kind.
Revision is an exact positive integer through9007199254740991. Unknown or duplicate
properties, numeric strings, compressed input, queries and malformed headers fail400.

Direct records and folders must belong to the authenticated account and be active.
Collections must be active, with an active owner, and either owned or covered by a
current active ReadOnly/ReadWrite recipient grant. Either grant level permits organizing
the recipient's own profile; it grants no extra rights to edit/delete/share the collection.
Admission validates native grant signatures and resource/owner/recipient/epoch/revision
binding against current public pins. Trashed, terminal, foreign direct items and missing
or revoked grants yield the same404 without changing state.

An account-wide handle can add, retain or remove accessible identifiers. A selected
handle can change only its owned selected profile and may retain/remove only currently
visible original direct links. It cannot add a new identifier by guessing another owned
resource ID, including a descendant visible indirectly through a folder. Adding a new
direct link requires account-wide access. Selected handle issuance belongs to UC15.

Success200 returns exactly `profileId`, new `revision`, new `serverSequence`, `recordIds`,
`folderIds`, `collectionIds`. Arrays are deterministically ordered. Every successful set,
including an identical replacement, advances revision and sequence. Client editedAt,
profile envelope/wrappers, resource/grant ownership and encrypted bytes remain unchanged.
One item may appear in several profiles; removing a link changes no underlying resource
and no other profile. A stale retry after a lost response returns409; reload before retrying.

Missing identity/access returns401; invalid current access403; hidden profile or reference
404; stale visible revision or exhausted counters409; unavailable dependencies or corrupt
required native grant state503. Absent, closing or erased accounts return404. Profile
visibility precedes revision decisions; a current profile revision is established before
resolving references. All responses use no-store and errors expose no partial data.

The transaction locks own account/session/profile, then collections and grants in public-ID
order, with SHARE locks for the latter. It rechecks access after waits and current visibility
at the write. Metadata and all three sets commit atomically; insert/delete failures roll back
all links and metadata. Other writers must respect the collection-before-grant lock order
and must not acquire recipient account/profile locks after those shared rows.

Profile creation admits real nonempty sets with the same visibility and native binding rules.
Retrieval and listing project visible IDs in one SQL snapshot with authorization/pagination;
revoked or inaccessible links disappear. Trash snapshots all stored IDs and removes only
the deleted profile's links. Restore must revalidate visibility rather than revive old grants.
Owned retained resources and active owned collection grants participate in complete content
rotation; see [vault protection](../security/vault-protection-api.md).

These tables establish association targets; resource CRUD and sharing endpoints follow in
their own use cases. Independent security/protocol and real-client operability approvals
remain deferred for development only. Release gates and the pending review manifest remain.
