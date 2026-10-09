### Task 2: Deliver protected account query

**Files:** Query `Accounts/GetAccountQuery.cs`, `GetAccountHandler.cs`, output/status map; WebApi `AccountController.cs`, `Startup.cs`; Query tests and WebApi account-read HTTP tests; OpenAPI/README/operational docs.

**Interfaces:** server-created `GetAccountQuery(Guid identityId,string? vaultAccess):BaseQuery`; `IQueryHandlerAsync<GetAccountQuery,AccountOutput>`; public output ID/revision/state/EncryptedEnvelope details.

- [ ] Add unit tests for missing/invalid handles/actor, store outcomes, public projection and corrupt metadata; observe RED, implement using TimeProvider and Domain store, run Query suite GREEN.
- [ ] Add real-host tests for main/every AF, missing/current denied identity, header/query/body validation, isolation, expired/revoked/profile session and actual database outage; observe absent route RED. Implement trusted-actor/header extraction, QueryMediator/DI/status mapping; run WebApi suite GREEN.
- [ ] Generate/inspect OpenAPI, mark only UC-03 done and update milestone progress. Run fresh unfiltered coverage.py/helper/spec/contract/format checks: expect zero failures/skips and >=90% line coverage.
- [ ] One fresh final review; reproduce/fix Important findings once, ledger rulings/minors/declines. Open own PR closing #4; required CI green, merge/close/projectDone/delete feature branch/sync under existing authorization.
