# Encrypted folder creation API (UC23)

`POST /api/folders` creates an owned folder from client-encrypted content. Send a current Heimdall bearer token and one `X-Cerberus-Vault-Access` header. Responses use `Cache-Control: no-store`.

The body requires `folderId`, `envelope`, `editedAt` and `profileIds`. `parentFolderId` may be omitted or null for a root. Public IDs use canonical lowercase UUIDs; profile IDs must be unique and nonzero. A folder cannot be its own parent. The envelope uses `cerberus-content-v1` with initial key epoch 1. The encrypted name remains a client-side requirement; the API neither receives nor validates a plaintext name.

`editedAt` must be UTC and at least `0001-01-01T00:00:00.000001Z`. The server floors it to microseconds before persistence. Success is 201 with `folder_created` and exactly `folderId`, `revision`, `serverSequence` and `editedAt`; the initial revision is 1. Internal database IDs, access credentials and encrypted parent/profile content are absent from this output.

Account-wide access can create an unlinked root or associate the new folder with multiple owned active profiles. Selected access requires exactly that selected profile in `profileIds`. An optional parent must already be visible through owned direct folder ancestry or an active owned collection attached to the selection. A read/write collection grant does not allow creating folders for another owner. Parent and all ancestors must remain active, nonterminal and in the same account.

Creation inserts the folder and direct profile links atomically. Each directly linked profile and the immediate parent advances its structural revision/server sequence once; existing ciphertext, key wrappers, client edit times and other links remain intact. Ancestors and selections reached only through inheritance do not receive fabricated direct changes. A failed transaction leaves none of these changes behind.

The server rejects malformed/unknown/duplicate/case-variant body fields, invalid envelopes, self-parent input, compressed/non-UTF-8/BOM bodies, query parameters and malformed/repeated access headers with 400. Oversized input gets the common empty 413. Missing identity or access is 401; current revoked, expired or superseded access is 403. Hidden/missing/foreign/trashed/terminal relationships are nonrevealing 404. A folder UUID already present, trashed or permanently reserved, or exhausted affected revision capacity, yields 409. Necessary corrupt structural state or unavailable persistence yields 503. Known failures return no success payload.

Folder identifiers are reserved within the folder kind. A record, profile, account, collection or grant marker using the same UUID cannot block a distinct folder. Retrying a lost successful creation returns 409 rather than duplicating links or metadata. Reload the winning state before retrying a conflict.

New folders belong to the complete owned protection-rotation inventory. Missing a folder rejects rotation before proof consumption; complete rotation preserves its client edit time, parent and links. A record can use a newly created permitted folder immediately. Offline inherited visibility delivery and external-client key provisioning remain release qualification obligations.
