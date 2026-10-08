# Cerberus API

An end-to-end encrypted vault API for credentials, passwords, notes and custom sensitive
records. Cerberus manages encrypted storage, organization and resource permissions; Heimdall
handles identity, and authorized clients encrypt/decrypt locally.

> **Status:** foundation infrastructure and UC-01 account registration implemented; 54 business
> use cases remain. Independent protocol review is explicitly deferred for development and
> remains required before release. See [development review deferral](docs/security/development-review-deferral.json)
> and [foundation status](docs/operations/foundation-status.md).

## Planned capabilities

- Distinct Cerberus accounts and vault profiles integrated with Heimdall identity.
- Named custom/template records, nested folders and multi-profile collections.
- Read-only/read/write collection sharing and explicit software secret grants.
- Client-held keys, one-time refreshable recovery and profile-specific protection.
- Latest-edit synchronization and configurable offline renewal: initially 24 hours, editable or disabled.
- Recoverable 30-day trash/account closure, immediate purge, privacy export and operational health.

All approved capabilities are required for the first release; the project owner is the beta user.

## What it doesn't do

- Build the Flutter or other client applications in this repository.
- Decrypt vault content on the server or replace Heimdall identity.
- Add another external runtime application service.
- Promise immediate remote revocation on disconnected clients or retract copies already made.

## Specifications

Start with the initial context, then the normative formal requirements.

| Document | What it contains |
| --- | --- |
| [Brainstorm](docs/initial/Brainstorm.md) | Original source notes, preserved unchanged. |
| [Project Overview](docs/initial/Project%20Overview.md) | Scope, audience and success criteria. |
| [Technology Stack](docs/initial/Technology%20Stack.md) | Initial choices and full library assessment. |
| [Workflow](docs/initial/Workflow.md) | Approved delivery gates and unattended authorization rules. |
| [Business Rules](docs/initial/Business%20Rules.md) | BR-01 through BR-29 and domain relationships. |
| [Vision Document](docs/requirements/Vision%20Document.md) | Features and product constraints. |
| [System Requirements Document](docs/requirements/System%20Requirements%20Document.md) | Requirements, data model, interfaces and authorization. |
| [Use Case Specification Document](docs/requirements/Use%20Case%20Specification%20Document.md) | All use cases, main flows and alternative flows. |
| [Development Workflow Document](docs/requirements/Development%20Workflow%20Document.md) | Normative stage gates and Definition of Done. |
| [Testing Specification Document](docs/requirements/Testing%20Specification%20Document.md) | Unit/functional coverage and commands. |
| [Technology Stack Document](docs/requirements/Technology%20Stack%20Document.md) | Single technology/version policy source. |
| [Operations & Infrastructure Document](docs/requirements/Operations%20%26%20Infrastructure%20Document.md) | Scaffold, configuration, CI, health, retention and restore controls. |

## Installation

The `develop` branch contains the .NET 10 solution; `main` holds only the specifications until the
first release. Use an SDK accepted by `global.json` and a Docker-compatible runtime for functional
tests. Starting the development host requires
explicit database, Heimdall and operational configuration; no production defaults are supplied.
The container is a development artifact, not a release-ready vault API.
See [operational settings](docs/operations/proposed-beta-settings.md), the nonsecret
[configuration example](docs/operations/appsettings.example.json),
[Heimdall contracts](docs/operations/heimdall.md) and [restore runbook](docs/operations/restore.md).
Copy the reviewed nonsecret settings into the host's `appsettings.json` or supply named
`CERBERUS_...` environment keys, which take precedence over `Cerberus:Property` values.
Supply all credentials and identity scope separately through protected configuration.
Registration requires an independent durable `CERBERUS_REGISTRATION_FINGERPRINT_KEY`;
preserve it across replicas, restores and routine identity-signing-key rotation.

From the repository root:

