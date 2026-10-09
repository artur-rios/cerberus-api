### Task 1: Scoped single-snapshot profile read store

**Files:** Create Domain/Profiles/ProfileListContracts.cs and Data/Profiles/ProfileListStore.cs under their existing src projects; tests/Infrastructure/ArturRios.Cerberus.Data.Tests/ProfileListStoreTests.cs.
**Interfaces:** Produces ProfileListRequest(Actor,AccessVerifier,PageSize,After,Boundary?), ProfileListRow(ProfileId,Revision,ServerSequence,EditedAt,Envelope,KeyWrappers), ProfileListPage(Items,Boundary,HasMore); IProfileListStore.ListAsync(request,ct) -> VaultResult<ProfileListPage>.
- [ ] Write real PostgreSQL tests for ownership/trash/erasure before pagination, stable boundary/gaps, selected-session restrictions, current account/session states, no-expiry policy, unavailable persistence and cancellation. Expected: missing contracts/store compilation failure.
- [ ] Implement single LINQ SQL snapshot with account state/access status + only permitted ordered bounded rows and boundary/lookahead; map dependency failures, propagate cancellation.
- [ ] Run whole Data project; expected zero failures/skips. Commit.
**Completion:** Data project green; page contents, winning persisted state and filtering assertions.

