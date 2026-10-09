### Task 3: Strict HTTP endpoint and delivery evidence

**Files:** Modify WebApi/Controllers/ProfilesController.cs, Startup.cs; create tests/Presentation/ArturRios.Cerberus.WebApi.Tests/ProfileListHttpTests.cs; modify contracts/openapi.json, README.md, docs/operations/profile-listing.md.
**Interfaces:** Consumes query/handler/store; produces GET /api/profiles with single pageSize/cursor/access header and no body, 200/400/401/403/404/503.
- [ ] Write HTTP tests against real PostgreSQL+Heimdall for main flow, continuation/empty, foreign/trash omission, scoped access, stale access, invalid parameters/headers/body, no-store, provider/storage outage. Expected: GET405 before implementation.
- [ ] Add endpoint/DI, strict decimal parser and query allowlist; document cursor/page semantics; update backlog. Run whole Web project; expected zero failures/skips.
- [ ] Generate/check OpenAPI, helper/spec/diff checks, run full unfiltered coverage suite and audit zero failures/skips >=90%; report branch coverage. Commit.
**Completion:** Every main/AF flow implemented with fresh full evidence. One final ordinary review, one blocking correction pass, required green CI, authorized merge and issue Done then next UC.
