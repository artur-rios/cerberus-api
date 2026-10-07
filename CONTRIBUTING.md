# Contributing

Use one issue, one branch and one PR per use case. Human approval is the default
at the stages in [Development Workflow](docs/requirements/Development%20Workflow%20Document.md).
Unattended transitions require explicit scoped authorization.

## Branches

- Start `feature/<name>` or `fix/<name>` from an up-to-date `develop` and open the PR into `develop`.
- Use `feature/uc-##-use-case-name` for use cases; use lowercase names with digits, dots, underscores or hyphens.
- Merge feature/fix PRs by squash or merge commit. Dependabot PRs also target `develop`.
- Cut `release/x.y.z` from `develop` and open the PR into `main`. A release is a snapshot and carries no changes of its own; fixes land on `develop` first.
- Merge release PRs with a merge commit, then tag the successful release `vx.y.z`.
- Delete merged work branches. Do not force push or delete `develop` or `main`.

## Required checks and releases

The rulesets mirror Heimdall: `branch-policy`, `test` and `docker` are required on both branches;
`main` additionally requires `deploy/production`. Administrator bypass is preserved.
The copied rulesets require zero formal approvals; the documented human review process
still applies. Tags matching `v*` are protected against updates and deletion.

CI validates the specifications during the documentation-only stage. Once the foundation
adds the solution and Dockerfile, it restores/builds, scans vulnerable packages, runs
unit and functional tests, enforces 90% merged line coverage and builds the container.

Heimdall's release process deploys staging, verifies it, then deploys production and
publishes `deploy/production` before merging/tagging. Cerberus must register and configure
its deployment integration before its first release. This repository does not publish
a synthetic production status or deploy automatically during initialization.

Reference: [Heimdall contribution policy](https://github.com/artur-rios/heimdall-api/blob/develop/CONTRIBUTING.md).
