Review UC20 owned recoverable record deletion. Checkout: /tmp/cerberus-uc20-worktree. Read-only; do not change files/index/HEAD/branch, do not spawn agents. This is the ONE fresh whole-branch ordinary implementation review mandated by executing-plans; independent formal security/protocol and real-client qualification are user-deferred for develop only. Still assess ordinary implementation correctness/security. Use most capable model as directed by that skill.

Base:1713397fea3deb4ae9d30119e493997eb7943514
Head: 67042c4632936dcb73ee170e00ea10e061a1a2a5
Package: /tmp/cerberus-uc20-worktree/.superpowers/sdd/2026-10-09-uc-20-delete-record/review-1713397..67042c4.diff
Spec: /tmp/cerberus-uc20-worktree/docs/superpowers/specs/2026-10-09-uc-20-delete-record.md
Plan: /tmp/cerberus-uc20-worktree/docs/superpowers/plans/2026-10-09-uc-20-delete-record.md
Ledger: /tmp/cerberus-uc20-worktree/.superpowers/sdd/2026-10-09-uc-20-delete-record/progress.md

Implemented purpose-built PostgreSQL transaction, typed retained association snapshot, strict command/output, DELETE route/DI, docs/OpenAPI and 289 new cases (116 Data/74 Command/99 HTTP). Actual owned opaque bytes remain; allstoredlinks snapshot/remove; activeimmediateparents bump; retain720hours+oneop/entry/queue; currentnativeforeignRO/RW403 even RW; finalcurrentexpiry/lifecycle/fullancestry; noforeignlocks; actualstoreconcurrency/rollback/native fixtures. No schema/dependencychange. Read adjacent stores/schema to evaluate FK locks and scope interactions.

Verification: /tmp/cerberus-uc20-coverage.log fresh unfiltered full suite (3878passed,zero failures/skips,98.3%line/91.7%branch; actual final output inspected), all six production assemblies >=90line. Other actual logs /tmp/cerberus-uc20-{http-red,http-green-1,data-red,data-green-1,data-green-2,data-green-3,task1-data,command-red,command-green,task2-command,locked-restore,nuget-audit,native-audit,native-verify,native-tests,helper-tests,specs,openapi-write,openapi-check}.log. No need duplicate full suite unless concrete diagnostic. Full native153OSV/322vectors/36helpers/23Python/spec107FR55UC29BR alreadypassed.

Review Focus verbatim:

- Multiple inactive, newly inserted or removed profile/collection links must not produce an incomplete restoration snapshot or unguarded parent update; Task1 lock/snapshot/newunheldbeforetarget tests plus review of unexpected multi-parent combinations.
- A selected owner must never expand to unselected records, and a native-visible foreign RW recipient must never gain deletion rights; Task1 current scope/native/permission matrix and Task3 actual Master/PerProfile/RO/RW HTTP cases.
- Recipient edits, owner rotation, association replacement and creation must not deadlock with parent/FK/record locks or lose a revision winner; Task1 actual concurrent stores and ordered lock trace, review future sharing-writer assumptions.
- Session expiry or a committed ancestor/terminal change immediately before the guarded statement must deny despite earlier success; Task1 every lock-class expiry with stale revision and final complete ancestry/lifecycle interception, plus HTTP current-state matrix.
- Rollback after partial association/parent/trash persistence and malformed store results must not leave a hidden record, leaked response or extra operation/deadline; Task1 fault injection/retry and Task2 corruption matrix with Task3 exact public response checks.


