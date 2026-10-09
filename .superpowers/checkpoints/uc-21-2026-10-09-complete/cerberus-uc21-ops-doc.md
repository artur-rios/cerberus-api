
### Owned record folder moves (UC21)

`PUT /api/records/{id}/folder` accepts exactly required `expectedRevision` and
required nullable `folderId` with current owner identity/access. Current selected
scope cannot introduce new effective owned profiles or collections. Complete
source/destination ancestry, current native resulting grant envelopes and exclusive
statement-time expiry are validated before an atomic record/immediate-parent update.
Content identity, ciphertext, client time, direct associations and valid wrappers
remain unchanged. Same-parent/no-parent no-ops advance only record metadata;
stale retries return409 without another parent bump. Native-visible foreign RO/RW
records deny403; hidden targets/destinations deny404 before conflict or corruption.

Lock order is own account NO KEY UPDATE, own session UPDATE, selected profile NO KEY
UPDATE, source/result owned collections by public UUID NO KEY UPDATE, resulting
grants by public UUID SHARE, distinct immediate folders by public UUID NO KEY UPDATE,
then record NO KEY UPDATE. Ancestors and foreign accounts/profiles are never locked.
Final SQL recomputes scope, held authority and exact native evidence; new earlier
locks or changed source parents require reload rather than late lock acquisition.
Real owner/recipient edits, trash, create, profile association and protection rotation
races, all seven lock-class expiry checks and faults after each structural mutation
are tested. Future writers must preserve this protocol and the UC35 implicit
recipient-account FK audit remains required. Query plans constrain source and
destination candidates to one each before upward traversal.

No schema, dependency, trash, purge or session change is introduced. Client-root
availability and any additional opaque client provisioning remain release
qualification work; native signature validation is not a decryption proof. Durable
sync visibility changes and shared OpenAPI corrections remain later prerequisites.
BEFORE ANY physical purge in UC22 or later, repair terminal identifiers by resource
kind and prove same-GUID different-kind survival. Never clear a selected profile
reference to null and thereby broaden a handle. Independent protocol/security and
real-client approvals remain deferred for development only. See the
[record API](../security/record-api.md#move-an-encrypted-record-uc21).
