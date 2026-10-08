# UC10 List Profiles Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Return only permitted active profiles through bounded authenticated opaque pagination.
**Architecture:** Domain read contracts; one database snapshot applies authorization and visibility before keyset pagination. Query owns encrypted cursor and validates outputs; HTTP owns strict visible syntax.
**Tech Stack:** .NET10, EF Core10, PostgreSQL18.6, first-party mediator/output, native AES-GCM.
**Spec:** docs/superpowers/specs/2026-10-08-uc-10-list-profiles.md

## Global Constraints

- One UC/branch/issue/PR into develop; preserve original user checkout.
- No plaintext vault content or key material. No-store; no sensitive logging.
- Full unfiltered suite; zero failures/skips; line coverage >=90%; report branch coverage.
- Revalidate current permissions each page, filter before pagination; no unrelated profile leakage.
- Independent security/client approvals remain deferred for development only.

## Review Focus

- Cursor substitution across actors, sessions or page sizes must reveal nothing.
- Current expiry, revocation, trash or selected-scope changes between pages must apply immediately.
- Huge/duplicate/encoded/unknown query inputs and GET bodies must fail predictably.
- Initial boundary excludes later inserts; sequence gaps and inaccessible rows never shorten a permitted page.
- Corrupt stored envelope, wrappers or metadata must return503 without partial payloads.

### Task 1: Scoped single-snapshot profile read store

**Files:** Create Domain/Profiles/ProfileListContracts.cs and Data/Profiles/ProfileListStore.cs under their existing src projects; tests/Infrastructure/ArturRios.Cerberus.Data.Tests/ProfileListStoreTests.cs.
**Interfaces:** Produces ProfileListRequest(Actor,AccessVerifier,PageSize,After,Boundary?), ProfileListRow(ProfileId,Revision,ServerSequence,EditedAt,Envelope,KeyWrappers), ProfileListPage(Items,Boundary,HasMore); IProfileListStore.ListAsync(request,ct) -> VaultResult<ProfileListPage>.
- [ ] Write real PostgreSQL tests for ownership/trash/erasure before pagination, stable boundary/gaps, selected-session restrictions, current account/session states, no-expiry policy, unavailable persistence and cancellation. Expected: missing contracts/store compilation failure.
- [ ] Implement single LINQ SQL snapshot with account state/access status + only permitted ordered bounded rows and boundary/lookahead; map dependency failures, propagate cancellation.
- [ ] Run whole Data project; expected zero failures/skips. Commit.
**Completion:** Data project green; page contents, winning persisted state and filtering assertions.

### Task 2: Encrypted cursor and query handler

**Files:** Create Query/Profiles/ProfileListCursor.cs, ListProfilesQuery.cs, ListProfilesHandler.cs, ProfileListOutput.cs, ProfileListMessages.cs; tests/Application/ArturRios.Cerberus.Query.Tests/ListProfilesHandlerTests.cs.
**Interfaces:** Consumes IProfileListStore contracts; produces ListProfilesQuery(actor,access,pageSize?,cursor?), ListProfilesHandler and ProfileListOutput(Items,NextCursor), ProfileListCursor protected actor/verifier/size/after/boundary tuple.
- [ ] Write query tests for context/bounds/cursor tampering+cross-context, full continuation/end, no partial output on corrupt stored data, cancellation and errors. Expected: missing query compilation failure.
- [ ] Implement purpose-separated encrypted cursor, strict pre-store validation and fail-closed opaque output validation.
- [ ] Run whole Query project; expected zero failures/skips. Commit.
**Completion:** All cursor/context/shape/sequence guards behaviorally covered.

### Task 3: Strict HTTP endpoint and delivery evidence

**Files:** Modify WebApi/Controllers/ProfilesController.cs, Startup.cs; create tests/Presentation/ArturRios.Cerberus.WebApi.Tests/ProfileListHttpTests.cs; modify contracts/openapi.json, README.md, docs/operations/profile-listing.md.
**Interfaces:** Consumes query/handler/store; produces GET /api/profiles with single pageSize/cursor/access header and no body, 200/400/401/403/404/503.
- [ ] Write HTTP tests against real PostgreSQL+Heimdall for main flow, continuation/empty, foreign/trash omission, scoped access, stale access, invalid parameters/headers/body, no-store, provider/storage outage. Expected: GET405 before implementation.
- [ ] Add endpoint/DI, strict decimal parser and query allowlist; document cursor/page semantics; update backlog. Run whole Web project; expected zero failures/skips.
- [ ] Generate/check OpenAPI, helper/spec/diff checks, run full unfiltered coverage suite and audit zero failures/skips >=90%; report branch coverage. Commit.
**Completion:** Every main/AF flow implemented with fresh full evidence. One final ordinary review, one blocking correction pass, required green CI, authorized merge and issue Done then next UC.
