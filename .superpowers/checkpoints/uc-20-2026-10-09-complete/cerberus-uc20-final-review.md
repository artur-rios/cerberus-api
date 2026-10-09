# UC20 whole-branch review

Base: `1713397fea3deb4ae9d30119e493997eb7943514`  
Head: `67042c4632936dcb73ee170e00ea10e061a1a2a5`  
Checkout: `/tmp/cerberus-uc20-worktree`

Reviewed the complete 20-file change in passes: transaction and authority SQL; adjacent stores, FK schema and retention executor; command and HTTP boundary; tests; specifications, plan, ledger and published contracts. The checkout was read-only throughout. No subagents or duplicate test suite were run. HEAD matched the supplied head, status was clean, and `git diff --check 1713397..67042c4` passed. No schema or dependency change is present.

### Strengths

- `RecordTrashStore.cs:25` establishes owned-account serialization; `:69` reloads authority after content and association locks; `:96` places complete current authority in the actual mutation statement. This avoids using an earlier successful scope check as permission to write.
- `RecordTrashStore.cs:89` captures all remaining stored direct typed links, including inactive parents, while `:84` limits structural revision changes to active parents. Composite same-owner FKs in `Configuration/ResourceModel.cs:14` prevent a foreign association from being silently dropped from the retained owned snapshot.
- `RecordTrashStore.cs:108` verifies preserved opaque bytes and metadata after the update, and `:119` commits operation, entry and queue with the record/link/parent changes. Failures roll back transaction state; retries do not create another retention window.
- `DeleteRecordHandler.cs:22` rejects malformed or contradictory store results before returning data. HTTP tests use actual create/unlock flows and native Master/PerProfile/RO/RW fixtures, rather than only a mocked controller.
- Data tests include controlled recipient-edit winner orders, every relevant lock-class expiry, before-write ancestry/terminal changes, new references before the target lock, removed links, actual rotation, and fault-injected persistence rollback. These are useful behavior tests, beyond coverage counts.

Paths above are relative to the respective changed source directories under `src/Infrastructure/ArturRios.Cerberus.Data`, `src/Application/ArturRios.Cerberus.Command`, and their obvious subdirectories.

### Issues

#### Critical (Must Fix)

None found in the reviewed implementation.

#### Important (Should Fix)

No new Important implementation defect or merge-blocking plan departure found. The inherited and planned release limitations below have important effects and are not being declared production-ready merely because this branch can merge.

#### Minor (Nice to Have)

1. **OpenAPI understates required request and success fields.**
   - File: `docs/contracts/openapi.json:2352`, `:2359`, `:5059`.
   - The access header and request body lack `required: true`, and the six success properties lack a required list. The command's `expectedRevision` itself is correctly required at `:5046`.
   - Generated clients can treat mandatory inputs and guaranteed output metadata as optional, producing avoidable 400/401 requests or unnecessary nullable handling. Runtime strictness is tested and does not depend on these annotations, so this is a contract-generation defect rather than an authorization defect.
   - Fix the shared generator metadata and regenerate contracts before release, with a contract-shape assertion for header, body and success requiredness.

2. **OpenAPI advertises a body for the empty 413 response.**
   - File: `docs/contracts/openapi.json:2499`.
   - The generated response declares ProblemDetails content, while the common request-limit behavior and `RecordTrashHttpTests.cs:96` return/assert an empty body.
   - A generated error parser may fail while attempting to deserialize the empty response. The request remains rejected correctly with 413; no content is disclosed or mutation committed.
   - Correct shared 413 response generation to omit content and regenerate before release.

### Review Focus (verbatim)

- Multiple inactive, newly inserted or removed profile/collection links must not produce an incomplete restoration snapshot or unguarded parent update; Task1 lock/snapshot/newunheldbeforetarget tests plus review of unexpected multi-parent combinations.
- A selected owner must never expand to unselected records, and a native-visible foreign RW recipient must never gain deletion rights; Task1 current scope/native/permission matrix and Task3 actual Master/PerProfile/RO/RW HTTP cases.
- Recipient edits, owner rotation, association replacement and creation must not deadlock with parent/FK/record locks or lose a revision winner; Task1 actual concurrent stores and ordered lock trace, review future sharing-writer assumptions.
- Session expiry or a committed ancestor/terminal change immediately before the guarded statement must deny despite earlier success; Task1 every lock-class expiry with stale revision and final complete ancestry/lifecycle interception, plus HTTP current-state matrix.
- Rollback after partial association/parent/trash persistence and malformed store results must not leave a hidden record, leaked response or extra operation/deadline; Task1 fault injection/retry and Task2 corruption matrix with Task3 exact public response checks.

### Focus results

