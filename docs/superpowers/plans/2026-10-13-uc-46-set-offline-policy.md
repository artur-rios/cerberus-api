# UC-46 Set offline policy implementation plan

> **For agentic workers:** Use superpowers:executing-plans inline and test-driven development.

**Goal:** Let the authenticated owner update or disable offline renewal with optimistic concurrency.

**Architecture:** Extend the existing offline policy flow with strict PUT input, a command handler and a transactional metadata store. Lock account, session and optional selected profile in the existing canonical order; recheck current authority after waits and use database statement time at the guarded write. Increment policy revision only; existing sessions become stale through their policy-version mismatch. Disconnected leases retain their previously issued policy until reconnection/expiry.

**Tech Stack:** Existing .NET 10 mediator/validation, EF Core/Npgsql, PostgreSQL, family xUnit attributes.

**Spec:** UC-46 main/AF-01–06 and FR-SY-02/03 in requirements documents; account fields/authorization matrix; reviewed integer-second lease contract in docs/security/protocol-review.md and tools/protocol-harness/python/cerberus_protocol/lease.py.

## Global constraints

- Issue #47, branch feature/uc-46-set-offline-policy, PR into develop. Continue the authorized batch while UC43 remains blocked on Heimdall178.
- PUT /api/accounts/me/offline-policy requires current scoped human authentication and one vault handle; account-wide or active own-profile sessions are accepted as in UC45.
- JSON has exactly expectedPolicyRevision, renewalEnabled and renewalInterval. All three are required; disabled interval must be explicit null. Enabled interval must be a positive whole-second TimeSpan: native leases encode integer-second issuance/expiry. Reject unsupported fractions, overflow and invalid counters without inventing a fixed maximum duration.
- Preserve account details/revision, revocation generation, profiles, grants, keys and unrelated sessions. Save setting/interval and increment PolicyRevision, invalidating old-version online access.
- Full unfiltered suite and fresh >=90% line coverage, whole-branch review and exact-head branch-policy/test/docker CI gate delivery. No production/release authorization.

## Review focus

1. Profile trash or session/actor changes during a lock wait must not authorize a write from an old snapshot.
2. Exclusive expiry must be checked at the guarded statement, rather than transaction start; expiry after a successful guarded write must not undo that authorized operation.
3. Concurrent same-version updates have one winner; rollback faults preserve all state.
4. Required JSON/strict native transport, disabled-null, supported durations and counter exhaustion must fail precisely.
5. Policy changes do not alter disconnected leases or revive sharing/software grants; no confidential fields enter output.

### Task 1: Strict policy update pipeline

**Files:** Domain/Offline/OfflinePolicyUpdateContracts.cs; Command/Offline/SetOfflinePolicyCommand.cs, SetOfflinePolicyValidator.cs, SetOfflinePolicyHandler.cs, SetOfflinePolicyOutput.cs, OfflinePolicyUpdateMessages.cs; Data/Offline/OfflinePolicyUpdateStore.cs; AccountController.cs, Startup.cs; corresponding Command/Data/WebApi tests.

**Interfaces:** UpdateAsync(OfflinePolicyUpdateRequest(Guid IdentityId, string Verifier, long ExpectedPolicyRevision, bool RenewalEnabled, TimeSpan? RenewalInterval), CancellationToken) returns OfflinePolicyReadResult/OfflinePolicySnapshot. Handler consumes SetOfflinePolicyCommand with trusted SetContext(identity,handle) and produces SetOfflinePolicyOutput: renewalEnabled, renewalInterval, policyRevision, revocationGeneration.

- [x] Write HTTP success test with expectedPolicyRevision1, interval00:00:01; require 200/new revision2 and unchanged content. Run and observe absent PUT405.
- [x] Add handler/validator and real PostgreSQL tests for valid 1-second/24-hour/arbitrary whole-second durations, disabled-null, malformed/unsupported/overflow input400, missing identity/access401, stale/revoked/foreign/invalid-profile access403, absent/hidden account404, stale/exhausted policy409 and dependency/corruption503. Assert all denied operations preserve persisted state and disclose no policy/content.
- [x] Implement strict required command body, stable messages and marker-compatible CommandOutput. Handler validates returned policy/counters against the request and preserves caller cancellation.
- [x] Implement metadata-only initial snapshot, account->session->optional profile locks, fresh snapshot rechecks and one guarded policy UPDATE under a transaction. Use statement_timestamp() after waits, capture immutable identity/selection/counters, check postconditions before commit, fail closed on storage faults. No ciphertext/key material is read.
- [x] Wire strict native JSON body middleware, media parameters, no-query/duplicate-header guard, route and DI. Run focused families; expected zero failures/skips. Commit implementation/tests.

### Task 2: Races, contract and verified delivery

**Files:** OfflinePolicyUpdateStoreConcurrencyTests.cs, offline-policy-api.md, OpenAPI, foundation-status, changelog and README.

**Interfaces:** Exercise Task1 UpdateAsync; document the exact public route/settings/version response and old-session invalidation.

- [x] Write real concurrency tests: two same-policy updates yield one winner; held account/session/profile locks followed by revocation, profile trash or positive policy/revocation change deny/conflict without mutation. Add before/at/after-expiry and post-write fault rollback cases. Run/fix focused suite.
- [x] Regenerate OpenAPI and document whole-second TimeSpan support, nullable disabled input, required fields, policy version CAS, fresh access after update, stable errors and last-issued disconnected policy. Update changelog/status.
- [ ] Run locked restore/audit/build, full unfiltered Release suite with coverage, helper/spec/OpenAPI/native corpus and Docker checks; require all pass. One fresh whole-change reviewer must have no unresolved Critical/Important findings.
- [ ] Update issue47 README status/count, push/open one PR, wait exact-head required CI and merge normally under recorded authorization. Verify issue/project/branch closeout and clean synchronized develop before UC47.
