### Task 3: Strict PUT refresh and named fresh recovery read

**Files:** shared RecoverVaultCommand/Validator/Handler, challenge validator/handler, VaultController; Command.Tests/VaultRecoveryRefreshHandlerTests.cs; WebApi.Tests/VaultRecoveryRefreshHttpTests.cs; docs/README/OpenAPI.
**Interfaces:** existing command SetContext adds optional access=null internal; handler hashes access only for refresh; same six public fields/four output. POST checks operation recover; PUT checks refresh-recovery. GET recovery-material shares GetVaultProtectionQuery projection and strict GET body/query checks, requires fresh current identity.
- [ ] Command context/access/purpose/hash/projection/error/cancel/challenge generation tests RED; implement full Command GREEN.
- [ ] HTTP missing PUT/read RED; implement native refresh→old recovery denial→new unlock/recovery, exact retry, AFs/strictness/current and fresh identity, read no consumption/session. Full WebApi GREEN.
- [ ] Docs/README UC41Done/M02 9/9, supporting UC40 read correction; generate/inspect OpenAPI; helpers/spec/drift/diff/full unfiltered coverage; commit/ledger.
- [ ] One final ordinary review/blocker correction pass if needed; required CI/pinned merge/issueDone/branch absence/tested tree equality/archive/sync.
