# Business Rules — Cerberus API

## Domain Entities

| Entity | Meaning |
| --- | --- |
| **Heimdall account** | External identity responsible for login and identity authorization; not a vault profile. |
| **Cerberus account** | One user's vault ownership boundary and Cerberus-specific details, linked to exactly one Heimdall account. |
| **Profile** | A selectable view and access boundary within one Cerberus account, separate from both accounts' details. |
| **Folder** | An account-owned container that may nest other folders and contains records. |
| **Record** | A named item with a user-defined collection of typed key/value fields. |
| **Record field** | Text, numeric, boolean, or hidden text content inside a record. |
| **Record template** | A client-offered field definition such as email/password, with mandatory fields when that template is used. |
| **Collection** | An account-owned grouping of folders and records, associated with profiles and optionally shared with other accounts. |
| **Collection grant** | Explicit permission for a recipient account to read, or read and write, a collection's included content. |
| **Recovery credential** | One-time recovery capability, backed by end-to-end encrypted key material, that can be refreshed. |
| **Offline authorization policy** | The account owner's renewal interval or decision to disable periodic renewal. |
| **Trash entry** | A recoverable deletion with a 30-day expiry for a record, folder, collection, or profile. |
| **Software client grant** | Explicit access and decryption-key provisioning for an authenticated software consumer. |

Templates describe caller-side record construction and validation. Their inclusion here does
not invent server-side template CRUD or permit the API to inspect encrypted field values.

## Relationships

| Relationship | Cardinality and boundary |
| --- | --- |
| Cerberus account → Heimdall account | Exactly one linked identity per Cerberus account. Each user has their own Cerberus account. |
| Account → profiles | Each profile belongs to exactly one account; an account can contain multiple profiles. |
| Account → folders / records / collections | Each entity has one owning account. Sharing never changes that ownership. |
| Folder → child folders | A folder may contain multiple child folders. Nesting must not form a cycle. |
| Folder → records | A folder may contain records; deleting it deletes its contained records. Exact membership constraints will be formalized in Phase 2. |
| Profiles ↔ folders / records | Many-to-many within the owning account: a folder or record can appear in multiple profiles. |
| Profiles ↔ collections | Many-to-many: an account-owned collection may be associated with multiple profiles. |
| Collections ↔ folders / records | Many-to-many: records and folders may be included in multiple collections. |
| Collection → recipient accounts | Zero or more explicit grants; this is the permitted cross-account sharing boundary. |
| Record → fields | User-defined typed fields; only the record name is universally mandatory. |
| Account → offline policy | One account-level policy; default 24 hours, another chosen interval, or renewal disabled. |

Accounts cannot share profiles directly. Cross-account access to folders and records is allowed
only through explicitly shared collections or explicitly granted software access. A grant
does not expose unrelated items or the owner's private profile organization.

## Rules

| ID | Rule | Rationale |
| --- | --- | --- |
| BR-01 | Each user has their own Cerberus account, linked to exactly one Heimdall account. | Keep vault ownership tied to one authenticated identity. |
| BR-02 | Delegate identity authentication and authorization to Heimdall; enforce Cerberus resource ownership and collection grants in Cerberus. | Keep identity and vault responsibilities distinct. |
| BR-03 | A vault profile is distinct from account details and belongs to one Cerberus account. | Support selective vault access without conflating identity profiles. |
| BR-04 | A record or folder may appear in multiple profiles within its owning account. | Support different device views without requiring duplicate records. |
| BR-05 | A collection belongs to one account and may be associated with multiple profiles. | Apply the user's clarified collection model. |
| BR-06 | Records and folders may belong to multiple collections. | Support flexible organization. |
| BR-07 | Permit nested folders and reject cyclic parent relationships. | Preserve a valid folder hierarchy. |
| BR-08 | Require a name for every record; permit otherwise user-defined fields of the four stated types. | Keep free-form records useful without imposing a fixed credential schema. |
| BR-09 | When a caller uses a template, require its mandatory fields; permit records created without a template. | Support guided entry and unrestricted custom records. |
| BR-10 | Protect vault contents with end-to-end encryption; the API must not possess plaintext vault contents or usable decryption keys. | Keep the server unable to decrypt users' vaults. |
| BR-11 | Encrypt communication between clients, Cerberus, and Heimdall. | Protect information in transit as well as at rest. |
| BR-12 | Support a master vault password or distinct profile passwords; keep vault unlocking separate from identity login. | Respect the requested access choices. |
| BR-13 | Allow a recovery credential to complete only one server-mediated recovery; issue a replacement after successful recovery. | Prevent reuse of the consumed recovery credential. |
| BR-14 | Permit recovery-key refresh at any time by an authorized user and invalidate the previous recovery credential. | Let users replace recovery credentials deliberately. |
| BR-15 | Give authenticated software only explicitly granted access; return ciphertext for local decryption with authorized keys. | Authentication alone must not expose the entire vault or plaintext. |
| BR-16 | Default periodic offline authorization renewal to 24 hours; allow the owner to change the interval or disable renewal. | Balance offline availability with user-controlled revocation exposure. |
| BR-17 | Enforce known revocations on reconnection; when renewal is enabled, block offline use after the authorization period expires until renewal succeeds. | Bound offline access where the user enables a renewal period. |
| BR-18 | Resolve conflicting edits by the latest edit. | Use the user's selected synchronization policy. |
| BR-19 | Online-only clients must not persist vault data locally; web clients may use temporary memory during display but must not use persistent storage or caching. | Preserve the clarified browser and online-only behavior. |
| BR-20 | Put deleted records, folders, collections, and profiles in trash by default; permit restoration for 30 days, then delete permanently. | Provide the agreed accidental-deletion window. |
| BR-21 | Let authorized owners permanently delete a record immediately or empty the trash at any time. | Provide explicit irreversible deletion controls. |
| BR-22 | Deleting a folder also deletes records inside it, including records contained through its nested hierarchy. | Apply the selected folder cascade policy. |
| BR-23 | Deleting a collection or profile deletes the entity and its associations, retaining underlying folders and records. | Prevent an organizational deletion from removing vault content. |
| BR-24 | Require fresh authentication for account closure, revoke access immediately, and allow cancellation within 30 days before permanent deletion. | Protect destructive closure and provide an explicit reversal window. |
| BR-25 | Offer immediate permanent account deletion; remove Cerberus-owned data and grants without deleting the linked Heimdall identity. | Respect erasure intent without affecting other Heimdall-consuming applications. |
| BR-26 | Reapply completed deletions when restoring backups so erased data cannot become active again. | Preserve deletion across disaster recovery. |
| BR-27 | Share collections with explicit read-only or read/write grants; only the collection owner manages sharing and deletes the collection. | Define a controlled cross-account sharing boundary. |
| BR-28 | Collection grants expose only included content and do not transfer ownership or grant access to unrelated account data. | Preserve isolation around shared content. |
| BR-29 | Meet the requested LGPD and GDPR requirements through documented and verified controls. | Treat privacy and deletion requirements as part of the first release. |

