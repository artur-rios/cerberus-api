### Task 3: Strict HTTP retrieval and evidence

**Files:** Modify ProfilesController.cs, Startup.cs, docs/contracts/openapi.json, README.md; create ProfileReadHttpTests.cs and docs/operations/profile-retrieval.md.
**Interfaces:** GET /api/profiles/{id} ->200/400/401/403/404/503 with one access header and no query/body; profile plus3 visible link arrays.
- [ ] Write real HTTP/PG/Heimdall tests for successful owned/scoped reads, foreign/hidden same404, stale account/session, strict GUID/query/header/body, provider/table outage, corrupt stored metadata, no-store and no mutations. Run; expected GET404 for missing route.
- [ ] Add endpoint/DI, raw canonical path validation and strict request guards. Run targeted HTTP tests; expected pass zero skips.
- [ ] Update docs/backlog and generate/check OpenAPI; helper/spec/diff checks. Run full unfiltered coverage suite including whole Web family; audit zero failures/skips >=90%, branches reported. Commit.
**Completion:** Main/AF01–04 complete, fresh evidence; one ordinary review and one correction pass if needed; requiredCI then authorized merge/issueDone, continueUC12.
