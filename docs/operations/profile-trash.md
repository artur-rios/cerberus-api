# Recoverable profile deletion (UC13)

`DELETE /api/profiles/{id}` moves an owned active profile to trash. Use its canonical lowercase public GUID, a current identity bearer, one `X-Cerberus-Vault-Access` header, and UTF-8 JSON containing only `expectedRevision`. The revision is an exact positive integer through9007199254740991. Query parameters, unknown/duplicate properties, numeric strings, compressed bodies and forged owner/resource identifiers are rejected400.

An account-wide handle can delete any own active profile. A selected handle can delete only that selected profile. Invalid, expired, revoked or stale-policy access returns403. Missing identity/access returns401. Missing, foreign, already trashed, erased and out-of-selection targets return the same404 before revision disclosure. An absent, closing or erased account returns404. Identity or persistence outages fail closed503.

The transaction locks account, current session and profile in that order. It rechecks the current statement time after the profile lock before revision decisions and again in the final write. A stale visible revision or exhausted revision/sequence returns409. If an edit wins first, deletion with the old revision conflicts; if deletion wins first, the edit sees404. Errors roll back profile, operation, membership, queue and handle changes together.

Success200 contains exactly `profileId`, `trashOperationId`, new `revision`, new monotonic `serverSequence`, server UTC `deletedAt` and `purgeAt`. The deadline is exactly720hours (30days) after deletion, independent of the database session time zone. Profile ciphertext, key wrappers, ownership and client `editedAt` are retained unchanged. Active profile reads/listing immediately omit it. All owned handles selected to the deleted profile are revoked; account-wide and unrelated selected handles survive. Restoration must require a fresh selected unlock rather than revive old handles.

A durable operation identifies the root and its typed membership. The profile snapshot
contains all stored record/folder/collection identifiers, including links currently hidden
by grant revocation. The same transaction removes only this profile's links, preserving
underlying records, folders, collections, grants and other profiles' memberships.
A queue or membership failure rolls back those changes together. Restoration must
revalidate current resource, owner and grant visibility before recreating links.
Trashed profiles remain in the complete owner key-rotation inventory until physical purge.

Deletion is recoverable. UC50–52 will provide trash listing, restoration and explicit purge; UC53 will register the typed retention handler that physically purges expired entries. This change queues durable `trash/{trashOperationId}` work due at `purgeAt`; the existing worker fails closed for an unregistered handler and retries. It does not claim that physical cleanup is already implemented. Future restore/list handlers must immediately enforce the deadline, operation membership and associations even while worker retries are pending. UC22 repairs terminal identities by resource kind and UUID and proves record-only erasure with unrelated same-UUID content preserved. Physical profile deletion and replay still require their own later handlers; selected profile IDs must never be cleared into account-wide access.

Retrying a successful deletion returns404, creates no duplicate operation and does not extend retention. A failed rolled-back transaction can be retried normally. Sequence gaps are valid. All responses are no-store. Independent security/protocol and real-client approvals remain deferred for development only; strict release gates and pending review manifest remain unchanged.
