### Task 3: Persist and reconcile registrations

**Files:** Domain `Accounts/Account.cs`, `RegistrationOperation.cs`; Data `Accounts/RegistrationStore.cs`, `AppDbContext.cs`, generated migration; Data registration tests; Command `Accounts/RegisterAccountHandler.cs` and handler tests.

**Interfaces:** Implement `IRegistrationStore` using real PostgreSQL; `RegisterAccountHandler : ICommandHandlerAsync<RegisterAccountCommand, RegisterAccountOutput>` returns `DataOutput<RegisterAccountOutput?>`.

- [ ] Add PostgreSQL tests for preserved ciphertext, unique identity/account/operation constraints, changed-input conflicts, concurrent attempts, terminal identifiers, and failure after upstream creation followed by retry. Observe failures.
- [ ] Implement a durable pending-operation transaction followed by serialized identity establishment and atomic account binding. Key input fingerprints with the configured server secret; store no credentials.
- [ ] Add handler tests for input denial, status mapping and dependency unavailability; observe failures, then implement handler and output.
- [ ] Run Data and Command suites; expected all pass. Commit migration and implementation.