1. **All direct associations:** checked the all-stored active/inactive test (`RecordTrashStoreTests.cs:36`), before-target inserts (`:133`), link removal (`:167`), multi-profile ordered-lock/plan test (`:214`), and the set-based implementation. Both association kinds use the same reload/held-set/final-guard discipline; duplicate keys are prevented by schema. Mixed active/inactive parents, multiple direct parents, and overlapping direct/inherited membership do not duplicate bumps or omit snapshots. There is no explicit combinatorial test of every multi-collection mixture; code/schema inspection supplies that part of the assessment.
2. **Owner and recipient authority:** checked selected direct/folder/collection routes, unselected owned targets, invalid selections, and actual Master/PerProfile HTTP unlocks. The foreign branch returns only denial, regardless of RW; relevant native evidence corruption yields 503, hidden routes yield 404. No selected scope expansion was found.
3. **Concurrency:** read RecordUpdateStore, RecordCreateStore, ProfileAssociationStore, VaultProtectionChangeStore and the FK model. Controlled current-writer tests cover collection-before-content and owner-account serialization. No cycle in these supported paths was found. Not-yet-written sharing/folder writers remain a future integration obligation, not proven by today's tests.
4. **Final authority:** repeated snapshots prioritize current authority over stale revision after waits. Final SQL recomputes the entire ancestry and session time predicate; before-write expiry, reparent-to-hidden, ancestor trash, and terminal markers are exercised. Account/session changes by legitimate writers serialize; independent terminal changes before the final statement are rechecked. No commit-time expiry guarantee is implied.
5. **Rollback and malformed results:** fault tests compare persisted records, links, parents, operation/entry/queue and sessions, then successfully retry. Command tests reject null/error-plus-data/unknown-error/mismatched IDs/unsafe numbers/timestamps; HTTP verifies exact six fields, no partial data, no-store and empty 413. PostgreSQL sequence gaps after rollback remain valid; a consumed sequence is not a committed second operation.

### Initial rulings and independent assessment

1. **Initial ruling (verbatim):** Ruling: Purpose-built current-scope ownertrash transaction using existing generic typedtrash tables, no permission-framework refactor or separate readthenwrite — currentauthority/revision/restorablelinks must commit together — cost if wrong: targeted SQL duplication and lock maintenance complexity.

   **Assessment:** Accepted. The one transaction covers current authority, record state, links, structural changes, operation, entry and queue. No separate preflight can authorize the mutation. Targeted SQL duplication is a real maintenance cost, but no divergent authority defect was found against RecordUpdateStore and the current schema.

2. **Initial ruling (verbatim):** Ruling: Current native-visible foreign RO/RW returns403 before revision; hidden404 first; validate allcontributingnativeevidence without foreignresource locks before denial — recipients have no delete right, current snapshot safely linearizes a no-write denial — cost if wrong: corrupt relevant native route can503 despite never permitting delete.

   **Assessment:** Accepted. StateError establishes current scope before the foreign-owner branch; the native evidence query includes every relevant visible contributor and ValidNative requires all of them. Denial performs no foreign resource locks and no mutation. A malformed relevant route causes a retryable 503 even when another route is valid; that availability cost is deliberate and tested, not a delete privilege escalation.

3. **Initial ruling (verbatim):** Ruling: Owned trash does not parse/decrypt its opaque ciphertext and preserves it/clientEditedAt/owner exactly — deletion does not replace or return content, allowing deletion of damaged ciphertext — cost if wrong: retained corrupt ciphertext still prevents successful authenticated client restore or complete rotation until separately repaired/purged.

   **Assessment:** Accepted. The store copies no replacement content, checks the stored bytes after mutation, and the HTTP/command response exposes only metadata. Trashing already-corrupt content does not make it recoverable or make complete rotation possible; the existing rotation parser still fails on corrupt retained content. This is an acknowledged existing-data limitation, not silent repair.

4. **Initial ruling (verbatim):** Ruling: Snapshot allstored direct typedlinks including inactive, detachfolder/removeProfileRecord+CollectionRecord atomically — restricted historical publicID snapshot supports later validrestore while activeassociations disappear — cost if wrong: UC51 must revalidate/reconcile snapshot and typedentry before retrash, never blindly revive permissions.

   **Assessment:** Accepted. Composite same-owner FKs and unique typed association keys justify resolving all stored direct IDs through owned parents. Reload after target/association locks, held-set checks and sorted snapshots cover inactive links and removed/new links. Future restoration must revalidate historical links and reconcile the typed entry; a snapshot alone must never revive grants.

