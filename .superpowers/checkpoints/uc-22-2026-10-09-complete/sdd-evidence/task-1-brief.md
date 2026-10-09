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

- [ ] Write tests before product: same GUID two-kind DB entries and independent
  lookup; invalid kind/ID rejects; concurrent same-kind earliest entry; typed new
  files and legacy readable/samekind retry/crosskind writes/corruption; actual old
  migration upgrade preserving rows and failing invalid kind closed. Real current
  account/profile/record reads/writes/reservation and selected handle survive other
  kind erasure but own-kind still denies; native grant/collection/owner collisions
  preserve sharing. Update typed interface tests, run focused RED (missing signature
  plus observable collision failures before product changes).
- [ ] Implement smallest typed schema/interface/ledger/predicate repair; manually
  inspect every compound predicate/kind mapping. Focused GREEN then whole Data;
  record actual output zero failures/skips. Commit `fix: scope terminal erasures by
  resource kind`; task-done audits fresh completed wholeData log. Expected exit0.

Task command: dotnet test tests/Infrastructure/ArturRios.Cerberus.Data.Tests --logger
"console;verbosity=minimal"; focused FullyQualifiedName~TypedTerminalErasureTests
or ErasureLedgerTests or RestoreReconcilerTests. No untyped predicate remains.

