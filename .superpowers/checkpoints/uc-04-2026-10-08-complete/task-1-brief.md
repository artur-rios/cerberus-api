### Task 1: Atomically replace authorized account details

**Files:** Domain `Accounts/AccountUpdateContracts.cs`; Data `Accounts/AccountUpdateStore.cs`; Data tests `AccountUpdateStoreTests.cs`.

**Interfaces:** `AccountUpdateRequest(Guid IdentityId,string Verifier,long ExpectedRevision,byte[] DetailsEnvelope,DateTimeOffset Now)`; `AccountUpdateResult(Guid? Id=null,long Revision=0,string? Error=null)`; `IAccountUpdateStore.UpdateAsync(AccountUpdateRequest,CancellationToken)`.

- [ ] Write real PostgreSQL tests for valid/no-expiry writes, unchanged identity/policy/session fields, stale revision/exhaustion, all account/session denials, unavailable provider/cancellation, concurrent same-revision writers, and intercepted close/revoke/tombstone changes before UPDATE. Run: missing contract/store RED.
- [ ] Implement metadata-only initial authorization and conditional atomic update with all current predicates and exact revision. Do not retrieve the previous envelope or change identity. Run full Data suite GREEN. Commit.

