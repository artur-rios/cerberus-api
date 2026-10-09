### Task 2: Shared profile projection and get query

**Files:** Create Query/Profiles/ProfileProjection.cs, GetProfileQuery.cs, GetProfileHandler.cs, ProfileDetailsOutput.cs, ProfileReadMessages.cs; modify ListProfilesHandler.cs to use shared validator; tests/Application/ArturRios.Cerberus.Query.Tests/GetProfileHandlerTests.cs.
**Interfaces:** Consumes IProfileReadStore and ProfileListRow. Produces GetProfileQuery(actor,access,profileId), GetProfileHandler/DataOutput<ProfileDetailsOutput>; shared ProfileProjection.TryRead(row,actor,out item) preserves UC10 invariants.
- [ ] Write query tests for trusted context, target substitution, complete opaque data/link shape, corrupt envelope/wrapper/recipient/counters/time/links, store failure and cancellation. Run; expected missing query compile failure.
- [ ] Extract existing metadata validation under existing UC10 tests, implement get query/output and strict link IDs. Current concrete store has empty links.
- [ ] Run whole Query family including UC10; expected all pass zero skips; commit.
**Completion:** Current and new query paths share identical visible metadata guards, no partial output.

