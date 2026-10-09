### Task 1: Validate encrypted registration input

**Files:** Domain `Accounts/EncryptedEnvelope.cs`; Command `Accounts/RegisterAccountCommand.cs`, `RegisterAccountValidator.cs`; mirrored Domain/Command tests.

**Interfaces:** `EncryptedEnvelope.IsValid()`; `RegisterAccountCommand : BaseCommand` with account/operation GUIDs, identity registration input and encrypted details; `RegisterAccountValidator : AbstractValidator<RegisterAccountCommand>`.

- [ ] Write literal valid/invalid envelope and registration validation tests, including unknown JSON members, canonical encoding, exact nonce/tag lengths, invalid identifiers and credential shape.
- [ ] Run the new tests; expected missing behavior fails before implementation.
- [ ] Implement strict metadata validation without decrypting content and validator rules matching Heimdall's name/email/password contract.
- [ ] Run Domain and Command tests; expected all pass. Commit the verified task.

