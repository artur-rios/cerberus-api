### Task 3: HTTP update and completion evidence

**Files:** Modify src/Presentation/ArturRios.Cerberus.WebApi/Controllers/ProfilesController.cs, Startup.cs, README.md, docs/contracts/openapi.json; create tests/Presentation/ArturRios.Cerberus.WebApi.Tests/ProfileUpdateHttpTests.cs, docs/operations/profile-update.md.
**Interfaces:** PUT /api/profiles/{id}, strict bodyexpectedRevision/envelope/editedAt, accessheader;200metadata4fields or400/401/403/404/409/503.
- [ ] Write HTTP actual PG/Heimdall tests: native owned and selected success; hidden/currentstate failures, malformed canonicalroute/body/query/header, rawemptyaccess, strict unknown/duplicates/numericstrings, unsupportedcontent/epoch; concurrency/retry; literal pre2000 precision; dependency outage and no-store/no mutation. Run; expected missingPUT405.
- [ ] Implement VaultProofBody boundedstrict endpoint, trustedcontext and DI; focusedHTTP expected all pass.
- [ ] Update operations/backlog/OpenAPI; helpers/spec/drift/diff pass. Full unfiltered coverage includes wholeWeb;0fails/skips >=90%, branches reported. Commit.
**Completion:** allUC12flows complete; one ordinary final review, one TDD correctionpass if blocking; required exactheadCI then authorized merge/issueDone/archive/UC13.
