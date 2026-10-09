### Task 3: Validated mediator/HTTP delivery

**Files:** Command `Authentication/` login/challenge commands, validators, handlers/output/status map; WebApi `Controllers/AuthController.cs`, `Startup.cs`; Command handler/validator tests; WebApi `AuthenticationHttpTests.cs` and controlled identity fixture; generated OpenAPI/README.

**Interfaces:** `LoginCommand : BaseCommand` email/password; `VerifyChallengeCommand : BaseCommand` challengeToken/code/recoveryCode; both handlers return `DataOutput<AuthenticationOutput?>` with `HeimdallLogin Identity` and nullable `AuthenticationAccount Account`; adapter outcome selects store lookup only for completed identity.

- [ ] Write validator/handler tests covering invalid input, pending MFA no store calls, all stable errors, unbound/active/inactive accounts and both authentication modes. Run: expected missing behavior fails; implement and run full Command suite green.
- [ ] Add real-host tests for both routes and every AF, including malformed/duplicate bodies before calls, generic credential denial, inactive/erased accounts, controlled upstream failures and database failure. Run: expected routes absent; implement mediator dispatch/DI/status mapping. Run WebApi suite: expected green.
- [ ] Generate OpenAPI, inspect both routes and public schemas; mark UC-02 done in branch README. Run fresh unfiltered coverage.py, helper/spec/OpenAPI/format checks: expected zero failures/skips and coverage>=90%.
- [ ] One fresh whole-branch review; reproduce/fix important issues, record all rulings/declines/minors. Push/open PR closing #3; green CI, merge, close issue/set Done/delete branch/sync under batch authorization.
