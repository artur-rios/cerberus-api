# UC-35 Grant collection access implementation plan

> Execute with test-driven development and the repository's subagent-driven
> workflow. Each task receives a fresh implementer and then a fresh task reviewer;
> finish with one whole-branch reviewer before delivery.

**Goal:** Implement POST `/api/collections/{id}/grants` for strict initial grant
creation and explicit revoked-grant reactivation, using the audited ordered
dual-account lock protocol and native recipient-envelope binding.

**Architecture:** Add collection-grant domain contracts and an application command
boundary, then a dedicated PostgreSQL transaction store, and finally expose the
route with HTTP/docs/OpenAPI integration. Reuse the current grant schema,
`RecipientEnvelope`, `CollectionGrantBinding`, strict request middleware and
collection authority conventions. No migration or new dependency.

**Base:** `e6ea84cebd166d897fa5613f3e2c3ab7171f213a`

## Task 1: Domain and application contract

**Files**

- Add `src/Domain/ArturRios.Cerberus.Domain/Collections/CollectionGrantContracts.cs`.
- Add `src/Application/ArturRios.Cerberus.Command/Collections/GrantCollectionAccessCommand.cs`.
- Add handler, validator, output and messages beside the command.
- Add focused command tests.

**TDD steps**

1. Write failing tests for all five required inputs, explicit-null versus positive
   regrant CAS, access parsing, envelope/grant revision consistency, trusted path
   context, authentication/access precedence and safe error mapping.
2. Add immutable domain request/details/store contracts and strict validators.
3. Implement the handler's exact output validation: collection/grant identity,
   expected revision result, access and sorted unique record/folder arrays.
4. Run focused tests, then the entire Command family. Commit and task-review.

## Task 2: Guarded PostgreSQL grant writer

**Files**

- Add `src/Infrastructure/ArturRios.Cerberus.Data/Collections/CollectionGrantStore.cs`.
- Add focused real-PostgreSQL store tests.

**TDD steps**

1. Start with red initial RO/RW and revoked-regrant tests.
2. Implement target-first authority classification, relevant native foreign denial,
   and the audited single-statement public-ID-ordered owner/recipient `FOR UPDATE`.
3. Add session, selected profile, target collection and captured grant locks in the
   documented order, rechecking authority after each wait.
4. Validate current owner/recipient protection and caller envelope binding, then
   implement guarded insert/reactivation and post-write verification.
5. Add exact member projection without profile leakage and no unrelated mutation.
6. Grow red/green coverage for duplicates, stale/mismatched regrant, collisions,
   terminal/lifecycle/protection/native corruption, every fault stage and rollback.
7. Add actual reciprocal, recipient-writer, membership/delete/rotation and same-pair
   race tests, lock capture/order assertions and bounded target query plans.
8. Run focused tests, then the entire Data family. Commit and task-review.

## Task 3: HTTP route, integration, documents and exhaustive verification

**Files**

- Update `CollectionsController` and dependency registrations.
- Add actual HTTP tests for strict transport and native end-to-end visibility.
- Update OpenAPI, collection API, foundation status, changelog and README tracker.

**TDD steps**

1. Add a failing route test and the exact 201/error contract tests.
2. Register validator/handler/store and add the POST action using the shared strict
   body pipeline and canonical path/header/query checks.
3. Cover invalid media/UTF-8/BOM/compression, duplicate/unknown/case-forged fields,
   nullable CAS forms, canonical IDs, both access modes and all status mappings.
4. Prove real recipient collection/record/folder GET/list behavior, selected-profile
   isolation, regrant, foreign-owner denial, pin/signature/epoch/revision failures,
   exact response fields, no profile/key leakage and no-store/empty-413 behavior.
5. Update generated OpenAPI and operational/security documentation; mark UC-35 Done
   only in the feature content that will merge with the completed implementation.
6. Run focused Web tests, all six test families and coverage, native suites, locked
   restore, dependency audit, specs, OpenAPI drift/shape and whitespace checks.
7. Commit and task-review.

## Final review and delivery

1. Dispatch one fresh whole-branch reviewer over base-to-head with actual evidence
   and all five design priorities. Resolve every Critical/Important finding and
   record accepted minors/rulings in the tracked ledger.
2. Transition issue #36 to Testing, push the branch, open exactly one PR closing
   #36, attach it to the chat and verify unchanged base/head.
3. Wait for required exact-head `branch-policy`, `test` and `docker` checks. Merge
   normally with squash, branch deletion and exact-head guard only after all pass.
4. Verify the squash tree equals the reviewed tree, issue/project are Done, local
   and remote branches are gone, and `develop` equals `origin/develop`.
5. Save the completion checkpoint, preserve the tracked ledger, clean only ignored
   UC-35 scratch artifacts, and continue the authorized sequential batch.

