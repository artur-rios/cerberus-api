### Task 1: Typed scoped identity authentication

**Files:** Shared `Identity/HeimdallContracts.cs`, `HeimdallClient.cs`; Shared `AuthenticationIdentityTests.cs`.

**Interfaces:** `HeimdallAuthentication(HeimdallLogin? Login = null, Guid? IdentityId = null, string? Error = null)`; `IHeimdallClient.AuthenticateAsync(string email, string password, CancellationToken)` and `VerifyChallengeAsync(string challenge, string? code, string? recoveryCode, CancellationToken)`.

- [ ] Write adapter fixtures for completed login/challenge, MFA, 401/403, malformed/unavailable, foreign-scope/invalid token, current denial/outage and caller cancellation. Run targeted tests: expected missing interface or behavior fails.
- [ ] Add typed adapter methods preserving existing nullable methods. Structural parse precedes signed/current identity validation; pending challenge carries no identity GUID. Run full Shared suite: expected green. Commit.