Initial Rulings verbatim (evaluate each):
Ruling: Purpose-built current-scope ownertrash transaction using existing generic typedtrash tables, no permission-framework refactor or separate readthenwrite — currentauthority/revision/restorablelinks must commit together — cost if wrong: targeted SQL duplication and lock maintenance complexity.
Ruling: Current native-visible foreign RO/RW returns403 before revision; hidden404 first; validate allcontributingnativeevidence without foreignresource locks before denial — recipients have no delete right, current snapshot safely linearizes a no-write denial — cost if wrong: corrupt relevant native route can503 despite never permitting delete.
Ruling: Owned trash does not parse/decrypt its opaque ciphertext and preserves it/clientEditedAt/owner exactly — deletion does not replace or return content, allowing deletion of damaged ciphertext — cost if wrong: retained corrupt ciphertext still prevents successful authenticated client restore or complete rotation until separately repaired/purged.
Ruling: Snapshot allstored direct typedlinks including inactive, detachfolder/removeProfileRecord+CollectionRecord atomically — restricted historical publicID snapshot supports later validrestore while activeassociations disappear — cost if wrong: UC51 must revalidate/reconcile snapshot and typedentry before retrash, never blindly revive permissions.
Ruling: Bump active directly affected ownedprofile/collection/immediatefolder structuralrevision/sequence once; preserveopaquecontent/clienttime/keymetadata; no inheritedancestor/recipientprofile bumps — directmembership removal is structural like UC16creation — cost if wrong: later durablevisibility sync must also compute dynamicdescendant/removal effects.
Ruling: Keep selectedprofile handles valid and nonnull — recordtrash does notdelete profile or change its policy/scope — cost if wrong: callers must reload target after success; no resource-level handle revocation implied.
Ruling: OwnaccountNoKey→sessionUpdate→ALLrelatedownprofilesincludingselectionGUIDNoKey→owncollectionsGUIDNoKey→immediatefolderNoKey→recordUpdate→existingtypedassociationrowsUpdate — parents permit FKKeyShare, targetexcludesnewrefs and associationrowsstabilize removals; compatible with ownedaccount serialization and crossownercollectionbeforecontent order — cost if wrong: actual FK/rotation/recipient/association concurrency tests must reveal cycles; future writers must preserve protocol.
Ruling: Newunheldparent/association409 afterfreshscope and no lateearlierlocks; test newinserts BEFOREtargetlock, then finalcompleteancestry/currentauthority guards — targetUPDATE excludes later FKrefinserts; earliernewrefs require reload — cost if wrong: extra retry/recursion and statement-snapshot linearization.
Ruling: Existingtypedtrashentry with active ownedrecord is conflictingstate409; retryalreadytrashed404 withoutdeadlineextension — do not overwrite restorationhistory or create secondoperation — cost if wrong: futureUC51 must consume/reconcile typedentry before retrash.
Ruling: Queue trash/{operationGUID} using existing failclosed executor; actualrestore/expiry handler remainsUC51/53 beforeRelease — no physicalpurge is authorized by this recoverable endpoint; retainedrecord stays in complete rotationinventory — cost if wrong: missing later handler delays physicalpurge and must block release, never falselycomplete work.
Ruling: Retain conservative globalterminalGUID exclusions until typedrepair BEFORE ANY physicalpurge and nevernull selectedProfileId — known cross-kind collision safety prerequisite retained — cost if wrong: sameGUID acrossresource kinds can falselyhide untilrepair.
Ruling: Independent security/protocol and actualclient approvals remain development-onlydeferred under explicituser instruction; ordinaryreview/native/fullsuite/CI andreleasegates remain — cost if wrong: latentqualification defects await mandatory release approval.

Known shared generator Minors: required header/requestBody/outputfields not allmarkedrequired (inputexpectedRevisionISrequired); generated413ProblemDetails though testedactualempty. These are mandatory release contract cleanup, not evidence of runtime wrongbehavior. Grade by actualeffect, not silence. Every behavior considered and declined as outside scope MUST be enumerated with reason; emptylistonlyifnone. Check allFocus deliberately; name uncovered effects. Return precise Critical/Important/Minor and ready-to-merge judgment. Write full report to /tmp/cerberus-uc20-final-review.md (outside checkout); final message concise with reportpath and findings.

Use the complete requested template below; placeholders resolved by preceding paths/description/range:
# Code Reviewer Prompt Template

Use this template when dispatching a code reviewer subagent.

**Purpose:** Review completed work against requirements and code quality standards before it cascades into more work.

