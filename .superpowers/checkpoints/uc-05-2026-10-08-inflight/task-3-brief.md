### Task 3: Deliver protected identity command and route

**Files:** Command `Identity/UpdateIdentityCommand.cs`, validator/handler/output/statuses; WebApi IdentityController/Startup; Command unit tests; WebApi identity-update HTTP tests and controlled Heimdall fixture extensions; OpenAPI/README/operational docs.

**Interfaces:** strict command body Name/Email, server-only actor/token/access via SetActor; output public Id/Name/Email/EmailVerified; handler uses authorized store callback with typed adapter.

- [ ] Write handler/validator tests for input and trusted context, callback sequencing, success/errors/incomplete results/cancellation. Observe RED; implement, run Command suite GREEN.
- [ ] Write real-host tests for main/all five AFs, strict body/header/query rejection, owned access isolation, provider route/body/auth binding and failure/conflict contracts, name/email verification reset, and unchanged local vault state. Observe absent-route RED; implement controller/DI/controlled fixture, run full WebApi suite GREEN.
- [ ] Generate/inspect OpenAPI, mark only UC05 done and milestone5/9. Run fresh unfiltered coverage.py/helper/spec/contract/diff checks; report line>=90% and branch. Commit and ledger.
- [ ] One fresh final code review and one RED→GREEN correction pass if needed; open own PR closing #6, wait required CI green, merge/close/projectDone/verify remote branch deletion/sync/archive under existing authorization.
