# SDD ledger - plan: docs/superpowers/plans/2026-10-12-uc-42-grant-software-access.md

## Authorization and binding

- User approved the UC-42 design, written implementation plan, isolated worktree, recommended subagent-driven execution, and cleanup of feature branches/worktree workflow artifacts after completion.
- Branch: `feature/uc-42-grant-software-access`.
- Base: `d730c91c1b3577802fc12568959054900d14b71d`.
- Delivery target: GitHub issue #43 and its existing project #15 item only.

## Dependency preflight

| Producer | Consumer | Boundary | Finding |
|---|---|---|---|
| Task 1 | Task 2 | `IHeimdallClient.VerifyApplicationAsync` and `HeimdallApplicationStatus` | Exact signature and status semantics agree. |
| Task 2 | Task 4 | Domain request/result/store interfaces | Exact names, types and nullable revision semantics agree. |
| Task 2 | Task 5 | Command/output/messages | The controller and two-field response consume the stable boundary. |
| Task 3 | Task 4 | Normalized entities, mappings and DbSets | The store consumes the exact schema Task 3 produces. |
| Task 4 | Task 5 | Store implementation and DI registration | HTTP composition consumes one atomic persistence path. |
| Task 1 | Task 1 | Tests and implementation | Consistent after adding the complete inspected Heimdall application response shape. |
| Task 2 | Task 2 | Tests and implementation | Consistent; cryptographic verification belongs to the store task. |
| Task 3 | Task 3 | Tests and implementation | Consistent; same-account composite foreign keys enforce ownership. |
| Task 4 | Task 4 | Tests and implementation | Consistent; canonical locks and aggregate replacement are both exercised. |
| Task 5 | Task 5 | Tests and implementation | Consistent; generated OpenAPI uses the verified `--write` switch. |

## Rulings

- Ruling: Use `d730c91c1b3577802fc12568959054900d14b71d` as the branch base because the fetched post-UC-37 change is limited to attribution-license/WakaTime files. Cost if wrong: another semantic change hidden in that commit would require rebase and renewed review.
- Ruling: Physical cascades are allowed only at existing terminal account/target/grant removal boundaries; logical trash and revocation must preserve the aggregate until their own state transitions. Cost if wrong: later lifecycle work may require an explicit audit-preservation migration.
- Ruling: Linux is the binding verification host. The Windows test run exposed the repository's existing CNG, durable-erasure, path, and resource-limit incompatibilities; the isolated Linux SDK run passed Domain (361), Shared (125), Query (606), and Command (1,753), and the exact base SHA passed the repository's GitHub Actions `Tests` workflow (run 38296953001). Cost if wrong: a Windows-specific regression would not be represented by the project-supported Linux evidence.
- Ruling: Existing protected-endpoint middleware must revalidate the current human before controller body validation; malformed authenticated grant requests therefore guarantee zero application-verifier and store calls, not zero total Heimdall calls. Cost if wrong: bypassing the middleware would weaken the established authorization boundary.

## Task status

- Task 1: complete at `984fc72e85716d1562cdd9109b64474d97a2a8b4`; independent review APPROVED with no findings; Linux Shared suite 180/180 passed.
- Task 2: complete at `42cead548a5f4122d49509d3353116d7544e0520`; independent review APPROVED with no findings; Linux Command suite 1,864/1,864 passed.
- Task 3: complete at `17714325e3c73023eb93a34082573c3e26030196`; independent review APPROVED with no findings; focused schema/migration 10/10 and full Linux solution 9,875/9,875 passed; EF reports no pending model changes.
- Task 4: complete at `61945f4170d04a7f2c2935dbcd5888ea656c2f72`; independent review APPROVED with no findings; focused real-PostgreSQL store/concurrency suite 147/147 and full Linux solution 10,022/10,022 passed.
- Task 5: complete at `6a7894f116120899d24af3152e72242b1cefe479`; independent re-review APPROVED after correcting generated OpenAPI media types; focused 96/96 and full Web 3,132/3,132 passed at corrected head. Original full solution evidence at `077cf0e` is 10,117/10,117 with 98.6% line and 92.2% branch coverage.

## Whole-branch review

- Exact range `d730c91c1b3577802fc12568959054900d14b71d..ea785221ff1cdf3e802c9be615a39615ac5bd6d0` received an independent APPROVED verdict with no remaining findings after the captured shell-output cleanup.
- Fresh reviewer evidence: 434/434 focused Linux/PostgreSQL tests, 571/571 source hashes, specification checks, whitespace checks and clean tracked status.
- Delivery remains behind Gate 2: no Testing transition, push or pull request without explicit user approval.
