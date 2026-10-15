# UC50 List Trash Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let owners list their currently recoverable deletion operations with encrypted root metadata and original deadlines.

**Architecture:** Add the existing CQRS query/store/controller boundary for `GET /api/trash`. A deletion operation is the recoverable unit: return its root once, including a folder cascade once; independently deleted items remain separate operations. Selected handles use the retained root associations and current active owned parent/collection scope, following UC22's retained-record authority. A final database statement captures current authorization, expiry, typed terminal exclusions, scope and bounded ciphertext page together. Opaque authenticated cursors bind actor, handle, page size and operation ordering boundary.

**Tech Stack:** Existing .NET 10, PostgreSQL, native envelope protocol, ArturRios mediator/output, xUnit/Moq/Testcontainers. No new dependency or migration.

**Spec:** `docs/requirements/Use Case Specification Document.md` UC50; System Requirements FR-TR-01, sections 4, 5, 7–8; BR10/19/20/22/23; Testing Specification; existing UC22 retained-association authority.

## Global Constraints

- One issue (#51), branch and PR from clean synchronized `develop`; existing unattended authorization covers design, transitions, verified normal merge and owned branch cleanup.
- Full unfiltered suite, fresh production line coverage >=90%, report branch coverage; exact-head required CI checks and independent whole-change review before merge.
- No plaintext or usable keys, no foreign-owner trash, no mutation, no-store on all responses. Expired/terminal content is inaccessible even before physical cleanup.
- Root-only projection preserves the deletion operation as the UC51 restore unit; list exposes no retained association IDs or hidden cascade members.
- Selected-profile root visibility follows original explicit selected-profile links, original links to currently active owned selected collections, or the former parent's complete currently active selected ancestry. Trashed profiles require account-wide access; no historical inactive-container route is invented.
- Account SHARE then own session SHARE stabilize owner topology without foreign locks or network under database locks. Final statement rechecks current database time and all authority after waits. Ciphertext projection happens only after visibility/pagination.
- Strict retained JSON parsing rejects duplicates/unknown members/noncanonical IDs; unusable authority evidence never broadens scope. Missing/invalid selected-scope evidence omits that root. Required visible ciphertext/key corruption returns503 with no partial page.
- Cursor orders by internal operation identity, bounded by captured maximum; it is neither durable sync nor a frozen snapshot. Each page rechecks current scope.

## Review Focus

1. Folder cascade versus independently prior trash: one root per operation and unchanged original deadlines.
2. Selected-profile retained visibility after links are removed, including missing/hidden former parent and foreign collections.
3. Deadline/session expiry during lock waits, current terminal markers, and cursor reuse after revocation.
4. Same GUID across resource kinds; malformed hidden evidence cannot reveal or deny unrelated visible ciphertext.
5. Profile native master-wrapper binding and exact polymorphic HTTP/OpenAPI shape, including empty preauthentication413.

## Flow mapping

| Flow | Implementation and evidence |
| --- | --- |
| Main | Four native root kinds, exact operation ID/deadline, bounded encrypted page; query/data/HTTP tests. |
| AF01 | Strict page/cursor/query/header/body validation400; unsupported visible persisted envelope503; no write. |
| AF02 | Owner-only candidate selection; selected retained authority; expired/typed terminal/missing roots omitted before paging. |
| AF03 | Current human bearer plus vault handle, active owner/session/selection;401/403 and no disclosure. |
| AF04 | Persistence/identity outages503, cancellation propagation and atomic no-partial-page response. |

### Task 1: Query and native trash contract

**Files:** Domain/Trash/TrashListContracts.cs and TrashListItem.cs; Query/Trash/{ListTrashQuery,ListTrashHandler,TrashListCursor,TrashListOutput,TrashListMessages}.cs; mirrored Query.Tests/ListTrashHandlerTests.cs and Domain.Tests/TrashListItemTests.cs.

**Interfaces:** `ITrashListStore.ListAsync(TrashListRequest, CancellationToken)` returns `VaultResult<TrashListPage>` containing typed raw native rows, safe operation positions/boundary and HasMore. Query accepts actor/access/pageSize/cursor, validates native rows and returns `TrashListOutput(Items, NextCursor)`. Four item variants carry kind, operationId, resourceId, revision, serverSequence, editedAt/deletedAt/purgeAt and envelope; collection adds keyEpoch, profile adds encrypted keyWrappers.

- [ ] Write meaningful contract/handler/cursor tests: all kinds; invalid actor/access/page; actor/handle/page/purpose/tamper binding; inconsistent page/order/duplicates/deadlines/native metadata; stable safe errors and cancellation.
- [ ] Run tests and record expected missing-feature RED, implement interfaces and behavior, rerun GREEN.
- [ ] Commit reviewable query/native contract once focused tests pass.

### Task 2: Current owner-visible retained persistence

**Files:** Data/Trash/TrashListStore.cs and TrashListSql.cs; mirrored Data.Tests/TrashListStoreTests.cs (split boundary tests if useful).

**Interfaces:** Consumes Task1 store contracts. Strictly parse retained root associations into safe SQL parameters; final SQL returns one current snapshot of actor/session/scope/native root page and current protection pins. Verify returned profile master wrappers against independently captured owner pins.

- [ ] Write real PostgreSQL tests: all four roots, actual folder cascade plus independently prior trash; retained direct/collection/parent scope; hidden/expired/terminal/foreign omission; typed GUID collision; page boundary/current revocation; invalid current authority; visible native corruption; relevant ancestry; failures/cancellation; expiry after owner/session lock waits; no state mutation.
- [ ] Observe missing-store RED; implement account/session lock ordering and final snapshot, verify GREEN.
- [ ] Commit persistence behavior after focused tests pass.

### Task 3: HTTP contract, documentation and delivery

**Files:** WebApi/Controllers/TrashController.cs; Startup.cs; ContractRegistration.cs; WebApi.Tests/TrashListHttpTests.cs and TrashListContractTests.cs; security/trash-api.md; foundation status/changelog/README/OpenAPI.

**Interfaces:** GET `/api/trash?pageSize=&cursor=` with existing bearer + single X-Cerberus-Vault-Access. Only exact query names; body forbidden;413 has empty response. New four-type `kind` discriminator schemas mark output fields required.

- [ ] Add actual host tests before route registration: owner/selected/native metadata all four kinds, no-store; malformed transport/auth/identity/dependency; exact JSON and generated schema (no grant variant), no mutations. Observe absent route RED, implement and verify GREEN.
- [ ] Document operation-root semantics, original deadlines, current selected scope, cursor limits/inventory cost and restoration deferred to UC51.
- [ ] Run locked restore, zero-warning Release build, dependency/native audits, helper/spec/OpenAPI/native corpus + actual lease clients, Docker, full unfiltered Release suite and fresh merged coverage; freeze source/test hashes around full run.
- [ ] Obtain independent whole-change review, address material findings with RED→GREEN evidence, update README to51 Done/5Todo, open PR closing51 and set Testing.
- [ ] Wait for exact-head branch-policy/test/docker SUCCESS, normal squash merge, verify identical source/merged trees, issue Closed/project Done, clean synchronized develop and owned branch removal. Hash/archive evidence and continue UC51.
