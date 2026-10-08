# UC12 Update Profile Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace owned profile ciphertext with atomic expected-revision concurrency and synchronization metadata.
**Architecture:** Dedicated locked transactional store, strict command validation, bounded raw HTTP boundary. Immutable wrappers/ownership and current native epoch remain unchanged.
**Tech Stack:** .NET10, EFCore10, PostgreSQL18.6, first-party mediator/output.
**Spec:** docs/superpowers/specs/2026-10-08-uc-12-update-profile.md

## Global Constraints

- Isolated feature/uc-12-update-profile, issue13, onePR into develop; preserve primary user files.
- No plaintext content/keys; no-store. Canonical native envelope, counters1..9007199254740991.
- UTC timestamp normalized to microseconds BEFORE binding; statement_timestamp after locks.
- Full unfiltered suite0fail/0skip >=90% production line; report branch coverage.
- Independent approvals development-only deferred; strict release gate unchanged.

## Review Focus

- Expiry during account/session/profile lock waits or immediately before write must deny without mutation.
- Concurrent same-revision edits and lost-response retry preserve exactly one winner.
- Pre2000 submicrosecond timestamps must not cause503 after committed update.
- Selected/foreign/trashed/erased targets must fail without disclosing ciphertext or revision.
- Wrapper grantRevision can lag content revision; repeated updates preserve valid wrappers, epochs and ordering.

### Task 1: Atomic profile content update

**Files:** Create src/Domain/ArturRios.Cerberus.Domain/Profiles/ProfileUpdateContracts.cs, src/Infrastructure/ArturRios.Cerberus.Data/Profiles/ProfileUpdateStore.cs, tests/Infrastructure/ArturRios.Cerberus.Data.Tests/ProfileUpdateStoreTests.cs; modify VaultProtectionChangeStore.cs and profile rotation regression tests.
**Interfaces:** Produces ProfileUpdateInput(long ExpectedRevision,EncryptedEnvelope Envelope,DateTimeOffset EditedAt).IsValid(), ProfileUpdateRequest(Guid Actor,string AccessVerifier,Guid ProfileId,ProfileUpdateInput Input), IProfileUpdateStore.UpdateAsync(request,ct)->VaultResult<ProfileCreateDetails>. Existing ProfileCreateDetails metadata reused.
- [ ] Write real PG tests: two modes exact opaque update/repeated revision2→3 with immutable wrappers/account/session; all hidden/currentaccount/session states and selected valid/escape/dangling/foreign/trash/erased; stale/concurrent/retry; invalidinput/epoch/storage/counters; before/at/post2000 literal timestamp; expiry3locklocations and prewrite; rollback/outage/cancel/exhaustion. Run focused; expected missing-store compile failure.
- [ ] Implement strict input and account→session→profile locks, current auth/visibility then revision/native stored metadata. Final parameterized UPDATE uses statement_timestamp and expectedrevision; nextval global sequence. Fetch/check returned metadata beforecommit; map failures/rollback.
- [ ] Add regression for native complete rotation after content update; existing wrapper grantRevision <=profile.Revision, verify exact signed wrapper revision, while replacement binds new profile.Revision+1. Run whole Data family; expected all pass zero skips. Commit.
**Completion:** main/AF01–05 persistence proven, no partial state or forbidden wrapper change.

### Task 2: Command and defensive metadata projection

**Files:** Create src/Application/ArturRios.Cerberus.Command/Profiles/UpdateProfile{Command,Validator,Handler,Output,Messages}.cs; tests/Application/ArturRios.Cerberus.Command.Tests/UpdateProfileHandlerTests.cs.
**Interfaces:** Consumes IProfileUpdateStore. Produces UpdateProfileCommand(ExpectedRevision,Envelope,EditedAt; SetContext(actor,access,profileId)), UpdateProfileHandler validator/store, UpdateProfileOutput metadata4fields, UpdateProfileMessages maps200/400/401/403/404/409/503.
- [ ] Write trustedcontext/input/counter/time tests; successful output and correct hashedaccess/route/request; errorforward, cancellation, null/corrupt/substituted metadata503 with no data including literal pre2000 normalized result. Run; expected missing-command compilation failure.
- [ ] Implement strict JSON and validator, failclosed handler; require newrevision expected+1, canonicalpositive sequence/UTC microseconds and normalized inputtime match. Never accept owner/scope/profile from body.
- [ ] Run whole Command family; expected all pass zero skips. Commit.
**Completion:** strict input and no partial bad dependency output.

### Task 3: HTTP update and completion evidence

**Files:** Modify src/Presentation/ArturRios.Cerberus.WebApi/Controllers/ProfilesController.cs, Startup.cs, README.md, docs/contracts/openapi.json; create tests/Presentation/ArturRios.Cerberus.WebApi.Tests/ProfileUpdateHttpTests.cs, docs/operations/profile-update.md.
**Interfaces:** PUT /api/profiles/{id}, strict bodyexpectedRevision/envelope/editedAt, accessheader;200metadata4fields or400/401/403/404/409/503.
- [ ] Write HTTP actual PG/Heimdall tests: native owned and selected success; hidden/currentstate failures, malformed canonicalroute/body/query/header, rawemptyaccess, strict unknown/duplicates/numericstrings, unsupportedcontent/epoch; concurrency/retry; literal pre2000 precision; dependency outage and no-store/no mutation. Run; expected missingPUT405.
- [ ] Implement VaultProofBody boundedstrict endpoint, trustedcontext and DI; focusedHTTP expected all pass.
- [ ] Update operations/backlog/OpenAPI; helpers/spec/drift/diff pass. Full unfiltered coverage includes wholeWeb;0fails/skips >=90%, branches reported. Commit.
**Completion:** allUC12flows complete; one ordinary final review, one TDD correctionpass if blocking; required exactheadCI then authorized merge/issueDone/archive/UC13.