One-time recovery means future recovery requests are rejected after consumption or refresh.
It cannot retroactively retract keys or plaintext already copied by a client. The formal
cryptographic design must address key wrapping, rotation, and transactional recovery without
claiming that invalidating an API credential alone destroys old cryptographic material.

## Validation Constraints

| Concern | Approved constraint |
| --- | --- |
| Record name | Required. No unique-name constraint or length limit has been approved yet. |
| Free-form record | No universally mandatory content field beyond the name. |
| Typed field | One of text, numeric, boolean, or hidden text. |
| Template-based record | Required fields come from the selected template; the example email/password template requires both. |
| Account ownership | Every owned entity belongs to one Cerberus account. |
| Profile / collection associations | Enforce account ownership and explicit sharing boundaries. |
| Folder nesting | No self-parent or ancestor cycle. |
| Collection grant | Read-only or read/write; sharing administration belongs to the owner. |
| Recovery | Reject a consumed or superseded recovery credential. |
| Offline renewal | Default 24 hours; accept a chosen positive interval or an explicit disabled policy. Numerical bounds are a Phase 2 contract decision. |

End-to-end encryption means the API cannot validate the plaintext name or template fields by
decrypting them. The formal requirements must distinguish caller-side content validation from
server-side envelope and metadata validation. Field lengths, uniqueness, identifiers, schema,
and request limits are intentionally reserved for the formal contract phase rather than
silently imposed here.

## Permissions

| Actor | Allowed operations |
| --- | --- |
| Account owner | Manage their account details, profiles, folders, records, collections, vault access, recovery, trash, offline policy, and explicit grants. |
| Read-only collection recipient | Read included content with the granted keys; no modification permission. |
| Read/write collection recipient | Read and edit included content within the grant; cannot manage sharing or delete the collection. |
| Authorized software client | Retrieve explicitly permitted encrypted information and decrypt locally with granted keys. |
| Unauthenticated caller | Only identity-entry/recovery operations explicitly allowed by the eventual contract; no vault content access. |

The exact set of write operations, shared-folder descendant visibility, and recipient deletion
rights will be spelled out in the Phase 2 authorization matrix. No administrator plaintext
access is authorized.

## Lifecycle

| Entity / capability | Lifecycle |
| --- | --- |
| Record | Active → trash → restored, or permanently deleted after 30 days / explicit purge; immediate permanent deletion is also available. |
| Folder | Active → trash with its contained records → restored or permanently purged. Nested cascade and restoration are formalized together. |
| Collection | Active → trash → restored or permanently purged; deletion removes associations without deleting contained items. |
| Profile | Active → trash → restored or permanently purged; deletion removes associations without deleting contained items. |
| Account | Active → closure pending with access revoked → cancellation within 30 days or permanent deletion; immediate permanent deletion is also available. |
| Recovery credential | Current → consumed or superseded; successful recovery replaces it with a new current credential. |
| Collection grant | Granted → revoked; API access stops, and clients enforce revocation on reconnection or authorization expiry. |
| Offline authorization | Valid → renewal due or revoked; renewed only after successful online authorization when renewal is enabled. |

The account deletion policy above is the user's approved product decision. Specific compliance
retention exceptions, backup lifetimes, Heimdall reauthentication during closure cancellation,
and recipient copies will be addressed in Phase 2. Account closure does not permit previously
deleted contents to be recovered after permanent deletion.

## Prohibitions

- Never share profiles directly between accounts or bypass collection/software grants.
- Never expose unrelated vault content through a shared collection.
- Never decrypt vault data on the API or write plaintext secrets or usable vault keys to logs.
- Never reuse consumed or replaced recovery credentials for server-mediated recovery.
- Never resurrect permanently deleted content through synchronization or backup restoration.
- Never use persistent browser storage or caching for vault data.
- Never claim immediate revocation while a client is disconnected. Renewal-disabled clients
  have no periodic offline expiry and learn of revocation upon reconnection.
- Never claim that removing a grant retracts plaintext or decryption keys already copied.

Related documents: [Project Overview](Project%20Overview.md),
[Technology Stack](Technology%20Stack.md), and [Workflow](Workflow.md).
