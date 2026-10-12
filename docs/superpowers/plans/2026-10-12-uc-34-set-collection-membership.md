# UC-34 Set collection membership implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: use
> `superpowers:executing-plans` task by task. Every task is test-first and receives
> a separate implementation review. One fresh whole-branch review follows all tasks.

**Goal:** Atomically replace one owned collection's direct record/folder members
and opaque client key manifest without broadening a selected scope or weakening
current recipient isolation.

**Architecture:** Dedicated Domain/Command contract; guarded owner-only PostgreSQL
transaction with current selected-scope admission, active native-grant validation,
stable collection-before-content locks, symmetric-difference structural bumps and
strict HTTP integration.

**Tech stack:** Existing .NET 10, C#, FluentValidation, Mediator, EF Core, Npgsql,
PostgreSQL, xUnit, Moq and native protocol helpers. No schema/package/algorithm change.

**Spec:** `docs/superpowers/specs/2026-10-12-uc-34-set-collection-membership.md`

## Global constraints

- Exact five-field input: expected revision, same-epoch envelope, editedAt,
  recordIds and folderIds. Exact six-field output with sorted memberships.
- Owner-only. Selected target requires exact PC and every requested member already
  visible in that selection. Valid visible foreign RO/RW returns validated 403.
- Replace direct CR/CF rows atomically with the collection envelope. Bump active
  owned symmetric-difference members once; preserve retained/inherited resources.
- Validate all active target grants and native pins; keep their rows and wrappers
  exact. Do not generate keys or claim external-client decryptability.
- Cover main flow and AF-01 through AF-06, fresh full six-family verification,
  minimum 90% line coverage per production assembly, native audits, one final
  reviewer and exact-head required CI.

## Review focus

- A selected handle must never import a private member or containing folder that was
  not already visible, including direct-record and inherited collection routes.
- All active recipient bindings must validate before effective scope changes, while
  revoked/unrelated evidence cannot poison the owner operation.
- Stable collection/grant/content ordering and final graph equality must prevent
  missed concurrent links, grants or member writers without foreign account locks.
- Only the collection and direct symmetric-difference members may advance; all
  opaque member bytes, hierarchy, other memberships and grant wrappers stay exact.
- Every link/envelope/counter stage must commit together or roll back completely.

### Task 1: Strict collection membership contract and command

**Files:** Create
`src/Domain/ArturRios.Cerberus.Domain/Collections/CollectionMembershipContracts.cs`;
create `SetCollectionMembershipCommand.cs`, validator, handler, output and messages
under `src/Application/ArturRios.Cerberus.Command/Collections`; create
`tests/Application/ArturRios.Cerberus.Command.Tests/SetCollectionMembershipHandlerTests.cs`.

- [ ] Write unit tests first for required fields, typed arrays, envelope/time/revision
  validation, trusted context and handle hashing, cancellation, safe store errors,
  malformed results, exact sorted six-field output and no persistence on rejection.
- [ ] Capture focused missing-type RED with working discovery, then implement the
  contracts and command boundary.
- [ ] Run focused and complete Command tests with zero failures/skips.
- [ ] Commit `feat: define collection membership contract`; update the ledger and
  obtain a separate task review before Task 2.

### Task 2: Guarded membership replacement transaction

**Files:** Create
`src/Infrastructure/ArturRios.Cerberus.Data/Collections/CollectionMembershipStore.cs`;
create
`tests/Infrastructure/ArturRios.Cerberus.Data.Tests/CollectionMembershipStoreTests.cs`.

- [ ] Write real PostgreSQL tests first for wide/selected success, direct typed
  overlaps, retained/promoted effective members, multi-collection preservation,
  exact envelope/link/result metadata and symmetric-difference bumps.
- [ ] Cover foreign/missing/trashed/terminal/incomplete ancestry, selected private
  expansion, record-only container denial, cycles, stale/max counters and precedence.
- [ ] Cover every active native grant, overlapping recipients, malformed required
  wrappers/pins, revoked/unrelated evidence, recipient current visibility, unchanged
  wrapper bytes/revisions and retained rotation inventory.
- [ ] Cover concurrent collection update/delete/protection rotation, profile
  association, record/folder move/trash/update and graph/grant growth in both relevant
  orders; assert stable locks, final guards and bounded query shape.
- [ ] Inject faults after target write, each association delete/insert family and
  every structural bump; prove full rollback, clean retry, outage/internal versus
  caller cancellation and exactly one same-revision winner.
- [ ] Capture missing-store RED, implement the transaction, then run focused and
  complete Data tests with zero failures/skips.
- [ ] Commit `feat: replace collection memberships`; update the ledger and obtain a
  separate task review before Task 3.

### Task 3: HTTP, documentation and complete verification

**Files:** Modify `CollectionsController.cs` and `Startup.cs`; create
`tests/Presentation/ArturRios.Cerberus.WebApi.Tests/CollectionMembershipHttpTests.cs`;
update collection security/operations/status docs, README, CHANGELOG and generated
OpenAPI.

- [ ] Write actual HTTP tests first for wide/selected native success, exact output,
  existing GET/list visibility, recipient changes, foreign 403/hidden 404, strict
  route/query/header/media/body transport, current lifecycle and winner retry.
- [ ] Capture the missing-route RED, add `[HttpPut("{id}/members")]` with
  `VaultProofBody`, explicit store/validator/handler registrations, and run focused
  Web green.
- [ ] Document the exact contract, same-epoch opaque key-manifest boundary, native
  grant preservation, offline limitations and M03 completion; regenerate OpenAPI.
- [ ] Run locked restore/dependency audit, native OSV/corpus/helper suites, specs,
  OpenAPI drift, whitespace, and a fresh unfiltered six-family coverage run. Require
  zero failures/skips and at least 90% line coverage for every production assembly.
- [ ] Commit `feat: expose collection membership replacement`; update the ledger and
  obtain a separate Task 3 review.

## Final review and delivery

- [ ] Build one original-base-to-tested-head package containing this design, plan,
  review priorities, rulings and fresh evidence. Dispatch one fresh whole-branch
  reviewer. Apply at most one blocking TDD correction pass; defer nonblocking minors
  with explicit rationale and cost.
- [ ] Re-run changed-scope and full verification after any correction; require a
  clean worktree and unchanged base assumptions.
- [ ] Push the feature branch, open one PR into `develop` closing issue #35, attach
  it to this chat and move the project item to Testing.
- [ ] Require successful exact-head `branch-policy`, `test` and `docker` checks plus
  clean mergeability and no base drift. Squash-merge normally with head matching.
- [ ] Confirm issue closed/project Done, README M03 26/26, local/remote feature branch
  deletion, tested-tree equality on `develop`, checkpoint preservation and workspace
  cleanup. Then perform the mandatory pre-UC35 recipient-account FK lock audit.

