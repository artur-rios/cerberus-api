### Task 3: HTTP deletion and verified delivery

**Files:** Modify src/Presentation/ArturRios.Cerberus.WebApi/Controllers/ProfilesController.cs, Startup.cs, README.md, CHANGELOG.md, docs/contracts/openapi.json; create ProfileTrashHttpTests.cs and docs/operations/profile-trash.md.
**Interfaces:** DELETE /api/profiles/{id}, strictbodyexpectedRevision, singleaccessheader;200metadata6fields or400/401/403/404/409/503.
- [ ] Write actualHTTP/PG/Heimdall tests for owned/scoped deletion and retainedbytes/operation/queue/no-store, subsequentread/list/selected revocation, unrelatedpreservation, hidden404/currentpermission, strictpath/query/header/body, identityoutage/persistencefailure, stale/retry/conflict. Run; expected missingDELETE405.
- [ ] Implement VaultProofBody endpoint/trustedcontext/DI; focusedHTTPexpected pass0skip.
- [ ] Update operations/backlog/Unreleased changelog (include UC12 catch-up dueparallelnewpolicy), actualOpenAPI generation/drift/helper/spec/diff. Fullunfiltered includes wholeWeb;0fail/0skip >=90%line, branches reported. Commit.
**Completion:** allflows implemented, explicit futurepurge/association obligations; oneordinaryfinalreview, oneTDDfixpass ifblocking; strictchecked requiredCIgate thennormalmerge issue14Done/archive/UC14.
