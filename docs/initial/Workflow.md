# Workflow â€” Cerberus API

How a single use case is delivered, from picking it up to closing it out. Phase 2 will
formalize this approved workflow in `docs/requirements/Development Workflow Document.md`.

> **One use case = one branch = one issue = one pull request.**

The repository is [artur-rios/cerberus-api](https://github.com/artur-rios/cerberus-api).
GitHub issues and the associated public project track the approved backlog. Specifications remain in `docs/initial/` and
`docs/requirements/`; the project README belongs at the repository root.

## Invocation

Work starts when a use case is named by its identifier, for example `UC-03`. If the identifier
is missing or ambiguous, ask which one before implementation. The default workflow handles
one use case per pass.

## The golden rule: pause at every stage boundary

Human approval is the default:

- Present the refined design and implementation plan before writing product code.
- The only unattended status transition is `Todo â†’ In Progress`, once the branch exists
  and implementation begins.
- Ask before moving to `Testing`, before opening a pull request, and before moving to `Done`.
- A human reviews and merges the pull request and deletes its branch by default.

The user may explicitly authorize unattended transitions for a named run or scope. Record the
authorized transitions and automated verification criteria before executing them. Permission
to advance stages does not imply permission to merge, self-approve, close issues, or delete
branches; those actions need to be covered explicitly when an unattended run requires them.
Keep one issue, branch, and pull request per use case in unattended mode too.

When pausing, summarize the completed work, identify the next transition, and wait for the
required go-ahead. A review request concerns concrete work already prepared for inspection.

## Workflow overview

```text
Load specs â†’ Refine design and plan â†’ [approval] â†’ Branch + issue In Progress
  â†’ Implement â†’ [approval] â†’ issue Testing â†’ Verify until green â†’ [approval]
  â†’ Open PR â†’ [human review + merge + branch deletion] â†’ [approval] â†’ issue Done
```

Explicitly authorized unattended transitions replace only the gates included in that
authorization and still require passing verification.

---

## Step 1 â€” Load the specifications

Read the relevant documents from disk, including the target use case's actors, preconditions,
postconditions, main flow, and every `AF-xx` alternative flow. Do not work from memory.

The formal files are created in Phase 2. Once present, read:

| File under `docs/requirements/` | What to load |
| --- | --- |
| `Use Case Specification Document.md` | Target `UC-xx` and all its flows. |
| `System Requirements Document.md` | Traced functional requirements, data model, interfaces, and permissions. |
| `Development Workflow Document.md` | Normative stages, gates, branch pattern, and Definition of Done. |
| `Testing Specification Document.md` | Test conventions, required coverage, and suite commands. |
| `Technology Stack Document.md` | Packages, versions, compatibility, and implementation patterns. |
| `Operations & Infrastructure Document.md` | Applicable infrastructure and deployment requirements. |

Then locate the use case's issue and confirm its scope and dependencies.

## Step 2 â€” Refine the design and plan

1. Turn the traced requirements into a repository-specific design: components, interfaces,
   validators, domain rules, authorization, encrypted contracts, and error responses for all
   alternative flows.
2. Write an implementation plan that sequences meaningful behavior tests with implementation,
   following the Testing Specification.
3. Present the design and plan for approval before writing product code, unless this transition
   is explicitly included in the user's unattended authorization.

Do not redesign approved business rules during implementation without identifying the proposed
specification change.

## Step 3 â€” Branch and move the issue to In Progress

Use an up-to-date `develop` as the base. The branch pattern follows the established Heimdall API
convention confirmed by the user:

```text
feature/uc-##-use-case-name
```

For example:

```bash
git switch develop
git pull
git switch -c feature/uc-01-create-account
```

The example illustrates naming, not an assignment of `UC-01`; Phase 2 defines the actual use
case identifiers. Preserve the existing repository selection and uncommitted user work before
switching branches. These commands use the configured GitHub remote.

Once the branch exists and work begins, move the issue to `In Progress`. No other default
status transition is unattended.

## Step 4 â€” Implement

Execute the approved plan on the use case's branch. Implement the main flow and every
alternative flow, including ownership and shared-collection authorization checks. Develop
the meaningful tests with the implementation and commit reviewable changes as appropriate.

Use approved libraries and repository patterns. Do not silently add services, weaken
end-to-end encryption, or expand an issue beyond its traced requirements.

## Step 5 â€” Pause for review before Testing

When implementation is code-complete, present what changed and request approval to move the
issue to `Testing`. Perform the transition only after approval or an explicit unattended
authorization covering it.

## Step 6 â€” Test until green

Finish all required tests and run them against the approved stack. Cover each handler and
domain behavior with unit tests, and each endpoint with functional tests against a real
PostgreSQL Testcontainers database. Assert responses and resulting database state.

Use Given/When/Then names, the family test helpers, Moq, Bogus, and coverage tooling as defined
in [Technology Stack](Technology%20Stack.md). Follow Heimdall's line-coverage floor. Test main
and applicable alternative flows, access denial, sharing boundaries, recovery consumption,
trash/purge behavior, and synchronization where relevant to the use case.

From the solution directory once the project is scaffolded, run:

```text
dotnet test --filter "Category=Unit"
dotnet test --filter "Category=Functional"
```

These are intended commands for the scaffolded solution. Phase 2 will define the actual
solution directory and coverage command.

Fix failures and rerun the affected checks until the required suite passes. Report fresh
verification results. Request approval before opening a pull request unless that transition
was explicitly authorized unattended.

## Step 7 â€” Open the pull request after approval

Push the branch and open a pull request into `develop` referencing the issue so a successful
merge closes it. Explain the resulting behavior, relevant design choices, and validation.
Attach the created pull request to the active Codex chat when working through Codex.

Hand off for human review and merge by default. Address review changes on the same branch,
rerun the required checks, and keep the issue's status accurate. Merge or branch deletion by
an agent requires explicit authorization for those actions.

## Step 8 â€” Close out after merge

After the reviewed pull request is merged and the branch deleted, ask before moving the issue
to `Done`, unless explicitly authorized to make that transition unattended. Confirm the issue
is closed and update the repository-root README's backlog entry when it tracks the use case.

---

## Branch protections and releases

Feature and fix branches start from `develop` and target `develop`; accepted names are
`feature/<name>`, `fix/<name>` and Dependabot branches. Releases use `release/x.y.z`
snapshots of `develop` and target `main` with merge commits only. Both branches reject
force pushes and deletion, require `branch-policy`, `test` and `docker`, and retain
Heimdall's administrator bypass. `main` also requires `deploy/production`; the deployment
integration must be provisioned before a release. Release tags matching `v*` cannot be
updated or deleted except through the same administrator bypass. Human review remains
the default process; the copied rulesets do not mandate an approval count.

See the repository [contribution policy](../../CONTRIBUTING.md) for release details.

## Definition of Done

- [ ] Implemented on its own `feature/uc-##-use-case-name` branch from `develop`.
- [ ] Main flow and every alternative flow implemented.
- [ ] Unit and functional tests cover the use case at the correct layers.
- [ ] Required suites and the adopted coverage gate pass with fresh evidence.
- [ ] Encryption, ownership, and sharing rules remain satisfied.
- [ ] Pull request reviewed by a human and merged, or verified and merged under explicit
      unattended authorization covering that action.
- [ ] Branch deleted by the human or explicitly authorized agent.
- [ ] Issue closed and in `Done`, with the required transition authorization.
- [ ] README backlog status updated when the README tracks this use case.

Reference convention: [Heimdall Development Workflow](https://github.com/artur-rios/heimdall-api/blob/main/docs/requirements/Development%20Workflow%20Document.md).
