### Task 4: Complete content and active-grant rotation

**Files:** Modify Domain/Protection/ProtectionChange.cs and Data/Protection/VaultProtectionChangeStore.cs, create Domain/Resources/CollectionGrantReplacement.cs; tests/Domain/ProtectionChangeTests.cs plus Data/VaultResourceRotationTests.cs.
**Interfaces:** Extend ContentReplacement kinds; onlyprofileKeyWrappers, additive ProtectionChange.GrantReplacements default[]; CollectionGrantReplacement(Guid GrantId,long ExpectedRevision,RecipientEnvelope KeyEnvelope). Existing old requests unchanged for empty new inventory; rewrap requires both manifests empty.
- [ ] Write native realPG fullaccount/profiles/record/folder/collection/activeownedgrant rotation incltrash; missing/extra/duplicate/foreign resources/grants, received/revoked grants not authorized, stale/corrupt current/new epoch/native recipient/author/resource/grant/revision, no proof consumption on validation failure, fresh salts/nonces, rollback afterconsume, preserveEditedAt/owner and monotonics, rewrap byte retention. Runfocused; expected old domain/store rejects new manifest RED.
- [ ] Extend complete inventory/validation before proof consume, validate current/replacement native bindings against recipient/owner pins, atomic resource/grant updates/counters/collectionepoch and signedbody comparison. Run wholeDomain/Data; expected allpass0skip. Commit.
**Completion:** New content/grants cannot escape or undermine existing rotation guarantees.

