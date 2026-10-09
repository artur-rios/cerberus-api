### Task 3: Typed application handlers and protected HTTP routes

**Files:** Command Protection commands/validators/handlers/outputs/maps; Query Protection read handler/output; WebApi VaultController/DI/raw body capture; mirrored Command/Query/WebApi tests.
**Interfaces:** init accountId/expectedAccountRevision/material; challenge operation/requestHash; unlock expectedProtectionRevision plus trusted internal actor/proof headers/raw bytes; read actor only.

- [ ] Write handler tests for validation/trusted actor/context/statuses/incomplete store results/cancellation; observe RED, implement and run Command/Query GREEN.
- [ ] Write real host main init/read/challenge/native-sign/unlock/account-read and all AFs, strict JSON/headers/query/encoding, current identity/fresh signed iat, ownership isolation and hashed-only persistence. Observe absent-route RED; implement strict controllers/DI/body capture, then full WebApi GREEN.
- [ ] Generate/inspect OpenAPI, document client plaintext contract and bootstrap/unlock/expiry/retry/protocol-deferral behavior, mark only UC38 Done and M02 6/9. Full unfiltered coverage/helpers/spec/OpenAPI/diff checks, commit/ledger.
- [ ] One fresh final review; one RED→GREEN correction pass for Critical/Important if needed; own PR closing39, all required CI green, authorized merge/close/projectDone/sync/archive.
