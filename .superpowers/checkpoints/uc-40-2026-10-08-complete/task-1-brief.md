### Task 1: Strict recovery transition and durable-result contracts

**Files:** Domain/Protection/RecoveryReplacement.cs; Domain.Tests/RecoveryReplacementTests.cs; tests/TestSupport/ProtectionFixture.cs native recovery signing helper.
**Interfaces:** strict `RecoveryReplacement(string Operation,Guid IdempotencyKey,long ExpectedRevision,PasswordWrapper PasswordWrapper,RecoveryWrapper RecoveryWrapper,PublicJwk NewRecoveryVerifier)` with `IsValid()/IsValidTransition(ProtectionMaterial current)` and `Replace(ProtectionMaterial current)` returning complete material retaining other pins. `RecoveryRequest(Guid Actor,long IdentityIssuedAt,Guid ChallengeId,string Proof,byte[] RawBody,RecoveryReplacement Replacement)`; `RecoveryDetails(string Status,long ProtectionRevision,long Generation,long RevocationGeneration)`; `IVaultRecoveryStore.RecoverAsync(RecoveryRequest,CancellationToken)->Task<VaultResult<RecoveryDetails>>`; `VaultRecoveryOperation` entity accountId/key/digest43/counters. Existing challenge overload extends recover in Task2.
- [ ] Write metadata/safe integer/key role/fingerprint/slot/generation/fresh-context/private JSON cases; absent-types RED.
- [ ] Implement pure strict contracts and material replacement preserving retained pins, no server secrets. Full Domain GREEN; commit/ledger.

