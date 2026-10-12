# UC-36 Change collection access implementation plan

> Execute with test-driven development and the repository's subagent-driven
> workflow. Each task receives a fresh implementer and then a fresh task reviewer;
> finish with one whole-branch reviewer before delivery.

**Goal:** Implement PUT `/api/collections/{id}/grants/{grantId}` for strict active
grant permission changes, native next-revision binding and atomic recipient
revocation-generation invalidation.

**Architecture:** Add grant-change domain/application contracts, then a dedicated
PostgreSQL transaction store, and finally expose the route with HTTP/docs/OpenAPI
integration. Reuse `RecipientEnvelope`, `CollectionGrantBinding`, the UC-35
ordered dual-account protocol, strict request middleware and collection authority
conventions. No migration or new dependency.

**Base:** `e9f58936eab3723ad82dfe06f44c7667585bb284`

## Task 1: Domain and application contract

**Files**

- Add `CollectionGrantChangeContracts.cs` beside the collection grant contracts.
- Add change command, validator, handler, output and messages beside UC-35.
- Add focused command tests.

**TDD steps**

1. Write failing tests for exact three-field input, positive safe expected revision,
   next-revision envelope agreement, both access values, trusted path context,
   authentication/access precedence and safe error mapping.
2. Add immutable domain request/details/store contracts and strict validators.
3. Implement exact store-output validation for path collection/grant identity,
   expected revision plus one, requested access and sorted unique direct roots.
4. Run focused tests, then the complete Command family. Commit and task-review.

## Task 2: Atomic PostgreSQL permission writer

**Files**

- Add `CollectionGrantChangeStore.cs`.
- Add focused real-PostgreSQL store tests.

**TDD steps**

1. Start with red read-only-to-read-write and read-write-to-read-only tests.
2. Implement target-first owner/foreign classification and exact active path-grant
   capture without locking a denied foreign request's requested grant.
3. Lock owner/recipient accounts together in UUID order, then owner session,
   selected profile, collection and exact grant, rechecking authority after waits.
4. Validate current owner/recipient protection, collection binding and existing
   active grant; validate the replacement envelope at expected revision plus one.
5. Add one guarded atomic grant CAS plus recipient-generation increment, followed
   by exact grant/generation/direct-root verification before commit.
6. Grow red/green coverage for hidden/revoked/cross-collection/terminal grants,
   stale/no-op/exhausted state, recipient lifecycle, stored/caller native failures,
   every mutation/projection fault, rollback and cancellation.
7. Add actual same-grant and reciprocal races, recipient session issuance and
   lifecycle/protection writers in both acquisition orders, exact lock capture,
   one-grant/one-generation mutation and bounded target queries.
8. Run focused tests, then the complete Data family. Commit and task-review.

## Task 3: HTTP route, integration, documents and exhaustive verification

**Files**

- Update `CollectionsController` and dependency registrations.
- Add real HTTP tests for strict transport and online permission enforcement.
- Update OpenAPI, collection API, foundation status, changelog and README tracker.

**TDD steps**

1. Add a failing route test and exact 200/error contract tests.
2. Register validator/handler/store and add the PUT action through the shared strict
   body pipeline with canonical collection/grant paths and header/query checks.
3. Cover invalid media/UTF-8/BOM/compression, duplicate/unknown/case-forged fields,
   revision/access/envelope forms, canonical IDs and all status mappings.
4. Prove both permission directions with real native envelopes: old recipient
   handles become stale, a new unlock preserves reads, read-write permits member
   updates and read-only rejects them; cover selected isolation and regrant state.
5. Verify exact response fields, no identity/profile/pin/key leakage and complete
   rollback/no-store/empty-413 behavior.
6. Update generated OpenAPI and operational/security documentation; mark UC-36
   Done only in feature content that merges with the completed implementation.
7. Run focused Web tests, all six test families and coverage, native suites, locked
   restore, dependency audit, specs, OpenAPI drift/shape and whitespace checks.
8. Commit and task-review.

## Final review and delivery

1. Dispatch one fresh whole-branch reviewer over base-to-head with actual evidence
   and all five design priorities. Resolve every Critical/Important finding and
   record accepted minors/rulings in the tracked ledger.
2. Transition issue #37 to Testing, push the branch, open exactly one PR closing
   #37, attach it to the chat and verify unchanged base/head.
3. Wait for exact-head `branch-policy`, `test` and `docker` checks. Merge normally
   with squash, branch deletion and exact-head guard only after all pass.
4. Verify the squash tree equals the reviewed tree, issue/project are Done, local
   and remote branches are gone, and `develop` equals `origin/develop`.
5. Save the completion checkpoint, preserve the tracked ledger, clean only ignored
   UC-36 scratch artifacts, and continue the authorized sequential batch.
