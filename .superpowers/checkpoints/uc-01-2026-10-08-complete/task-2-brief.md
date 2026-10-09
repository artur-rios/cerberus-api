### Task 2: Establish proved scoped identity

**Files:** Shared `Identity/HeimdallClient.cs`, `HeimdallContracts.cs`; Domain `Accounts/RegistrationContracts.cs`; Shared fixture tests.

**Interfaces:** `IHeimdallClient.EstablishRegistrationIdentityAsync(HeimdallRegistration registration, string? proofToken, CancellationToken cancellationToken)` returns `RegistrationIdentity`; `IRegistrationStore.RegisterAsync(RegistrationRequest request, Func<CancellationToken, Task<RegistrationIdentity>> establishIdentity, CancellationToken cancellationToken)` returns `RegistrationResult`.

- [ ] Add HTTP fixtures for new creation, proved existing login, explicit proof, MFA, duplicate email, malformed/denied/unavailable response and lost-create-response retry. Observe expected failures.
- [ ] Implement typed outcomes; only rejected login permits constrained creation. Revalidate all existing identity proofs and preserve original adapter contracts.
- [ ] Run the complete Shared suite; expected all pass. Commit.

