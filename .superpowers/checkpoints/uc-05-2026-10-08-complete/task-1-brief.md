### Task 1: Implement typed self-bound Heimdall update adapter

**Files:** Domain `Identity/IdentityUpdateContracts.cs`; Shared Identity contracts/client (split update parser into partial file if needed); Shared `IdentityUpdateClientTests.cs` plus existing adapter fixtures.

**Interfaces:** `IdentityDetails(Guid Id,string Name,string Email,bool EmailVerified)`; `IdentityUpdateResult(IdentityDetails? Identity=null,string? Error=null)`; `IIdentityUpdateStore.UpdateAsync(Guid identityId,string verifier,Func<CancellationToken,Task<IdentityUpdateResult>> update,CancellationToken)`; `IHeimdallClient.UpdateIdentityAsync(string token,Guid identityId,string name,string email,CancellationToken)`.

- [ ] Write adapter contract tests for exact own route/name-email-only body/actor bearer, invalid or mismatched token, correct curated response,400/401/403/404/409/503, malformed/missing/mismatched identity/scope/role/required metadata, duplicate/numeric JSON, timeout and caller cancellation. Observe absent-method RED.
- [ ] Add Domain contracts and typed adapter. Validate original actor token, strict bounded response parsing, required own public identity/scope/role; map stable errors without provider bodies. Run full Shared suite GREEN. Commit.

