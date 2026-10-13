# UC-45 Get offline policy implementation plan

> **For agentic workers:** Use superpowers:executing-plans for inline execution. Follow test-driven development for each behavioral change.

**Goal:** Return the current owner's offline policy with its initial 24-hour default.

**Architecture:** Add a metadata-only query/store and strict GET route at `/api/accounts/me/offline-policy`. Use the existing current identity middleware, opaque vault handle, account lifecycle and terminal erasure predicates. Account-wide sessions and currently valid selected-profile sessions may read their own account policy; selected-profile reads must validate the profile's owner, lifecycle and tombstone. Never load ciphertext or key material.

**Tech Stack:** .NET 10, existing mediator, EF Core/PostgreSQL, xUnit family attributes.

**Spec:** [UC-45](../../requirements/Use%20Case%20Specification%20Document.md#uc-45-get-offline-policy), FR-SY-01, account fields and authorization matrix in System Requirements, Testing Specification.

## Global constraints

- One issue #46, branch `feature/uc-45-get-offline-policy`, PR into `develop`.
- User authorized continuing into another milestone while UC-43 stays blocked; Heimdall #178 now tracks that prerequisite. Preserve all behavioral dependencies and fail-closed authentication.
- Preserve existing unattended verification/delivery authorization recorded in the backlog plan. No release or production deployment.
- Full unfiltered suite, 90% merged production line coverage and required branch-policy/test/docker checks gate delivery.
- Output only `renewalEnabled`, nullable `renewalInterval` (TimeSpan), `policyRevision` and `revocationGeneration`. Disabled policy has null interval; enabled interval must be positive and versions must be positive. Corrupt policy returns 503.

## Review focus

1. Selected-profile handles must not expose another account's policy or authorize an invalid selected profile.
2. Expired, future-issued, stale-policy, stale-generation and revoked sessions must fail closed.
3. Disabled renewal must require a no-expiry session and return null interval.
4. A body (including unspecified HTTP/2 length), query or duplicate/malformed handle must not bypass strict GET input.
5. Persistence and current identity failures must disclose neither policy nor ciphertext; no-store applies.

### Task 1: Metadata query, persistence and strict HTTP endpoint

**Files:**
- Create Domain/Offline/OfflinePolicyContracts.cs, Query/Offline/GetOfflinePolicyQuery.cs, Query/Offline/GetOfflinePolicyHandler.cs, Query/Offline/OfflinePolicyOutput.cs, Query/Offline/OfflinePolicyMessages.cs, Data/Offline/OfflinePolicyReadStore.cs under the existing production projects.
- Modify WebApi/Controllers/AccountController.cs and WebApi/Startup.cs.
- Create GetOfflinePolicyHandlerTests.cs, OfflinePolicyReadStoreTests.cs and OfflinePolicyHttpTests.cs in the corresponding test projects.

**Interfaces:** Store `ReadAsync(Guid identityId, string verifier, DateTimeOffset now, CancellationToken)` returns nullable `OfflinePolicySnapshot` plus stable error. Handler consumes `GetOfflinePolicyQuery(Guid IdentityId, string? VaultAccess)` and outputs `OfflinePolicyOutput` through `DataOutput`.

- [ ] Write the HTTP success/default test first. Expected route returns 200 and true/24 hours/version 1, no ciphertext, no-store. Run the focused HTTP test and observe absent-route failure.
- [ ] Write handler and real PostgreSQL tests covering main flow and all four AFs: invalid input 400; hidden/closing/erased account 404; absent identity/access 401; denied scope/session 403; database/identity unavailable and invalid stored policy 503. Include each review-focus case and cancellation. Initially tests may fail compilation until the contracts exist; the HTTP behavioral failure is the RED evidence.
- [ ] Implement the single-snapshot store projection that withholds all policy fields unless current authority permits the read. Validate stored policy before returning data. Wire the strict route and DI.
- [ ] Run focused query/Data/HTTP families. Expected all pass; verify unchanged persisted state for denials and no confidential fields in responses.
- [ ] Commit implementation and tests with a Conventional Commit subject.

### Task 2: Contract, documentation and delivery

**Files:** docs/security/offline-policy-api.md, docs/contracts/openapi.json, docs/operations/foundation-status.md, CHANGELOG.md, README.md.

**Interfaces:** Document Task 1's exact route/output/statuses; metadata read does not issue a lease or enable software authentication.

- [ ] Regenerate OpenAPI; document session scopes, default, disabled policy, format and fail-closed errors. Record the dependency exception and Heimdall blocker.
- [ ] Run specs, Python helper tests, locked restore, vulnerability audit, full unfiltered application suite with coverage, OpenAPI check, protocol harness and Docker build. Expected no failures/skips and >=90% production line coverage.
- [ ] Obtain one fresh whole-branch code review; fix Important/Critical findings with regression evidence before delivery.
- [ ] Update README issue #46 status/count only for the verified delivery. Push, open its PR, wait for exact-head required checks and merge normally under recorded authorization. Close issue/project status and delete delivered branch. Sync develop before UC-46.