```
Subagent (general-purpose):
  description: "Review code changes"
  prompt: |
    You are a Senior Code Reviewer with expertise in software architecture,
    design patterns, and best practices. Your job is to review completed work
    against its plan or requirements and identify issues before they cascade.

    ## What Was Implemented

    [DESCRIPTION]

    ## Requirements / Plan

    [PLAN_OR_REQUIREMENTS]

    ## Git Range to Review

    **Base:** [BASE_SHA]
    **Head:** [HEAD_SHA]

    ```bash
    git diff --stat [BASE_SHA]..[HEAD_SHA]
    git diff [BASE_SHA]..[HEAD_SHA]
    ```

    ## The spec is a vision document

    The spec says what the software must do. It does not enumerate every
    input, environment, or condition the software will meet. For behavior
    the spec is silent on, judge by what a reasonable person using this
    software would expect: a reasonable person's expectation is a
    requirement, and a spec's silence is not permission. Grade such
    findings by their effect on that person, not by whether the spec
    mentions the trigger.

    ## Declined to judge

    Before your verdict, list every behavior you considered and set aside
    as outside the plan or spec, one line each, with the reason. The
    executor rules on each line; nothing you set aside is dropped
    silently. An empty list means you set nothing aside.

    ## Read-Only Review

    Your review is read-only on this checkout. Do not mutate the working tree, the index, HEAD, or branch state in any way. Use tools like `git show`, `git diff`, and `git log` to inspect history. If you need a working copy of a different revision, check it out into a separate temporary directory (e.g. `git worktree add /tmp/review-[SHA] [SHA]`) — never move HEAD on this checkout.

    ## You Do Not Dispatch Subagents

    Do all of this review yourself. Never spawn a subagent to review part
    of the diff, and never spawn another reviewer for a second opinion.
    This process already provides every review seat the work gets; a
    reviewer you spawn duplicates one of them at full cost, and its
    verdict counts for nothing. If the diff feels too large for one
    pass, review it in passes yourself and say so in your report.

    ## What to Check

    **Plan alignment:**
    - Does the implementation match the plan / requirements?
    - Are deviations justified improvements, or problematic departures?
    - Is all planned functionality present?

    **Code quality:**
    - Clean separation of concerns?
    - Proper error handling?
    - Type safety where applicable?
    - DRY without premature abstraction?
    - Edge cases handled?

    **Architecture:**
    - Sound design decisions?
    - Reasonable scalability and performance?
    - Security concerns?
    - Integrates cleanly with surrounding code?

    **Testing:**
    - Tests verify real behavior, not mocks?
    - Edge cases covered?
    - Integration tests where they matter?
    - All tests passing?

    **Production readiness:**
    - Migration strategy if schema changed?
    - Backward compatibility considered?
    - Documentation complete?
    - No obvious bugs?

    ## Calibration

    Categorize issues by actual severity. Not everything is Critical.
    Acknowledge what was done well before listing issues — accurate praise
    helps the implementer trust the rest of the feedback.

    If you find significant deviations from the plan, flag them specifically
    so the implementer can confirm whether the deviation was intentional.
    If you find issues with the plan itself rather than the implementation,
    say so.

    ## Output Format

    ### Strengths
    [What's well done? Be specific.]

    ### Issues

    #### Critical (Must Fix)
    [Bugs, security issues, data loss risks, broken functionality]

    #### Important (Should Fix)
    [Architecture problems, missing features, poor error handling, test gaps]

    #### Minor (Nice to Have)
    [Code style, optimization opportunities, documentation polish]

    For each issue:
    - File:line reference
    - What's wrong
    - Why it matters
    - How to fix (if not obvious)

    ### Recommendations
    [Improvements for code quality, architecture, or process]

    ### Assessment

    **Ready to merge?** [Yes | No | With fixes]

    **Reasoning:** [1-2 sentence technical assessment]

    ## Critical Rules

    **DO:**
    - Categorize by actual severity
    - Be specific (file:line, not vague)
    - Explain WHY each issue matters
    - Acknowledge strengths
    - Give a clear verdict

    **DON'T:**
    - Say "looks good" without checking
    - Mark nitpicks as Critical
    - Give feedback on code you didn't actually read
    - Be vague ("improve error handling")
    - Avoid giving a clear verdict
```

**Placeholders:**
- `[DESCRIPTION]` — brief summary of what was built
- `[PLAN_OR_REQUIREMENTS]` — what it should do (plan file path, task text, or requirements)
- `[BASE_SHA]` — starting commit
- `[HEAD_SHA]` — ending commit

**Reviewer returns:** Strengths, Issues (Critical / Important / Minor), Recommendations, Assessment

## Example Output

```
### Strengths
- Clean database schema with proper migrations (db.ts:15-42)
- Comprehensive test coverage (18 tests, all edge cases)
- Good error handling with fallbacks (summarizer.ts:85-92)

### Issues

#### Important
1. **Missing help text in CLI wrapper**
   - File: index-conversations:1-31
   - Issue: No --help flag, users won't discover --concurrency
   - Fix: Add --help case with usage examples

2. **Date validation missing**
   - File: search.ts:25-27
   - Issue: Invalid dates silently return no results
   - Fix: Validate ISO format, throw error with example

#### Minor
1. **Progress indicators**
   - File: indexer.ts:130
   - Issue: No "X of Y" counter for long operations
   - Impact: Users don't know how long to wait

### Recommendations
- Add progress reporting for user experience
- Consider config file for excluded projects (portability)

### Assessment

**Ready to merge: With fixes**

**Reasoning:** Core implementation is solid with good architecture and tests. Important issues (help text, date validation) are easily fixed and don't affect core functionality.
```

