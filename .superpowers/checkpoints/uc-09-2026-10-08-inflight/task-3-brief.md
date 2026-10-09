### Task 3: Strict command/HTTP contract and delivery

**Files:** Command/CreateProfile command/validator/handler/output; WebApi/ProfilesController and DI; Command.Tests/ProfileCreateHandlerTests.cs; WebApi.Tests/ProfileCreateHttpTests.cs; docs/README/OpenAPI.
**Interfaces:** seven public command fields matching ProfileCreateInput; trusted internal actor/access SetContext; handler hashes opaque access; output four profile metadata fields; POST /api/profiles with single access header.
- [ ] Command validation/context/error projection RED then GREEN. HTTP absent route RED then strict native owned creation, every AF/current provider/no-store/schema/body/query, one-winner concurrency GREEN.
- [ ] Update docs/README; generate/inspect OpenAPI; full unfiltered suite/coverage/helpers/spec/drift/diff, commit/ledger.
- [ ] Single final ordinary reviewer/fix pass if needed; required CI/pinned merge/issueDone/branch absence/tested tree/archive/sync.
