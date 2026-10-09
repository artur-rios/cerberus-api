### Task 4: Deliver HTTP registration

**Files:** WebApi `Controllers/AccountController.cs`, `Startup.cs`; WebApi registration functional tests with controlled HTTP Heimdall and real PostgreSQL; `docs/contracts/openapi.json`; README UC-01 row.

**Interfaces:** Anonymous `POST /api/accounts` passes optional bearer proof through the command mediator; stable DataOutput statuses 200/201/400/401/403/404/409/503.

- [ ] Write real-host success and every AF test, including no-store, credentials redaction, persisted ciphertext and pending-operation retry. Observe failures, then implement route/DI/status map.
- [ ] Generate and inspect OpenAPI with `python3 scripts/openapi.py --write`; mark only UC-01 done in branch README.
- [ ] Run `dotnet test src/ArturRios.Cerberus.sln --configuration Release --logger trx` unfiltered, `python3 scripts/coverage.py`, helper/spec/OpenAPI checks and applicable dependency/harness checks. Expected zero failures/skips and coverage at least 90%.
- [ ] Perform one fresh whole-branch code review; fix important findings with observed red/green tests. Push/open PR closing #2, require green CI, merge, close issue, delete feature branch and sync base under batch authorization.
