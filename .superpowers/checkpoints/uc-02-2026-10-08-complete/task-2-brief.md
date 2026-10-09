### Task 2: Read permitted account context

**Files:** Domain `Accounts/AuthenticationContracts.cs`; Data `Accounts/AccountAuthenticationStore.cs`; Data `AccountAuthenticationStoreTests.cs`.

**Interfaces:** `AuthenticationAccount(Guid Id, long Revision)`; `AccountAuthenticationResult(AuthenticationAccount? Account = null, string? Error = null)`; `IAccountAuthenticationStore.FindAsync(Guid identityId, CancellationToken)`.

- [ ] Write real PostgreSQL tests for active, absent, closure-pending, erased and unavailable database outcomes plus cancellation. Run: expected missing types/behavior fails.
- [ ] Implement no-tracking account/tombstone lookup and typed provider-failure result; no writes or credentials. Run full Data suite: expected green. Commit.

