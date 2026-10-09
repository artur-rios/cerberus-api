### Task 1: Native refresh transition contracts

**Files:** Domain/Protection/RecoveryReplacement.cs; Domain.Tests/RecoveryReplacementTests.cs.
**Interfaces:** existing RecoveryReplacement.IsValid accepts recover/refresh-recovery; existing RecoveryRequest adds optional string? AccessVerifier=null after Replacement. Existing callers compile unchanged. Pure transition validation/material replacement shared.
- [ ] Write refresh full-wrapper/pin/generation/context tests and unsupported-operation regression; observe refresh rejected RED.
- [ ] Extend strict contracts, full Domain GREEN, commit/ledger.