```bash
dotnet restore src/ArturRios.Cerberus.sln
dotnet build src/ArturRios.Cerberus.sln --no-restore
dotnet run --project src/Presentation/ArturRios.Cerberus.WebApi -- --validate-configuration
dotnet run --project src/Presentation/ArturRios.Cerberus.WebApi -- --migrate
dotnet run --project src/Presentation/ArturRios.Cerberus.WebApi
```

Clone the repository with `git clone https://github.com/artur-rios/cerberus-api.git`.

## Roadmap

Track delivery on the public [Cerberus API project](https://github.com/users/artur-rios/projects/15).
Each use case has one issue; the foundation issue comes first. Every milestone depends on
Foundation, with dependencies only on earlier milestones. Counts below are updated as issues close;
GitHub milestone pages show live progress. No due dates or labels were assigned.

| Milestone | Delivers | Depends on | Issues | Status |
| --- | --- | --- | --- | --- |
| [M-01 — Foundation](https://github.com/artur-rios/cerberus-api/milestone/1) | Scaffold, persistence, identity adapters, tests, CI and safe operational primitives. | — | 1 | 1 / 1 closed |
| [M-02 — Account identity and vault protection](https://github.com/artur-rios/cerberus-api/milestone/2) | Register/login, manage account details and protect/recover the vault. | M-01 | 9 | 9 / 9 closed |
| [M-03 — Organized vault](https://github.com/artur-rios/cerberus-api/milestone/3) | Manage profiles, custom records, nested folders and collection membership. | M-01, M-02 | 26 | 5 / 26 closed |
| [M-04 — Controlled sharing and software secrets](https://github.com/artur-rios/cerberus-api/milestone/4) | Share selected collections and serve explicitly granted software ciphertext. | M-01, M-02, M-03 | 6 | 0 / 6 closed |
| [M-05 — Offline synchronization](https://github.com/artur-rios/cerberus-api/milestone/5) | Apply configurable renewal, incremental access changes and deterministic offline edits. | M-01, M-02, M-03, M-04 | 5 | 0 / 5 closed |
| [M-06 — Recoverable deletion and beta operations](https://github.com/artur-rios/cerberus-api/milestone/6) | Deliver account closure/erasure, trash restoration/expiry, privacy export and operational health. | M-01, M-02, M-03, M-04, M-05 | 9 | 0 / 9 closed |

## Backlog

### M-01 — Foundation

| Issue | Work | Spec | Status |
| --- | --- | --- | --- |
| [#1](https://github.com/artur-rios/cerberus-api/issues/1) | Project scaffold and initial infrastructure | [Operations & Infrastructure](docs/requirements/Operations%20%26%20Infrastructure%20Document.md) | Done |

### M-02 — Account identity and vault protection

| Issue | Work | Spec | Status |
| --- | --- | --- | --- |
| [#2](https://github.com/artur-rios/cerberus-api/issues/2) | UC-01 — Register account | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-01-register-account) | Done |
| [#3](https://github.com/artur-rios/cerberus-api/issues/3) | UC-02 — Authenticate | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-02-authenticate) | Done |
| [#4](https://github.com/artur-rios/cerberus-api/issues/4) | UC-03 — Get account | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-03-get-account) | Done |
| [#5](https://github.com/artur-rios/cerberus-api/issues/5) | UC-04 — Update account | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-04-update-account) | Done |
| [#6](https://github.com/artur-rios/cerberus-api/issues/6) | UC-05 — Update identity details | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-05-update-identity-details) | Done |
| [#39](https://github.com/artur-rios/cerberus-api/issues/39) | UC-38 — Initialize vault protection | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-38-initialize-vault-protection) | Done |
| [#40](https://github.com/artur-rios/cerberus-api/issues/40) | UC-39 — Change vault protection | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-39-change-vault-protection) | Done |
| [#41](https://github.com/artur-rios/cerberus-api/issues/41) | UC-40 — Recover vault access | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-40-recover-vault-access) | Done |
| [#42](https://github.com/artur-rios/cerberus-api/issues/42) | UC-41 — Refresh recovery key | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-41-refresh-recovery-key) | Done |

### M-03 — Organized vault

| Issue | Work | Spec | Status |
| --- | --- | --- | --- |
| [#10](https://github.com/artur-rios/cerberus-api/issues/10) | UC-09 — Create profile | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-09-create-profile) | Done |
| [#11](https://github.com/artur-rios/cerberus-api/issues/11) | UC-10 — List profiles | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-10-list-profiles) | Done |
| [#12](https://github.com/artur-rios/cerberus-api/issues/12) | UC-11 — Get profile | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-11-get-profile) | Done |
| [#13](https://github.com/artur-rios/cerberus-api/issues/13) | UC-12 — Update profile | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-12-update-profile) | Done |
| [#14](https://github.com/artur-rios/cerberus-api/issues/14) | UC-13 — Delete profile | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-13-delete-profile) | Done |
| [#15](https://github.com/artur-rios/cerberus-api/issues/15) | UC-14 — Set profile associations | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-14-set-profile-associations) | Todo |
| [#16](https://github.com/artur-rios/cerberus-api/issues/16) | UC-15 — Open profile access | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-15-open-profile-access) | Todo |
| [#17](https://github.com/artur-rios/cerberus-api/issues/17) | UC-16 — Create record | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-16-create-record) | Todo |
| [#18](https://github.com/artur-rios/cerberus-api/issues/18) | UC-17 — List records | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-17-list-records) | Todo |
| [#19](https://github.com/artur-rios/cerberus-api/issues/19) | UC-18 — Get record | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-18-get-record) | Todo |
| [#20](https://github.com/artur-rios/cerberus-api/issues/20) | UC-19 — Update record | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-19-update-record) | Todo |
| [#21](https://github.com/artur-rios/cerberus-api/issues/21) | UC-20 — Delete record | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-20-delete-record) | Todo |
| [#22](https://github.com/artur-rios/cerberus-api/issues/22) | UC-21 — Move record | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-21-move-record) | Todo |
| [#23](https://github.com/artur-rios/cerberus-api/issues/23) | UC-22 — Permanently delete record | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-22-permanently-delete-record) | Todo |
| [#24](https://github.com/artur-rios/cerberus-api/issues/24) | UC-23 — Create folder | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-23-create-folder) | Todo |
| [#25](https://github.com/artur-rios/cerberus-api/issues/25) | UC-24 — List folders | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-24-list-folders) | Todo |
| [#26](https://github.com/artur-rios/cerberus-api/issues/26) | UC-25 — Get folder | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-25-get-folder) | Todo |
| [#27](https://github.com/artur-rios/cerberus-api/issues/27) | UC-26 — Update folder | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-26-update-folder) | Todo |
| [#28](https://github.com/artur-rios/cerberus-api/issues/28) | UC-27 — Delete folder | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-27-delete-folder) | Todo |
| [#29](https://github.com/artur-rios/cerberus-api/issues/29) | UC-28 — Move folder | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-28-move-folder) | Todo |
| [#30](https://github.com/artur-rios/cerberus-api/issues/30) | UC-29 — Create collection | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-29-create-collection) | Todo |
| [#31](https://github.com/artur-rios/cerberus-api/issues/31) | UC-30 — List collections | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-30-list-collections) | Todo |
| [#32](https://github.com/artur-rios/cerberus-api/issues/32) | UC-31 — Get collection | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-31-get-collection) | Todo |
| [#33](https://github.com/artur-rios/cerberus-api/issues/33) | UC-32 — Update collection | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-32-update-collection) | Todo |
| [#34](https://github.com/artur-rios/cerberus-api/issues/34) | UC-33 — Delete collection | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-33-delete-collection) | Todo |
| [#35](https://github.com/artur-rios/cerberus-api/issues/35) | UC-34 — Set collection membership | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-34-set-collection-membership) | Todo |

### M-04 — Controlled sharing and software secrets

| Issue | Work | Spec | Status |
| --- | --- | --- | --- |
| [#36](https://github.com/artur-rios/cerberus-api/issues/36) | UC-35 — Grant collection access | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-35-grant-collection-access) | Todo |
| [#37](https://github.com/artur-rios/cerberus-api/issues/37) | UC-36 — Change collection access | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-36-change-collection-access) | Todo |
| [#38](https://github.com/artur-rios/cerberus-api/issues/38) | UC-37 — Revoke collection access | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-37-revoke-collection-access) | Todo |
| [#43](https://github.com/artur-rios/cerberus-api/issues/43) | UC-42 — Grant software access | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-42-grant-software-access) | Todo |
| [#44](https://github.com/artur-rios/cerberus-api/issues/44) | UC-43 — Retrieve software secrets | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-43-retrieve-software-secrets) | Todo |
| [#45](https://github.com/artur-rios/cerberus-api/issues/45) | UC-44 — Revoke software access | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-44-revoke-software-access) | Todo |

### M-05 — Offline synchronization

| Issue | Work | Spec | Status |
| --- | --- | --- | --- |
| [#46](https://github.com/artur-rios/cerberus-api/issues/46) | UC-45 — Get offline policy | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-45-get-offline-policy) | Todo |
| [#47](https://github.com/artur-rios/cerberus-api/issues/47) | UC-46 — Set offline policy | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-46-set-offline-policy) | Todo |
| [#48](https://github.com/artur-rios/cerberus-api/issues/48) | UC-47 — Renew offline authorization | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-47-renew-offline-authorization) | Todo |
| [#49](https://github.com/artur-rios/cerberus-api/issues/49) | UC-48 — Download synchronization changes | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-48-download-synchronization-changes) | Todo |
| [#50](https://github.com/artur-rios/cerberus-api/issues/50) | UC-49 — Upload offline edits | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-49-upload-offline-edits) | Todo |

### M-06 — Recoverable deletion and beta operations

| Issue | Work | Spec | Status |
| --- | --- | --- | --- |
| [#7](https://github.com/artur-rios/cerberus-api/issues/7) | UC-06 — Request account closure | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-06-request-account-closure) | Todo |
| [#8](https://github.com/artur-rios/cerberus-api/issues/8) | UC-07 — Cancel account closure | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-07-cancel-account-closure) | Todo |
| [#9](https://github.com/artur-rios/cerberus-api/issues/9) | UC-08 — Permanently delete account | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-08-permanently-delete-account) | Todo |
| [#51](https://github.com/artur-rios/cerberus-api/issues/51) | UC-50 — List trash | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-50-list-trash) | Todo |
| [#52](https://github.com/artur-rios/cerberus-api/issues/52) | UC-51 — Restore trash entry | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-51-restore-trash-entry) | Todo |
| [#53](https://github.com/artur-rios/cerberus-api/issues/53) | UC-52 — Empty trash | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-52-empty-trash) | Todo |
| [#54](https://github.com/artur-rios/cerberus-api/issues/54) | UC-53 — Expire retained deletions | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-53-expire-retained-deletions) | Todo |
| [#55](https://github.com/artur-rios/cerberus-api/issues/55) | UC-54 — Export account data | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-54-export-account-data) | Todo |
| [#56](https://github.com/artur-rios/cerberus-api/issues/56) | UC-55 — Check API health | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-55-check-api-health) | Todo |

## Changelog

Notable changes in each release are recorded in [CHANGELOG.md](./CHANGELOG.md).

## Contributing

Building from source, running the tests, the delivery workflow and its review gates, the branching
model and the release process are described in [CONTRIBUTING.md](./CONTRIBUTING.md).
