# UC11 Get Profile Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Retrieve an owned active encrypted profile and only visible relationships by public ID.
**Architecture:** Single-statement database authorization/target projection; shared query metadata validation; strict raw HTTP path/header/body boundary. Existing relationship inventory is empty until later resource entities integrate it.
**Tech Stack:** .NET10, EF Core10, PostgreSQL18.6, first-party mediator/output.
**Spec:** docs/superpowers/specs/2026-10-08-uc-11-get-profile.md

## Global Constraints

- Isolated UC11 branch/issue12/onePR into develop; preserve original user files.
- No plaintext keys/content; no-store, no sensitive logs; foreign profiles never follow collection grants.
- Full unfiltered zero failures/skips, >=90% production line coverage, report branches.
- Independent approvals remain deferred development-only; release gates preserved.

## Review Focus

- Guessed foreign/trashed/erased public IDs must return the same nonrevealing404.
- Valid selected scope cannot escape to another own profile; stale/dangling/foreign selection denies access.
- Malformed/case-variant/zero GUID paths and GET bodies must reject400 rather than routing404 or normalize silently.
- Corrupt recipient/profile/epoch/sequence metadata must fail503 without ciphertext or relationships.
- Future relationship/grant entities must extend visible links before release; current truthful arrays are empty.

### Task 1: Single-snapshot authorized profile read

**Files:** Create Domain/Profiles/ProfileReadContracts.cs, Data/Profiles/ProfileReadStore.cs; tests/Infrastructure/ArturRios.Cerberus.Data.Tests/ProfileReadStoreTests.cs.
**Interfaces:** Consumes existing ProfileListRow. Produces ProfileReadRequest(Actor,AccessVerifier,ProfileId), ProfileReadDetails(Profile,RecordIds,FolderIds,CollectionIds), IProfileReadStore.ReadAsync(request,ct)->VaultResult<ProfileReadDetails>.
- [ ] Write PG tests for exact owned ciphertext and empty links, foreign/trash/erasure/notfound, valid selection/escape/dangling/foreign/trashed scope, all current account/session policy states, outage and cancellation. Run new tests; expected missing-store compilation failure.
- [ ] Implement single LINQ SQL statement applying current account/session and visibility predicates before target projection. Propagate cancellation, map persistence failures503.
- [ ] Run whole Data family; expected all pass zero skips; commit.
**Completion:** Every visible/hidden/denied state proven through PostgreSQL with unchanged rows.

### Task 2: Shared profile projection and get query

**Files:** Create Query/Profiles/ProfileProjection.cs, GetProfileQuery.cs, GetProfileHandler.cs, ProfileDetailsOutput.cs, ProfileReadMessages.cs; modify ListProfilesHandler.cs to use shared validator; tests/Application/ArturRios.Cerberus.Query.Tests/GetProfileHandlerTests.cs.
**Interfaces:** Consumes IProfileReadStore and ProfileListRow. Produces GetProfileQuery(actor,access,profileId), GetProfileHandler/DataOutput<ProfileDetailsOutput>; shared ProfileProjection.TryRead(row,actor,out item) preserves UC10 invariants.
- [ ] Write query tests for trusted context, target substitution, complete opaque data/link shape, corrupt envelope/wrapper/recipient/counters/time/links, store failure and cancellation. Run; expected missing query compile failure.
- [ ] Extract existing metadata validation under existing UC10 tests, implement get query/output and strict link IDs. Current concrete store has empty links.
- [ ] Run whole Query family including UC10; expected all pass zero skips; commit.
**Completion:** Current and new query paths share identical visible metadata guards, no partial output.

### Task 3: Strict HTTP retrieval and evidence

**Files:** Modify ProfilesController.cs, Startup.cs, docs/contracts/openapi.json, README.md; create ProfileReadHttpTests.cs and docs/operations/profile-retrieval.md.
**Interfaces:** GET /api/profiles/{id} ->200/400/401/403/404/503 with one access header and no query/body; profile plus3 visible link arrays.
- [ ] Write real HTTP/PG/Heimdall tests for successful owned/scoped reads, foreign/hidden same404, stale account/session, strict GUID/query/header/body, provider/table outage, corrupt stored metadata, no-store and no mutations. Run; expected GET404 for missing route.
- [ ] Add endpoint/DI, raw canonical path validation and strict request guards. Run targeted HTTP tests; expected pass zero skips.
- [ ] Update docs/backlog and generate/check OpenAPI; helper/spec/diff checks. Run full unfiltered coverage suite including whole Web family; audit zero failures/skips >=90%, branches reported. Commit.
**Completion:** Main/AF01–04 complete, fresh evidence; one ordinary review and one correction pass if needed; requiredCI then authorized merge/issueDone, continueUC12.
