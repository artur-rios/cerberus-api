# SDD ledger - plan: docs/superpowers/plans/2026-10-12-uc-34-set-collection-membership.md

## Preflight

| Task | Implementer model | Review model | Shared interfaces/files | Baseline dependency |
| --- | --- | --- | --- | --- |
| 1 - contracts/command | gpt-6.1-sol high | gpt-6.1-sol high | Defines `CollectionMembership*` used by Tasks 2/3 | UC33 merged tree; Command baseline green |
| 2 - transactional store | gpt-6-astra xhigh | gpt-6.1-sol high | Consumes Task 1; owns `CollectionMembershipStore` | Task 1 committed/reviewed; Data baseline green |
| 3 - HTTP/docs/full verification | gpt-6-astra xhigh | gpt-6.1-sol high | Consumes Tasks 1/2; shared controller, Startup, docs/OpenAPI | Tasks 1/2 committed/reviewed; Web/native baseline green |
| Final whole-branch review | gpt-6-astra ultra | n/a | Reviews original base through tested Task 3 head | All tasks committed/reviewed; full evidence recorded |

Tasks are strictly sequential. No two implementers run concurrently. Each reviewer
receives the task base/head diff and test evidence; the final reviewer is fresh.

## Baseline

- Original base: `1283500c1614521906fd5c842405b230a1f06f0e`
- Original tree: `043164c43dc0b6694e93c9255640d6181dd382a0`
- Inherited UC33 evidence: 8,248 tests, zero failures/skips; 98.4% line and
  92.4% branch coverage; every production assembly above 90% line; Maven 173,
  OSV 153/0, corpus 322 across four runtimes, helpers 36 + 23; exact-head required
  CI successful and merged tree identical.
- Branch: `feature/uc-34-set-collection-membership`
- Project item `PVTI_lAHOAgOUtM4BmFmuzg_Lyug` moved to In Progress.

## Rulings

1. Same-epoch collection envelope is the atomic opaque client key-manifest boundary.
2. Active grant wrappers are validated and preserved, not revision-bumped.
3. Only direct symmetric-difference members receive structural bumps.
4. Selected access may retain/promote already effective members but cannot introduce
   any item outside its current effective scope.

## Task evidence

### Task 1

- Status: implemented, verified, committed and independently approved.
- Base: `0ce71e7`.
- Discovery: escalated `dotnet test tests/Application/ArturRios.Cerberus.Command.Tests/ArturRios.Cerberus.Command.Tests.csproj --no-restore --list-tests --filter FullyQualifiedName~UpdateCollectionHandlerTests --verbosity minimal`, exit 0; existing collection tests listed successfully.
- RED: focused `dotnet test tests/Application/ArturRios.Cerberus.Command.Tests/ArturRios.Cerberus.Command.Tests.csproj --no-restore --filter FullyQualifiedName~SetCollectionMembershipHandlerTests --verbosity minimal`, exit 1 with seven CS0246 errors for missing membership types, before any new production code.
- Task 1: Ruling: preserve the existing `ledger.md` and parent-controlled sequential task lifecycle rather than introducing skill-script `progress.md` bookkeeping - coordinated record and explicit Task 1-only brief govern - cost if wrong: automation expecting the skill's default filename would need adaptation.
- Windows sandbox .NET attempt returned exit 1 without test execution; approved narrowly scoped `require_escalated` .NET execution provides actual discovery/RED evidence.
- GREEN: focused Command test command above passed 110/110, zero failures/skips; complete `dotnet test tests/Application/ArturRios.Cerberus.Command.Tests/ArturRios.Cerberus.Command.Tests.csproj --no-restore --verbosity minimal` passed 1,437/1,437, zero failures/skips.
- Changes: strict five-field Domain/Command input, hashed trusted context, safe-error allowlist, fail-closed null/error-plus-data/mismatched results, safe counters, matching microsecond UTC edit time, distinct typed membership equality and sorted exact six-field output. Store/HTTP/Startup untouched.
- Verification: `git diff --check` passed; exact staged diff inspected before commit.
- Task 1 implementation: complete (base `0ce71e7`; commit
  `feat: define collection membership contract`; tests above).
- Independent review: approved exact range `0ce71e7..3d7c157`; Critical 0,
  Important 0, Minor 0. Reviewer independently reproduced 110 focused and 1,437
  complete Command tests with zero failures/skips, confirmed exact-range whitespace,
  clean scope/worktree and authorized Task 2 to start. Historical RED ordering is
  ledger evidence rather than a retained raw log; no correctness finding resulted.
- Reviewer declined Data authority/grants/locking/transactionality, HTTP integration
  and external-client qualification as correctly belonging to later gates.

### Task 2

- Status: pending.

### Task 3

- Status: pending.

### Final review and delivery

- Status: pending.

