### Task 1: Single-snapshot authorized profile read

**Files:** Create Domain/Profiles/ProfileReadContracts.cs, Data/Profiles/ProfileReadStore.cs; tests/Infrastructure/ArturRios.Cerberus.Data.Tests/ProfileReadStoreTests.cs.
**Interfaces:** Consumes existing ProfileListRow. Produces ProfileReadRequest(Actor,AccessVerifier,ProfileId), ProfileReadDetails(Profile,RecordIds,FolderIds,CollectionIds), IProfileReadStore.ReadAsync(request,ct)->VaultResult<ProfileReadDetails>.
- [ ] Write PG tests for exact owned ciphertext and empty links, foreign/trash/erasure/notfound, valid selection/escape/dangling/foreign/trashed scope, all current account/session policy states, outage and cancellation. Run new tests; expected missing-store compilation failure.
- [ ] Implement single LINQ SQL statement applying current account/session and visibility predicates before target projection. Propagate cancellation, map persistence failures503.
- [ ] Run whole Data family; expected all pass zero skips; commit.
**Completion:** Every visible/hidden/denied state proven through PostgreSQL with unchanged rows.

