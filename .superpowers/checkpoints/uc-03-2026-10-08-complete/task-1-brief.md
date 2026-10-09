### Task 1: Persist and verify account-wide access

**Files:** Domain `Access/OpaqueAccessHandle.cs`, `Access/VaultAccessSession.cs`, `Accounts/AccountReadContracts.cs`; Data `Accounts/AccountReadStore.cs`, `AppDbContext.cs`, generated migration; Domain handle tests and Data account-read tests.

**Interfaces:** `OpaqueAccessHandle.TryHash(string? token, out string verifier)`; `AccountSnapshot(Guid Id,long Revision,AccountState State,byte[] DetailsEnvelope)`; `AccountReadResult(AccountSnapshot? Account=null,string? Error=null)`; `IAccountReadStore.ReadAsync(Guid identityId,string verifier,DateTimeOffset now,CancellationToken)`.

- [ ] Add canonical handle and real PostgreSQL tests for valid/no-expiry sessions, expiry equality/future issuance, profile scope, stale policy/generation, revocation, cross-account use, hidden account, corruption boundary and provider failure/cancellation. Run: expected missing types/behavior fails.
- [ ] Implement verifier-only session schema with account FK/cascade and unique verifier, current authorization predicates and conditional ciphertext projection. Generate migration. Run Domain/Data tests: expected green. Commit.

