Closes #14.

Adds `DELETE /api/profiles/{id}` with a strict `expectedRevision` body. It atomically trashes an owned active profile, records durable operation membership and a 30-day deadline, queues retention work, and revokes all owned handles selected to that profile. Ciphertext, wrappers and client edit time remain available for recovery; ordinary reads/listing omit the profile immediately. Hidden targets return404, stale visible revisions409, and expiry or persistence failures leave no partial state.

Actual associations are currently empty. Resource/sharing implementations must extend snapshots and deactivation while preserving underlying content. Trash listing, restore, explicit purge and the typed retention handler belong to UC50–53; this change does not implement physical purge. Independent security/protocol and real-client approvals remain deferred for develop only, with release gates unchanged.

Validation: 1,845 tests passed across all six families, zero failures/skips; 97.9% production line and 89.6% branch coverage. Includes real PostgreSQL/HTTP tests for concurrency, lock-wait expiry, rollback, selected-handle revocation and strict boundaries. Helper tests23, specification consistency, generated OpenAPI drift and whitespace checks pass. The changelog includes UC13 and catches up UC12 entries under the newly introduced policy.