5. **Initial ruling (verbatim):** Ruling: Bump active directly affected ownedprofile/collection/immediatefolder structuralrevision/sequence once; preserveopaquecontent/clienttime/keymetadata; no inheritedancestor/recipientprofile bumps — directmembership removal is structural like UC16creation — cost if wrong: later durablevisibility sync must also compute dynamicdescendant/removal effects.

   **Assessment:** Accepted. Only actual remaining direct profile/collection links contribute parents, and the single immediate folder is included. Each active parent is guarded and bumped once; opaque fields are untouched. Dynamic inherited visibility disappears because the record becomes inactive. Future durable removal notifications are still necessary for offline clients.

6. **Initial ruling (verbatim):** Ruling: Keep selectedprofile handles valid and nonnull — recordtrash does notdelete profile or change its policy/scope — cost if wrong: callers must reload target after success; no resource-level handle revocation implied.

   **Assessment:** Accepted. The operation never clears the selected profile or revokes its session. Subsequent current-scope reads exclude the trashed target. Structural profile revision changes do not by themselves convert a selected session to account-wide scope.

7. **Initial ruling (verbatim):** Ruling: OwnaccountNoKey→sessionUpdate→ALLrelatedownprofilesincludingselectionGUIDNoKey→owncollectionsGUIDNoKey→immediatefolderNoKey→recordUpdate→existingtypedassociationrowsUpdate — parents permit FKKeyShare, targetexcludesnewrefs and associationrowsstabilize removals; compatible with ownedaccount serialization and crossownercollectionbeforecontent order — cost if wrong: actual FK/rotation/recipient/association concurrency tests must reveal cycles; future writers must preserve protocol.

   **Assessment:** Accepted for current writers. Account serialization aligns owned create, association, rotation and lifecycle operations. The reviewed composite FKs need KEY SHARE on referenced parents, which is compatible with the new parent NO KEY UPDATE locks. Recipient update acquires collection locks before content; tests force both winner orders. Future shared-folder or association writers must retain this ordering and owner serialization.

8. **Initial ruling (verbatim):** Ruling: Newunheldparent/association409 afterfreshscope and no lateearlierlocks; test newinserts BEFOREtargetlock, then finalcompleteancestry/currentauthority guards — targetUPDATE excludes later FKrefinserts; earliernewrefs require reload — cost if wrong: extra retry/recursion and statement-snapshot linearization.

   **Assessment:** Accepted. New associations committed before target admission are reloaded and must be in held parent sets; no earlier-order lock is acquired late. The target FOR UPDATE conflicts with FK reference admission and existing link rows are locked before removal. Final authority recomputes ancestry and scope in the mutation statement. Statement-time linearization, rather than commit-time expiry, is the stated and reasonable contract.

9. **Initial ruling (verbatim):** Ruling: Existingtypedtrashentry with active ownedrecord is conflictingstate409; retryalreadytrashed404 withoutdeadlineextension — do not overwrite restorationhistory or create secondoperation — cost if wrong: futureUC51 must consume/reconcile typedentry before retrash.

   **Assessment:** Accepted. The active-record typed-entry check returns conflict, and already-trashed records fail current visibility before another operation is constructed. The typed unique index is a further persistence backstop. Retry does not extend retention; UC51 must reconcile this entry before allowing another trash cycle.

10. **Initial ruling (verbatim):** Ruling: Queue trash/{operationGUID} using existing failclosed executor; actualrestore/expiry handler remainsUC51/53 beforeRelease — no physicalpurge is authorized by this recoverable endpoint; retainedrecord stays in complete rotationinventory — cost if wrong: missing later handler delays physicalpurge and must block release, never falselycomplete work.

   **Assessment:** Accepted for development integration only. RetentionExecutor throws when no unique handler exists and RetentionRunner does not mark that work complete. Retained records remain in rotation inventory. The absence of restoration and actual expiry has important user effects and blocks release; it is explicitly assigned to UC51/53, not fulfilled by this endpoint.

11. **Initial ruling (verbatim):** Ruling: Retain conservative globalterminalGUID exclusions until typedrepair BEFORE ANY physicalpurge and nevernull selectedProfileId — known cross-kind collision safety prerequisite retained — cost if wrong: sameGUID acrossresource kinds can falselyhide untilrepair.

   **Assessment:** Accepted as an inherited conservative safeguard, not as a completed solution. A terminal UUID from another resource kind can incorrectly hide a live resource because exclusion and uniqueness are global. Typed repair is required before any physical purge; nullable selection must never be used to erase a selected scope. This branch neither introduces physical purge nor broadens selected sessions.

