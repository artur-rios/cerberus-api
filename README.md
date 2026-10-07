# Cerberus API

An end-to-end encrypted vault API for credentials, passwords, notes and custom sensitive
records. Cerberus manages encrypted storage, organization and resource permissions; Heimdall
handles identity, and authorized clients encrypt/decrypt locally.

> **Status:** formal specifications drafted; implementation not started. Protocol review and
> concrete deployment configuration remain explicit preimplementation/release decisions.

## What it does

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

The repository currently contains documentation only. The following commands are intended
for the scaffold defined in [Technology Stack](docs/requirements/Technology%20Stack%20Document.md)
and [Operations & Infrastructure](docs/requirements/Operations%20%26%20Infrastructure%20Document.md).
Use the selected stable SDK and a Docker-compatible runtime for functional tests. Configure
the database, Heimdall scope/credentials and required operational settings first.

From the repository root after scaffolding:

```bash
dotnet restore src/ArturRios.Cerberus.sln
dotnet build src/ArturRios.Cerberus.sln --no-restore
dotnet run --project src/Presentation/ArturRios.Cerberus.WebApi
```

Clone the repository with `git clone https://github.com/artur-rios/cerberus-api.git`.

## Testing

Follow [Testing Specification](docs/requirements/Testing%20Specification%20Document.md).
After scaffolding, from the repository root:

```bash
dotnet test src/ArturRios.Cerberus.sln --filter "Category=Unit"
dotnet test src/ArturRios.Cerberus.sln --filter "Category=Functional"
dotnet test src/ArturRios.Cerberus.sln --collect:"XPlat Code Coverage"
```

Functional tests use real database containers and controlled Heimdall fixtures. Every use
case ships with main/alternative-flow tests. The merged line-coverage floor is 90%; branch
coverage is reported. Product tests cannot run until the scaffold exists.

## Roadmap

This is the reviewable backlog plan. No GitHub issue or milestone has been created. Every
milestone depends on Foundation; dependencies refer only to earlier milestones. No due dates
or labels were supplied.

| Milestone | Delivers | Depends on | Issues | Status |
| --- | --- | --- | --- | --- |
| M-01 — Foundation | Scaffold, persistence, identity adapters, tests, CI and safe operational primitives. | — | 1 | planned |
| M-02 — Account identity and vault protection | Register/login, manage account details and protect/recover the vault. | M-01 | 9 | planned |
| M-03 — Organized vault | Manage profiles, custom records, nested folders and collection membership. | M-01, M-02 | 26 | planned |
| M-04 — Controlled sharing and software secrets | Share selected collections and serve explicitly granted software ciphertext. | M-01, M-02, M-03 | 6 | planned |
| M-05 — Offline synchronization | Apply configurable renewal, incremental access changes and deterministic offline edits. | M-01, M-02, M-03, M-04 | 5 | planned |
| M-06 — Recoverable deletion and beta operations | Deliver account closure/erasure, trash restoration/expiry, privacy export and operational health. | M-01, M-02, M-03, M-04, M-05 | 9 | planned |

## Backlog

### M-01 — Foundation

| Issue | Work | Spec | Status |
| --- | --- | --- | --- |
| - | Project scaffold and initial infrastructure | [Operations & Infrastructure](docs/requirements/Operations%20%26%20Infrastructure%20Document.md) | planned |

### M-02 — Account identity and vault protection

| Issue | Work | Spec | Status |
| --- | --- | --- | --- |
| - | UC-01 — Register account | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-01-register-account) | planned |
| - | UC-02 — Authenticate | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-02-authenticate) | planned |
| - | UC-03 — Get account | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-03-get-account) | planned |
| - | UC-04 — Update account | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-04-update-account) | planned |
| - | UC-05 — Update identity details | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-05-update-identity-details) | planned |
| - | UC-38 — Initialize vault protection | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-38-initialize-vault-protection) | planned |
| - | UC-39 — Change vault protection | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-39-change-vault-protection) | planned |
| - | UC-40 — Recover vault access | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-40-recover-vault-access) | planned |
| - | UC-41 — Refresh recovery key | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-41-refresh-recovery-key) | planned |

### M-03 — Organized vault

