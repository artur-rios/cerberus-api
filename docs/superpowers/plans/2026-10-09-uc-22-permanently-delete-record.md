# UC22 permanent record deletion implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans inline.
> Steps use checkbox (`- [ ]`) syntax. User all-issues authorization replaces
> repeated approval menus; all automated verification/delivery gates remain.

**Goal:** owner-only irreversible active/trash record deletion with typed durable
erasure, retry-safe physical purge and deletion-preserving backup restoration.
**Architecture:** typed terminal identity throughout existing predicates, committed
authorized terminal intent/outbox, external fsynced ledger, fenced record finalizer.
**Stack:** existing .NET10/EF10/PostgreSQL18.6/native mediator; additive migration,
no dependency or service change.
**Spec:** docs/superpowers/specs/2026-10-09-uc-22-permanently-delete-record.md

## Global Constraints

Preserve four primary user-file SHA. Isolated feature/uc-22-permanently-delete-record
from verified develop5c994f6f5626c83cb332cb5170d93907cd8e10ef; issue23/one PR.
Binding spec governs exact one-input/two-output strict DELETEpermanent, current
owner/scope/expiry/native precedence, active/trash selection, every failure path,
typed identity BEFORE ANY physical purge, ordered locks, irreversible intent503
semantics, external-before-physical order, lease fencing, replay and nonnull selection.
All.NET commands serialized. Fresh exact-tree UC21 CI/full4235 baseline retained.
Full six-family suite/coverage>=90line/native/audits/spec/OpenAPI before finalreview.
One fresh ordinary review/one blocking TDD fix pass/no rereview/all rulings+costs
and minors exhaustive. Review/integration follow final task commit; Done only after
all remote delivery/nineDoD checks, verified tested develop tree and user-fileSHA.

## Review Focus

- Compound typed erasure predicates must preserve current account/profile/record/
  folder/collection/grant authority under same-GUID collisions; Task1 real-store
  matrix, old-schema migration and legacy-file replay exercise these paths while
  review checks every transformed predicate and unsupported legacy kind handling.
- Durable intent cannot be undone or leak a still-visible record after a ledger/
  commit/caller failure; Task2 real outage/post-intent/post-ledger fault and retry
  tests cover crash boundaries while review checks untested combinations.
- Selected trash scope must not trust stale/foreign/inactive historical membership
  or revive grants; Task2 direct/folder/collection historical matrix and Task4 actual
  selected handles cover paths while review examines mixed/cyclic/missing ancestry.
- Existing owner/recipient writers and implicit FK locks must not deadlock or
  bypass a terminal winner; Task2 controlled actual-store races and all lock expiry
  checks exercise current writers while future UC35 prerequisites remain explicit.
- Stolen/expired retention claims and nonempty trash cascade membership must not
  allow unfenced purge/incorrect completion or delete another resource kind; Task2
  actual claim theft/expiry/cascade tests and Task4 pg_dump/replay pin this.

### Task 1: Repair typed terminal identity before purge

**Files:** Modify Domain Operations/TerminalErasure.cs; Data AppDbContext.cs,
Erasure/{FileErasureLedger,TerminalErasureStore}.cs, all current Accounts/Identity/
Profiles/Protection/Records terminal predicates; add EF TypedTerminalErasure
migration/designer/snapshot; tests Data TypedTerminalErasureTests.cs and existing
ErasureLedger/RestoreReconciler tests; replace four old fixture-only kind sites
with actual kinds. No physical purge code in this task.
**Interfaces:** ITerminalErasureStore.IsErasedAsync(string resourceKind,Guid resourceId,CT),
existing ReapplyAsync(ErasureEntry,CT) typed; File ledger new kind-GUIDN filenames
with valid legacy GUIDN reads; unique(kind,id)/supported6-kind constraint.

- [x] Write tests before product: same GUID two-kind DB entries and independent
  lookup; invalid kind/ID rejects; concurrent same-kind earliest entry; typed new
  files and legacy readable/samekind retry/crosskind writes/corruption; actual old
  migration upgrade preserving rows and failing invalid kind closed. Real current
  account/profile/record reads/writes/reservation and selected handle survive other
  kind erasure but own-kind still denies; native grant/collection/owner collisions
  preserve sharing. Update typed interface tests, run focused RED (missing signature
  plus observable collision failures before product changes).
- [x] Implement smallest typed schema/interface/ledger/predicate repair; manually
  inspect every compound predicate/kind mapping. Focused GREEN then whole Data;
  record actual output zero failures/skips. Commit `fix: scope terminal erasures by
  resource kind`; task-done audits fresh completed wholeData log. Expected exit0.

Task command: dotnet test tests/Infrastructure/ArturRios.Cerberus.Data.Tests --logger
"console;verbosity=minimal"; focused FullyQualifiedName~TypedTerminalErasureTests
or ErasureLedgerTests or RestoreReconcilerTests. No untyped predicate remains.

### Task 2: Authorized terminal intent and durable fenced record purge