12. **Initial ruling (verbatim):** Ruling: Independent security/protocol and actualclient approvals remain development-onlydeferred under explicituser instruction; ordinaryreview/native/fullsuite/CI andreleasegates remain — cost if wrong: latentqualification defects await mandatory release approval.

   **Assessment:** Accepted as an explicit development-only authorization boundary. Ordinary authority, persistence, native-binding use, lock behavior and API correctness were reviewed here. This review does not substitute for independent cryptographic/protocol assessment or real-client release approval.

### Verification evidence inspected

- `/tmp/cerberus-uc20-coverage.log`: completed, unfiltered Domain 313, Shared 125, Command 641, Data 1,304, Query 259 and WebApi 1,236 = **3,878 passed, zero failed, zero skipped**. This is supplied execution evidence inspected by this reviewer, not a rerun claimed by the reviewer.
- `docs/coverage-report/Summary.json`: six production assemblies; **98.3% line / 91.7% branch** overall. Line coverage: Command 100%, Data 98.3%, Domain 99.3%, Query 100%, Shared 97.2%, WebApi 95.2%. All meet the 90% line gate. Branch coverage is reported, not misrepresented as a per-assembly 90% gate.
- Native audit log: 153 packages, no OSV findings in that snapshot. Native verification: 322 cases/four directions; native helper log: 36 tests OK; Python helper log: 23 tests OK. These are ordinary compatibility/audit evidence, not formal cryptographic assurance.
- NuGet audit reports no known direct/transitive vulnerabilities under the queried sources. Spec audit passes 107 FRs, 55 UCs, 29 BRs. OpenAPI drift check reports the contract current; current generation still has the two Minor semantic mismatches above.
- Reviewed added tests and adjacent production code directly. There was no concrete diagnostic requiring another full suite, and none was run. Remote CI, merge state and post-merge DoD are delivery gates for the executor, not outcomes claimed by this code review.

### Recommendations

Keep the two shared contract-generation fixes on the mandatory pre-release checklist. Preserve the documented lock protocol when adding move, collection membership, shared-folder and restore writers, and add controlled concurrency tests against this operation at those integrations. Keep restoration, expiry and typed terminal repair as concrete release blockers; do not describe scheduling a retention item as completed erasure.

The source uses dense formatting consistent with adjacent stores and tests; expanding particularly complex SQL/transaction sections would help maintenance, but no formatting-only change is required for this merge. A general authority framework refactor is not justified within this narrowly scoped transaction.

### Declined to judge as completed behavior

Every behavior considered and set aside from this branch's completion judgment is listed below. Reasons are explicit; these are not claims that the effects do not matter.

- **Actual restoration and association re-admission:** assigned to UC51 and explicitly excluded here. Current users cannot restore through an implemented restore endpoint yet; this is an Important release limitation. Stored snapshots and typed-entry conflicts support the future implementation but do not prove safe restoration.
- **Actual erasure at the 720-hour deadline:** assigned to UC53; no trash handler is installed here. Ciphertext can remain stored beyond the deadline and work retries rather than completes. This is an Important release limitation, despite being acceptable in the authorized development sequence.
- **Resource-kind-aware terminal identity and physical purge safety:** inherited global GUID exclusions/uniqueness can falsely hide a different-kind live resource with the same public ID; that availability effect is Important and must be repaired before any physical purge. Conservative exclusions are retained in this branch, not certified as a finished identity design.
- **Durable offline removal/descendant notifications:** future synchronization work is not implemented in this endpoint. Online current get/list behavior is covered; persistent offline clients must receive dynamic visibility removals later. Missing such sync in a released offline product would be Important.
- **Independent cryptographic/protocol assessment and real-client interoperability approval:** explicitly user-deferred for development only. Native fixtures, corpus and ordinary review provide useful evidence but cannot replace those release approvals.
- **Future sharing, folder move, lifecycle and restore writers not yet implemented:** their deadlock and scope correctness cannot be established from this branch. Existing writers were reviewed; future writers must obey account serialization, ordered parent/content locking and current-authority guards. Failure to do so would be Important and potentially security-critical depending on the effect.
- **Repair or authenticated recovery of already-damaged ciphertext:** deletion intentionally preserves opaque bytes without interpreting them. Existing corruption can still prevent client recovery and full rotation. Repair/purge is a separate operation; this branch neither causes that corruption nor claims to cure it.
- **A generalized permission/authority framework refactor:** considered as a maintenance response to duplicated SQL, but not required for observable correctness here. It would broaden the change and require its own behavioral regression review.

### Assessment

**Ready to merge? Yes — into `develop`, subject to the executor's required exact-head CI and delivery gates.**

**Reasoning:** The implementation follows the approved transaction, scope, retention and strict-response design, with strong actual-database/native HTTP evidence and no blocking runtime defect found. The two contract-generation Minors and explicit Important release prerequisites remain; this judgment is not approval to release the product.
