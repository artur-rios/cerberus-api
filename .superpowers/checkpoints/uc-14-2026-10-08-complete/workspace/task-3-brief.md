### Task 3: Nonempty profile create/read/list/trash integration

**Files:** Modify Data/Profiles/{ProfileCreateStore,ProfileReadStore,ProfileListStore,ProfileTrashStore,ProfileAssociationVisibility}.cs, create Data/Profiles/ProfileProjectionQuery.cs, Domain/Profiles/ProfileListContracts.cs, Query/Profiles/{ProfileListOutput,ProfileProjection}.cs; tests/Data/ProfileAssociationIntegrationTests.cs, Query tests and existing list HTTP shape assertion.
**Interfaces:** Consumes resolver/visibility; ProfileListRow and ProfileListItem gain nonnull init RecordIds/FolderIds/CollectionIds arrays default[], get Details keeps its arrays. Creation resolves actual sets. Trash snapshots all stored IDs and deletes links in existing transaction.
- [ ] Write failing integration tests actual create with owned/shared sets then get/list same-snapshot visible IDs; revoke/trash/closing/terminal omits links; selectedscope unchanged; invalid list arrays fail503. Actual trash retains underlying bytes/owners/grants and otherprofilelinks while snapshotting actual IDs; failure rolls back link deletion. Runfocused; expected old empty projection/unresolved404 failures.
- [ ] Integrate create admission/finalcurrentvisibility, EF single-query ID projections without N+1, list typed arrays/validation; trash actual snapshot/deactivation. Update existing strict list response expectation to additive9fields. Run wholeData/Query; expected allpass0skip. Commit.
**Completion:** No fake empty links remain and deletion preserves underlying content.