| Issue | Work | Spec | Status |
| --- | --- | --- | --- |
| - | UC-09 — Create profile | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-09-create-profile) | planned |
| - | UC-10 — List profiles | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-10-list-profiles) | planned |
| - | UC-11 — Get profile | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-11-get-profile) | planned |
| - | UC-12 — Update profile | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-12-update-profile) | planned |
| - | UC-13 — Delete profile | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-13-delete-profile) | planned |
| - | UC-14 — Set profile associations | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-14-set-profile-associations) | planned |
| - | UC-15 — Open profile access | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-15-open-profile-access) | planned |
| - | UC-16 — Create record | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-16-create-record) | planned |
| - | UC-17 — List records | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-17-list-records) | planned |
| - | UC-18 — Get record | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-18-get-record) | planned |
| - | UC-19 — Update record | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-19-update-record) | planned |
| - | UC-20 — Delete record | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-20-delete-record) | planned |
| - | UC-21 — Move record | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-21-move-record) | planned |
| - | UC-22 — Permanently delete record | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-22-permanently-delete-record) | planned |
| - | UC-23 — Create folder | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-23-create-folder) | planned |
| - | UC-24 — List folders | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-24-list-folders) | planned |
| - | UC-25 — Get folder | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-25-get-folder) | planned |
| - | UC-26 — Update folder | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-26-update-folder) | planned |
| - | UC-27 — Delete folder | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-27-delete-folder) | planned |
| - | UC-28 — Move folder | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-28-move-folder) | planned |
| - | UC-29 — Create collection | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-29-create-collection) | planned |
| - | UC-30 — List collections | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-30-list-collections) | planned |
| - | UC-31 — Get collection | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-31-get-collection) | planned |
| - | UC-32 — Update collection | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-32-update-collection) | planned |
| - | UC-33 — Delete collection | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-33-delete-collection) | planned |
| - | UC-34 — Set collection membership | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-34-set-collection-membership) | planned |

### M-04 — Controlled sharing and software secrets

| Issue | Work | Spec | Status |
| --- | --- | --- | --- |
| - | UC-35 — Grant collection access | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-35-grant-collection-access) | planned |
| - | UC-36 — Change collection access | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-36-change-collection-access) | planned |
| - | UC-37 — Revoke collection access | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-37-revoke-collection-access) | planned |
| - | UC-42 — Grant software access | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-42-grant-software-access) | planned |
| - | UC-43 — Retrieve software secrets | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-43-retrieve-software-secrets) | planned |
| - | UC-44 — Revoke software access | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-44-revoke-software-access) | planned |

### M-05 — Offline synchronization

| Issue | Work | Spec | Status |
| --- | --- | --- | --- |
| - | UC-45 — Get offline policy | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-45-get-offline-policy) | planned |
| - | UC-46 — Set offline policy | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-46-set-offline-policy) | planned |
| - | UC-47 — Renew offline authorization | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-47-renew-offline-authorization) | planned |
| - | UC-48 — Download synchronization changes | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-48-download-synchronization-changes) | planned |
| - | UC-49 — Upload offline edits | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-49-upload-offline-edits) | planned |

### M-06 — Recoverable deletion and beta operations

| Issue | Work | Spec | Status |
| --- | --- | --- | --- |
| - | UC-06 — Request account closure | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-06-request-account-closure) | planned |
| - | UC-07 — Cancel account closure | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-07-cancel-account-closure) | planned |
| - | UC-08 — Permanently delete account | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-08-permanently-delete-account) | planned |
| - | UC-50 — List trash | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-50-list-trash) | planned |
| - | UC-51 — Restore trash entry | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-51-restore-trash-entry) | planned |
| - | UC-52 — Empty trash | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-52-empty-trash) | planned |
| - | UC-53 — Expire retained deletions | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-53-expire-retained-deletions) | planned |
| - | UC-54 — Export account data | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-54-export-account-data) | planned |
| - | UC-55 — Check API health | [Use Case Specification](docs/requirements/Use%20Case%20Specification%20Document.md#uc-55-check-api-health) | planned |

## Contributing

One use case = one issue = one branch = one pull request. Use
`feature/uc-##-use-case-name` from `develop`, with human approval at the documented stage
boundaries by default. Unattended transitions require explicit scoped authorization.
Read [Development Workflow](docs/requirements/Development%20Workflow%20Document.md).

Before implementing encrypted contracts, complete the security/interoperability protocol
review. Before deploying the beta, select/pin stable dependencies, configure explicit retention
and limits, verify erasure-preserving restore and complete the documented privacy controls.
These are recorded decisions/gates, not assertions that implementation or compliance is complete.
