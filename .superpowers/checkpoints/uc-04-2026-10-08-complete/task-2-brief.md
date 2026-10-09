### Task 2: Deliver protected update command and HTTP contract

**Files:** Command `Accounts/UpdateAccountCommand.cs`, validator/handler/output/status map; WebApi AccountController/Startup; Command handler tests; WebApi AccountUpdateHttpTests; OpenAPI/README/operational docs.

**Interfaces:** strict `UpdateAccountCommand:BaseCommand` body `ExpectedRevision` + `Details`; server-only actor/access injected through `SetAccess(Guid,string?)`; `ICommandHandlerAsync<UpdateAccountCommand,UpdateAccountOutput>`; output only public Id/new Revision.

- [ ] Add unit tests for invalid metadata/revision/identity/access, exact hashed verifier and serialized envelope, success/error mapping; observe RED, implement validator/handler using TimeProvider, run Command suite GREEN.
- [ ] Add real-host HTTP tests covering main plus every AF, strict JSON/header/query input, cross-account/profile/expired/revoked access, missing/inactive/erased accounts, competing/stale revisions, identity outage and actual database failure; observe absent route RED. Implement protected PUT/trusted actor/header injection/DI/statuses; run WebApi suite GREEN.
- [ ] Generate/inspect OpenAPI; mark only UC04 done and milestone4/9. Run unfiltered coverage.py, helper/spec/contract/diff checks with >=90% line coverage, report branch. Commit verified changes and ledger.
- [ ] One fresh final code review; one correction pass with reproduced RED→GREEN if needed. Push/open PR closing #5, wait all required CI green, merge/close/projectDone/verify remote branch deletion/sync/archive ledger under existing authorization.