**Files:** New Domain Records/RecordPermanentDeleteContracts.cs; Data Records/
RecordPermanentDeleteStore.cs, Erasure/RecordPurgeHandler.cs, Erasure/RecordErasure.cs;
modify TerminalErasureStore record replay and Operations/RetentionWorkStore.cs
record-purge completion to use fresh database time after claim-row waits. New Data RecordPermanentDeleteStoreTests.cs
and RecordPurgeHandlerTests.cs/restore cases. Modify Protection/VaultProtectionChangeStore.cs
to omit typed terminal records awaiting purge from rotation inventory while retaining
recoverable trash. Reuse existing retention schema.
**Interfaces:** RecordPermanentDeleteRequest(Guid Actor,string AccessVerifier,
Guid RecordId,long ExpectedRevision); RecordPermanentDeleteDetails(Guid RecordId,
DateTimeOffset DeletedAt); IRecordPermanentDeleteStore.DeleteAsync(request,CT)
returns VaultResult<details>. RecordPurgeHandler implements IRetentionHandler Kind
"record-purge"; common record erasure removes only exact typed data, fenced by
current claim for workers, operator-gated restore replay before traffic for restore.
Store constructor consumes IDbContextFactory<AppDbContext>, IErasureLedger,
CerberusOptions and TimeProvider; immediate request claims use the explicitly
configured RetentionInterval as lease, matching the existing worker. Handler
constructor consumes factory and ledger; persisted database claim expiry governs.

- [x] Write RED tests all main/AF/current owner/native/selected active+trash scope,
  malformed restricted snapshot/no content parse, safe/stale/max revisions/parent
  capacities, typed collision survival, structural bumps exactly once, no selection
  nulling; locks/expiry/final current scope, newly unheld authority, current actual
  edit/trash/move/create/association/rotation winner races and target query plan.
  Intent rollback faults; after-intent ledger outage keeps all reads/writes denied
  and queued work; true fsynced ledger precedes removal; after-ledger/post-delete
  crash retries; actual claim expiry/theft fence all mutations/completion; nonempty
  cascade and other-kind trash survive; caller/internal cancellation/outages.
  Actual backup before deletion then restore/replay removes record but same-GUID
  other-kind survives and corrupt ledger/DB keeps gate closed. Expected missing
  contracts/store/handler compile RED before product.
- [x] Add minimal contracts/store/finalizer/replay; focused GREEN then wholeData
  zero failures/skips, inspect real races/faults/ledger/backup evidence. Commit
  `feat: permanently erase owned records`; task-done audits fresh wholeData log.

Task command: same wholeData; focused permanent/purge/restore tests. All Task1
typed prerequisites must already be committed/verified before physical purge code.

### Task 3: Strict permanent-delete command

**Files:** New Command Records/PermanentlyDeleteRecord{Command,Validator,Handler,
Output,Messages}.cs and Command.Tests/PermanentlyDeleteRecordHandlerTests.cs.
**Interfaces:** consumes Task2 request/store/details; internal context(actor,
handle,recordId), exactly one JsonRequired ExpectedRevision input; exact two output
fields recordId/deletedAt. Known stable safe error allowlist and canonical statuses.

- [ ] Write RED success/context/hash/ct forwarding, safe input boundaries and
  pre-store failures, exact required input/output shape; null/unknown/malformed
  store result or mismatched ID/invalid UTCµs timestamp→503 no partial output;
  expected errors and caller cancellation. Expected missing command/handler.
- [ ] Minimal fivefiles, focused then wholeCommand GREEN, commit `feat: validate
  permanent record deletion`; task-done audits fresh unchanged wholeCommand log.

Task command: dotnet test tests/Application/ArturRios.Cerberus.Command.Tests
--logger "console;verbosity=minimal"; focused PermanentlyDeleteRecordHandlerTests.

### Task 4: Actual HTTP, documentation and delivery

**Files:** RecordsController/Startup route+DI (store/command/purge handler), new
WebApi.Tests/RecordPermanentDeleteHttpTests.cs, record-api/operator docs, README
UC22Done/M03fourteen, CHANGELOG/OpenAPI; actual full coverage generated artifact.
**Interfaces:** Task2+3 DELETEpermanent strict current context/VaultProofBody,
200record_permanently_deleted exact2metadata;400401403404409empty413503/no-store.

- [ ] Actual native HTTP RED before route: actual UC16 records/UC15 Master and
  PerProfile handles, direct/folder/ownedcollection active/trash selected/wide,
  native RO/RW denial, current scope after erasure/stale create/edit/move/trash,
  sameGUIDotherkinds+real ledger files, exactly2fields/no content, complete strict
  syntax/input/header/query/UTF8/BOM/compression/oversize/cancellation/outage matrix.
  Expected missingroute404/405, prove before product registration.
- [ ] Minimal route/DI; focused GREEN. Update API/operator irreversible503/retry/
  migration/legacy/replay instructions, README/changelog and generated OpenAPI;
  shape/drift checks, retain existing shared generator Minors explicitly. Fresh
  lockedrestore/NuGet/nativeOSV153/corpus322fourways/36nativehelpers/23Python/spec/
  full-range whitespace check and fresh full unfiltered six-family coverage,
  >=90line each production assembly/zero failures-skips/reportbranch. Commit
  `feat: expose permanent record deletion`; task-done audits actual fresh full log.
  Then ONE fresh ordinary review/all rulings/minors/one blocking TDD pass if needed;
  PR closes23/Testing/latest exacthead3CI/freshbase/rules/normalmerge/allnineDoD/
  Done/remoteabsent/testeddeveloptree/userSHA/archive own scratch; continueUC23.

Task command: focused WebApi permanent tests then python3 scripts/coverage.py;
expected exit0/all six families/all coverage thresholds. Never infer from filtered
suite or skip required family; no redundant rerun once unchanged gates pass.
