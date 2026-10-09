# UC22 ordinary whole-branch review

Reviewed base `5c994f6f5626c83cb332cb5170d93907cd8e10ef` through head `cc2b8bb1983950ec45e8f2c7e0252d9e88e2dd88` in `/tmp/cerberus-uc22-worktree`. HEAD matched the requested head. This is the single fresh ordinary review, performed in passes by one reviewer. The checkout, index, HEAD and branches were not modified; no builds, tests, subagents or remote actions were run. This report is the only file written.

Inputs: the complete supplied reviewer prompt, binding UC22 spec, implementation plan, progress ledger, whole-branch diff, affected production code and tests, `/tmp/cerberus-uc22-validation.json`, and the actual full-suite coverage log. I checked the six unfiltered test summaries in that log: Domain 313, Shared 125, Command 783, Query 259, Data 1,634, WebApi 1,482; total 4,596 passed, zero failures or skips. Recorded coverage is 98.4% line / 91.7% branch, with all six production assemblies above 90% line. These are existing execution results, not tests independently rerun by this reviewer.

### Strengths

- Typed terminal identity is applied consistently across the changed account, identity, profile, protection and record predicates, including compound owner/resource/grant expressions. The composite index, six-kind database constraint, typed lookup, legacy filename validation and earliest-time replay agree. Unknown kinds fail closed instead of being guessed.
- `RecordPermanentDeleteStore.cs:51` commits authority-checked terminal intent and immediate durable work together. Rollback paths precede external ledger effects; failures after commit preserve terminal visibility denial and durable recovery. The request returns success only after ledger, physical purge and fenced completion.
- `RecordPermanentDeleteStore.cs:188` restricts selected trash authorization to typed owned operation membership and strictly parsed historical IDs. Final SQL rechecks the exact history bytes and current selected authority. Unretained live record links cannot widen trash scope. Direct valid history can survive an irrelevant broken folder path; folder-derived authority requires complete current active owned ancestry.
- `RecordErasure.cs:11` removes only record content, its cascading direct links and typed record trash membership. Nonempty cascades and other resource kinds survive. Persisted claim predicates guard each physical statement, with checks after statements to roll back expiry encountered during waits. `RetentionWorkStore.cs:39` acquires the work row first and uses a fresh database statement for record-purge completion.
- Current writer ordering is compatible with the new owner operation: account/session, owned profiles, owned collections, immediate folder, target, then existing links. The final mutation rejects newly contributing unheld direct authority without acquiring late earlier locks. Recipient edit races are exercised in both winner orders without foreign account/profile locks in this operation.
- Tests exercise actual PostgreSQL, filesystem ledger, native grant binding and HTTP middleware rather than substituting these critical boundaries with mocks. The real dump/restore test demonstrates exact record physical removal before the traffic gate opens while a same-UUID folder survives. Focused tests also cover rollback, post-intent outage, post-ledger failure, theft/expiry, rotation and stale writers.

### Issues

#### Critical (Must Fix)

None found.

#### Important (Should Fix)

None found that blocks the authorized develop integration.

#### Minor (Nice to Have)

1. **Ledger permission failures use the generic 500 response instead of the documented dependency 503.**
   - File: `src/Infrastructure/ArturRios.Cerberus.Data/Records/RecordPermanentDeleteStore.cs:46` (ledger filesystem operation at `src/Infrastructure/ArturRios.Cerberus.Data/Erasure/FileErasureLedger.cs:47`).
   - A permission denial while creating/reading/moving a ledger file can throw `UnauthorizedAccessException`, which is neither `IOException` nor another type in this catch filter. It escapes to `SafeFailureMiddleware.cs:14`, producing `500/internal_failure` rather than `503/persistence_unavailable`.
   - Actual effect: clients and operators receive the wrong failure classification during a ledger permission outage. The committed terminal intent/outbox remains intact, reads stay denied, and the worker catches the failure and retries; this does not create an erasure bypass or false success. That limited effect makes this Minor rather than a data-loss or authorization blocker.
   - Include this filesystem exception in the dependency mapping and add a focused regression that checks the 503 response plus retained intent/work. A deterministic permission-exception fixture avoids root-dependent chmod tests. This finding is based on the exception path inspected, not a newly executed reproduction.

2. **The shared OpenAPI generator still understates required request/response members.**
   - File: `docs/contracts/openapi.json:2557` (access header), `docs/contracts/openapi.json:2564` (body), `docs/contracts/openapi.json:5932` (output schema).
   - The access header and request body are not marked required, and `recordId`/`deletedAt` lack output requiredness, although the runtime contract requires them. `expectedRevision` itself is correctly required at line 5919.
   - Actual effect: generated client models can permit incomplete requests or unnecessarily optional success fields. Runtime validation and exact output remain correct. This is the already acknowledged shared generator Minor; repair the shared metadata before release and retain drift/shape checks.

3. **The advertised 413 body differs from the actual empty response.**
   - File: `docs/contracts/openapi.json:2703`.
   - The new endpoint advertises `ProblemDetails` content for 413; the common limit middleware and HTTP tests establish an empty body.
   - Actual effect: clients generated from the schema may attempt to deserialize a body that is absent. This is the already acknowledged shared generator Minor, not an endpoint erasure defect. Remove the generated content entry for the empty 413 response before release.

### Plan and ruling assessment

