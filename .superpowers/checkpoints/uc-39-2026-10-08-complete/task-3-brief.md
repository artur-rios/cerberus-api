### Task 3: Typed change handler and protected PUT contract

**Files:** Command/Protection/ChangeVaultProtectionCommand.cs/Handler.cs/Validator.cs/Output.cs; existing challenge validator/handler/maps; WebApi VaultController.cs/Startup.cs; Command.Tests/VaultProtectionChangeHandlerTests.cs; WebApi.Tests/VaultProtectionChangeHttpTests.cs; OpenAPI/docs/README.
**Interfaces:** public command mirrors Task1 ProtectionChange six fields; internal actor/access/challenge/proof/raw set via SetContext; output Task1 confirmation five fields. Handler hashes canonical handle and calls store. Challenge operation new exact change-protection supported; outputs must match command purpose.
- [ ] Write success/error/incomplete output/trusted context/validator/cancel handler tests; observe RED, implement, full Command GREEN.
- [ ] Write real host main init/unlock/challenge/change/re-unlock/account-read and all AFs; absent PUT RED. Implement strict header/query/raw body binding, DI, maps200/400/401/403/404/409/503. Full WebApi GREEN.
- [ ] Document complete inventory/password-slot vs content epochs/retry/no copied-key revocation, generate/inspect OpenAPI; README UC39 Done M02 7/9. Full unfiltered coverage/helpers/spec/OpenAPI drift/diff checks; commit/ledger.
- [ ] One final ordinary review, one TDD correction pass for blockers, own PR closing40, all CI green, authorized merge/issueDone/archive/sync.
