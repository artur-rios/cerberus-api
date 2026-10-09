### Task 3: Typed recovery handler and strict POST route

**Files:** Command/Protection/RecoverVaultCommand.cs/Validator.cs/Handler.cs/Output.cs; challenge validator/handler/maps; WebApi VaultController.cs/Startup.cs; Command.Tests/VaultRecoveryHandlerTests.cs; WebApi.Tests/VaultRecoveryHttpTests.cs; OpenAPI/docs/README.
**Interfaces:** command public six fields mirror RecoveryReplacement; internal actor/iat/proof context SetContext; output exact RecoveryDetails four fields. Handler validates before store and validates projected historical counters. Existing signed-iat freshness helper and canonical proof header binding.
- [ ] Write command trusted-context/validation/projection/failure/cancel and recover-challenge generation cases; absent types RED, implement, full Command GREEN.
- [ ] Write real host init→recover→oldkey rejects→newkey recovery and exact lost-response retries, all AFs/current provider checks/strict wire; absent-route RED. Implement marked raw-proof route/DI/statuses200/400/401/404/409/503. Full WebApi GREEN.
- [ ] Generate/inspect OpenAPI, document recovery/retry/historical status/client-secret boundary, README UC40 Done M02 8/9. Full unfiltered coverage/helpers/spec/drift/diff; commit/ledger.
- [ ] One final ordinary review, one TDD correction pass for blockers, own PR closes41/all CI green/authorized merge/Done/archive/sync.
