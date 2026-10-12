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

- Status: pending.

### Task 2

- Status: pending.

### Task 3

- Status: pending.

### Final review and delivery

- Status: pending.

