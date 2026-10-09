# Contributing

Use one issue, one branch and one PR per use case. Human approval is the default
at the stages in [Development Workflow](docs/requirements/Development%20Workflow%20Document.md).
Unattended transitions require explicit scoped authorization.

## Review gates

Before releasing encrypted contracts, complete the security/interoperability protocol
review; the owner deferred it for development into `develop` only (see [Testing](#testing)). Before deploying the beta, select/pin stable dependencies, configure explicit retention
and limits, verify erasure-preserving restore and complete the documented privacy controls.
These are recorded decisions/gates, not assertions that implementation or compliance is complete.

## Prerequisites

Use a .NET SDK accepted by `global.json` and a Docker-compatible runtime for functional tests.
The helper scripts under `scripts/` need Python 3.

## Building

From the repository root:

```bash
dotnet restore src/ArturRios.Cerberus.sln
dotnet build src/ArturRios.Cerberus.sln --no-restore
```

## Testing

Follow [Testing Specification](docs/requirements/Testing%20Specification%20Document.md).
From the repository root:

```bash
dotnet test src/ArturRios.Cerberus.sln --filter "Category=Unit"
dotnet test src/ArturRios.Cerberus.sln --filter "Category=Functional"
dotnet test src/ArturRios.Cerberus.sln --collect:"XPlat Code Coverage"
python3 -m unittest discover -s scripts -p 'test_*.py'
python3 scripts/verify_specs.py
python3 scripts/openapi.py
dotnet tool install --global dotnet-reportgenerator-globaltool
python3 scripts/coverage.py
python3 scripts/vulnerabilities.py
docker build -t cerberus-api:local .
```

Current functional tests use disposable PostgreSQL containers and an in-memory HTTP host.
Every implemented use case must ship with main/alternative-flow tests. The required merged
line-coverage floor is 90%; branch coverage is reported alongside it, without a numerical gate.
`python3 scripts/verify_protocol.py` intentionally fails while the review record is pending.
CI requires it for promotion to `main` and version-tag releases. The owner explicitly
deferred independent review for development into `develop`
([deferral record](docs/security/development-review-deferral.json)); actual approval remains
pending and is still required before any release.
After reviewing a deliberate API contract change, run `python3 scripts/openapi.py --write`
and commit its result. OpenAPI includes each implemented business route. Generation starts
no server and requires no deployment credentials.

`scripts/verify_specs.py` also checks `README.md`: every use case must appear exactly once in its
backlog, the roadmap's milestone counts must add up, and its links and anchors must resolve. It
reads the backlog as everything between the `## Backlog` and `## Contributing` headings, and the
roadmap as everything between `## Roadmap` and `## Backlog`, so keep those headings.

## Branches

- Start `feature/<name>` or `fix/<name>` from an up-to-date `develop` and open the PR into `develop`.
- Use `feature/uc-##-use-case-name` for use cases; use lowercase names with digits, dots, underscores or hyphens.
- Merge feature/fix PRs by squash or merge commit. Dependabot PRs also target `develop`.
- Cut `release/x.y.z` from `develop` and open the PR into `main`. A release is a snapshot and carries no changes of its own; fixes land on `develop` first.
- Release PRs merge with a merge commit, and the merge commit is tagged `vx.y.z` — see [Releasing](#releasing).
- Delete merged work branches. Do not force push or delete `develop` or `main`.

## Commits and the changelog

Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/) with a lowercase
subject, e.g. `feat: add restore and retention repositories` or `fix: harden foundation review gates`.

Record every change an API client or an operator would notice under `## [Unreleased]` in
[CHANGELOG.md](./CHANGELOG.md), in the same pull request that makes it.

## Versioning

The API follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html). A release is numbered
`<major>.<minor>.<patch>`, and each part is measured against what clients and operators depend on:

- **Major** — a change an existing client or deployment has to adapt to: an endpoint, field,
  status code or error removed, renamed or changed in meaning; a change to the encryption protocol
  or the encrypted record format that existing clients cannot read or produce; a configuration
  setting removed or renamed, or a new one made mandatory; or a migration, or a change to the
  durable erasure files, that needs manual steps on an existing deployment.
- **Minor** — a backward-compatible addition: a new endpoint, an optional field or query
  parameter, a new optional setting, or a migration that applies on its own.
- **Patch** — a fix that changes no contract: a bug fix, a dependency upgrade, or a change to
  logging or performance.

The version is not stored in the source. It is the name of the release branch: `release/x.y.z`
releases `vx.y.z`. The Branch Policy check rejects a release branch whose version already has a
tag. The OpenAPI `info.version` in `docs/contracts/openapi.json` (`v1`) is the API contract
version and is independent of the release number. No release has been made yet.

## Required checks

The rulesets mirror the other API repositories: `branch-policy`, `test` and `docker` are required
on `develop` and `main`, and `main` additionally requires `deploy/production`. The repository
owner's administrator bypass is preserved. The rulesets require zero formal approvals; the
documented human review process still applies. Tags matching `v*` are protected against updates
and deletion.

The **Tests** workflow runs on every pull request into, and push to, `develop` and `main`, and on
`v*` tags. It validates the specifications, runs the helper scripts' tests, restores and builds the
solution, scans for vulnerable packages, checks the generated OpenAPI contract, runs the unit and
functional tests, enforces 90% merged line coverage, and builds the container image. On pushes to
and pull requests into `main`, and on tags, it also runs `verify_protocol.py`.

## Releasing

Because a release branch carries no changes of its own, finalize the changelog on `develop` before
cutting it: in a `feature/` branch, rename `## [Unreleased]` in [CHANGELOG.md](./CHANGELOG.md) to
`## [x.y.z] - <yyyy-mm-dd>` above a fresh, empty `## [Unreleased]`, update the links at the bottom,
and merge it into `develop`. Then cut `release/x.y.z` from `develop` and open its pull request into
`main`.

Deployment is not set up yet. Unlike `heimdall-api` and `fortuna-api`, this repository is not an
application in [yggdrasil](https://github.com/artur-rios/yggdrasil)'s `catalog.yaml` and has no
`Jenkinsfile`, so nothing deploys a release branch and nothing sets the `deploy/production` status
the `main` ruleset requires: a release pull request cannot pass its required checks. Before the
first release, register `cerberus-api` in the catalog and add a `Jenkinsfile` that calls the
yggdrasil pipeline, as the other API repositories do. From then on, pushing `release/x.y.z` deploys
it to homologation, and when every check on the release pull request passes, Jenkins deploys to
production, sets `deploy/production`, merges the pull request with a merge commit, and creates the
tag and GitHub release `vx.y.z`. This repository does not publish a synthetic `deploy/production`
status.

Reference: [Heimdall contribution policy](https://github.com/artur-rios/heimdall-api/blob/develop/CONTRIBUTING.md).
