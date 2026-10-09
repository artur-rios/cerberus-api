### Task 2: Encrypted cursor and query handler

**Files:** Create Query/Profiles/ProfileListCursor.cs, ListProfilesQuery.cs, ListProfilesHandler.cs, ProfileListOutput.cs, ProfileListMessages.cs; tests/Application/ArturRios.Cerberus.Query.Tests/ListProfilesHandlerTests.cs.
**Interfaces:** Consumes IProfileListStore contracts; produces ListProfilesQuery(actor,access,pageSize?,cursor?), ListProfilesHandler and ProfileListOutput(Items,NextCursor), ProfileListCursor protected actor/verifier/size/after/boundary tuple.
- [ ] Write query tests for context/bounds/cursor tampering+cross-context, full continuation/end, no partial output on corrupt stored data, cancellation and errors. Expected: missing query compilation failure.
- [ ] Implement purpose-separated encrypted cursor, strict pre-store validation and fail-closed opaque output validation.
- [ ] Run whole Query project; expected zero failures/skips. Commit.
**Completion:** All cursor/context/shape/sequence guards behaviorally covered.