All four implementation tasks are present. The five Review Focus areas were considered directly: every changed typed predicate; durable intent/crash ordering; direct/folder/collection historical scope including hidden, missing, mixed and cyclic paths; current owner/recipient writer ordering; and persisted claim/cascade fencing. No significant unjustified plan deviation was found.

Disposition of all 17 supplied rulings:

1. **Typed identity across all existing predicates/schema/ledger:** satisfied. Every transformed compound expression inspected uses the actual account/profile/record/folder/collection/grant kind; the full-family regression evidence addresses the broad availability risk.
2. **Grant namespace:** satisfied in constraints, validation, ledger and grant visibility. Future grant erasure must preserve this exact namespace and minimal event form.
3. **Legacy filenames:** satisfied; validated legacy files remain in place, different kinds get typed filenames, same-kind retries retain their original event, and database replay keeps the earliest known timestamp.
4. **Invalid legacy kinds:** migration fails closed; fixture substitutions use the real target kinds without weakening denial expectations. Operator repair remains necessary for invalid legacy metadata.
5. **Intent → ledger → physical removal:** satisfied. Dependency/caller failure after intent remains irreversible pending deletion; this is documented and exercised, not treated as a rollback.
6. **Exactly recordId/deletedAt output:** satisfied by command, handler and route. No invented terminal live revision/sequence; later ordered synchronization remains separate.
7. **Restricted history plus current owned authority:** satisfied. Missing inaccessible history denies; current valid retained collection/folder paths can contribute; snapshots do not revive grants or links.
8. **Ignore unused ciphertext/time; validate required counters:** satisfied. Opaque content is not parsed for owner deletion; target and affected parent metadata still validate. Required-counter corruption remains an operational repair condition.
9. **Maximum target revision and one parent advance:** satisfied. Target revision does not advance; active directly affected live parents advance once, while already trashed targets cause no second structural bump. Exhausted parent capacity rejects.
10. **Current lock protocol/final scope:** satisfied for current implemented writers. The final guarded statement rechecks session expiry and current scope, and no late earlier locks are acquired. The future UC35 implicit recipient-account FK audit remains explicit.
11. **Existing work schema as outbox:** satisfied. Exact typed intent plus persisted work/token/expiry guards purge; no credentials or new encrypted snapshots are stored in the outbox. Lost claims do not authorize completion.
12. **Record replay before traffic:** satisfied. Exact record ciphertext/links/membership are removed transactionally with typed replay. Other kinds remain typed tombstones; selected ProfileId is never cleared.
13. **UC21 verified baseline:** accepted as supplied. No redundant baseline run was performed. The inspected fresh complete UC22 six-family log supplies subsequent regression evidence.
14. **Deferred independent qualification:** honored only for development. Ordinary current ownership/native binding/erasure checks were performed; release/deployment is not certified.
15. **Configured retention lease:** satisfied. The request uses injected options/time provider and the configured interval, matching the worker. Too-short leases can cause pending 503 and recovery, not unfenced success.
16. **Rotation inventory:** satisfied. Only typed terminal records are excluded; recoverable trash remains included. Stale extra inventory fails validation before proof consumption/content mutation, as exercised by tests.
17. **Fresh database-time completion:** satisfied for record-purge. The completion statement runs after the row-lock acquisition and checks persisted exclusive expiry; other retention kinds retain their existing clock contract.

### Recommendations

Keep the three Minor findings visible through release preparation. Carry forward the already documented ordered terminal synchronization, later typed physical handlers and BEFORE UC35 implicit FK/deadlock audit. Complete the executor's exact-head remote CI, branch-policy and merge/delivery gates; this read-only review does not assert those future actions succeeded.

### Declined to judge

- Independent formal security audit approval: explicitly deferred by the user for develop; ordinary current authorization and ownership behavior was reviewed.
- Independent formal protocol and cryptographic qualification: explicitly deferred by the user for develop; existing native binding use and current route rejection behavior were reviewed.
- Real external-client interoperability, decryption and client operability approval: explicitly deferred by the user; server fixtures do not establish these approvals.
- Production deployment, production restore drill and release qualification: outside this development review; main/tag/release gates remain mandatory and are not certified here.
- Durable offline ordered terminal visibility/tombstone consumption: explicitly assigned to later synchronization integration before release; the response is not treated as an ordered sync event.
- Physical purge/replay of account, profile, folder, collection and grant resources, and general trash expiry/restoration: explicitly assigned to their later use cases; their current typed fail-closed authority and cross-kind survival were reviewed here.
- Implicit recipient-account FK/deadlock behavior of future UC35 grant writers: explicitly a BEFORE UC35 prerequisite; currently implemented writer interactions were reviewed.
- The inherited UC21 trailing-space blank outside this diff: unchanged pre-existing formatting Minor; no UC22 behavior depends on it.
- Remote exact-head CI, issue/project closure, merge, branch cleanup and final delivery state: not independently executed or certified because this review is read-only and remote actions were prohibited; the executor must complete those gates.

### Assessment

**Ready to merge? Yes — for the authorized develop integration, subject to the executor completing the mandatory remote CI and delivery gates.**

**Reasoning:** No blocking current ownership, typed identity, durable erasure, lease-fencing or restore correctness defect was found in the reviewed branch. Three Minor findings remain, including the new permission-error classification gap and two already tracked OpenAPI mismatches; the existing 4,596-pass evidence supports development integration but does not confer release or client qualification.
