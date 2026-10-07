# Technology Stack — Cerberus API

## Platform & Language

Use the latest stable .NET and C# at implementation time, with mutually compatible stable
dependencies. Follow Heimdall's conventions, including nullable reference types and implicit
usings. The formal Technology Stack Document will own package versions; do not copy Heimdall's
current version pins without checking stable availability and compatibility.

## Application Type

ASP.NET Core HTTP Web API. Follow Heimdall's layered Domain, Application, Infrastructure, and
Presentation organization, with Command, Query, and Shared application projects and mediator
dispatch. Flutter clients are separate deliverables and are mentioned here only as consumers.

## Data Storage

PostgreSQL is the production and functional-test database, following the approved Heimdall
standards. Persist ciphertext and the metadata needed for organization, ownership, sharing,
authorization, synchronization, recovery state, and deletion. End-to-end encryption keeps vault
plaintext and usable vault decryption keys out of the server.

The exact boundary between encrypted content and server-visible metadata, cryptographic
formats, and indexes will be specified in Phase 2. PostgreSQL and a container runtime are
infrastructure dependencies, not additional external application services.

## Data Access

Use Entity Framework Core with code-first migrations and the author's relational repository
abstractions. Use the PostgreSQL provider. Follow Heimdall's singular, snake_case database
naming conventions.

Keep Entity Framework as the initial read and write approach. Introduce Dapper for reads only
when measured performance justifies it; retain the same ownership and permission constraints.

## Authentication

Heimdall is the only external runtime application dependency and remains responsible for
identity authentication and authorization. Cerberus integrates registration, sign-in, and
identity operations and enforces its vault ownership, profile scope, and collection grants.

Use the Web API utility library's bearer-token integration and JWT validation where compatible
with Heimdall's actual contracts. Token issuance remains a Heimdall responsibility. Validate
the identity contract and revocation mechanism before pinning the formal integration design.

A Heimdall identity links to one user's Cerberus account. Vault profiles are separate entities.
Identity authentication alone does not grant decryption capability. Human and software clients
decrypt locally with their authorized keys; Cerberus stores and returns encrypted key material
only. Recovery credentials are one-time, refreshable, and separate from Heimdall login recovery.

All client/API and API/Heimdall communication uses encrypted transport. End-to-end encryption
is additional to transport encryption.

## Testing

Follow the approved Heimdall testing standards:

| Concern | Choice |
| --- | --- |
| Framework | xUnit. |
| Naming | `GivenSomeCondition_WhenSomeAction_ThenSomeOutput`. |
| Unit scope | Command/query handlers, validators, and domain behavior with isolated collaborators. |
| Functional scope | HTTP endpoints, API responses, and resulting persisted state. |
| Functional persistence | Real PostgreSQL provisioned by Testcontainers; no substitute in-memory database. |
| Test helpers | `ArturRios.Util.Test`, including API host base classes and repository fakes. |
| Other test tools | Moq for mocks, Bogus for generated data, and Coverlet for coverage. |
| Categories | `Category=Unit` and `Category=Functional`, run and reported separately. |
| Coverage | Follow Heimdall's 90% merged line-coverage floor; report branch coverage without a numerical gate. |
| Per-use-case coverage | Main flow, all applicable alternative flows, ownership, authorization, and state changes. |

Test projects mirror the production layer folders and append `.Tests` to their production
project names. External Heimdall calls need controlled test doubles or a local test fixture;
the exact fixture and contract coverage will be specified in Phase 2.

The intended suite command is `dotnet test`, run from the eventual solution directory, with
category filters for separate runs. The concrete directory will be fixed in the formal layout.

## External Dependencies

The user's instruction is to evaluate the complete first-party library family and use the
packages that fit. The assessment below uses the public repository READMEs and the
[Dotnet Libraries index](https://github.com/artur-rios/dotnet-libraries), inspected on
2026-10-07. Package placement and versions remain subject to compatibility verification.

| Library / package | Intended use or selection decision |
| --- | --- |
| [ArturRios.Output](https://github.com/artur-rios/dotnet-output) | Consistent operation, data, pagination, and expected-error envelopes. |
| [ArturRios.Util](https://github.com/artur-rios/dotnet-util) | Applicable HTTP, retry, random, and general helpers; select security helpers only after reviewing their suitability. |
| [ArturRios.Extensions](https://github.com/artur-rios/dotnet-extensions) | Common extensions where needed; may arrive transitively through other family packages. |
| [ArturRios.Configuration](https://github.com/artur-rios/dotnet-configuration) | Layered configuration, including environment variables and environment-specific files. |
| [ArturRios.Logging](https://github.com/artur-rios/dotnet-logging) | Evaluate useful tracing/logging helpers within Heimdall's structured logging approach; avoid adding a competing logger pipeline. |
| [ArturRios.Jwt](https://github.com/artur-rios/dotnet-jwt) | Token reading/validation through compatible Web API authentication; no Cerberus identity-token issuer. |
| [ArturRios.Validation](https://github.com/artur-rios/dotnet-validation) | FluentValidation integration and validation-result envelopes for server-visible request contracts. |
| [ArturRios.Mediator](https://github.com/artur-rios/dotnet-mediator) | Command/query dispatch and handler contracts. |
| [ArturRios.Messaging](https://github.com/artur-rios/dotnet-messaging) | Do not select its Mailgun integration: Cerberus has no direct email service dependency. Identity messaging stays with Heimdall. |
| [ArturRios.Data.Relational.Core](https://github.com/artur-rios/dotnet-data) | EF Core abstractions, repositories, context foundation, and persistence result handling. |
| ArturRios.Data.PostgreSql | PostgreSQL provider for the relational core. |
| ArturRios.Data.Dapper | Conditional read optimization after profiling; no initial requirement to install it. |
| ArturRios.Data.Sqlite / ArturRios.Data.MySql | Do not select: PostgreSQL is the approved database for production and functional tests. |
| ArturRios.Data.MongoDb / ArturRios.Data.DynamoDb | Do not select: no second persistence engine or AWS dependency is required. |
| ArturRios.Data.Export / ArturRios.Data.Export.Excel | No initial selection: no CSV/Excel export capability was requested. Review again if an approved portability contract needs one. |
| [ArturRios.Util.WebApi](https://github.com/artur-rios/dotnet-webapi-util) | API startup, middleware, response mapping, tracing, typed HTTP client foundation, and compatible authentication. |
| [ArturRios.Util.Test](https://github.com/artur-rios/dotnet-test-util) | Shared testing utilities, fakes, and functional API-host infrastructure. |

The Data rows cover all nine packages listed in its current README. The twelve-library family
is fully assessed; relevance does not require installing every package. Declare direct package
references where Cerberus uses public types, and account for transitive dependencies.

[Dotnet Tools](https://github.com/artur-rios/dotnet-tools) was also inspected. It is optional
development tooling, not an API runtime dependency; its build/test wrappers do not introduce
product features.

Heimdall is the sole external service. Logging, health checks, and diagnostics follow its
standards where applicable; their concrete operational configuration is a Phase 2 decision.
Do not import Heimdall-specific Mailgun or Google dependencies into Cerberus merely because
they appear in the reference project.

## Deployment

Host the API on an Ubuntu VPS, as required by the brainstorm. Database, API, and the eventual
client applications form separate responsibilities. Detailed environments, configuration,
backup retention, erasure reconciliation, and deployment pipelines will be settled in the
Operations & Infrastructure specification.

Reference standards: [Heimdall Technology Stack](https://github.com/artur-rios/heimdall-api/blob/main/docs/requirements/Technology%20Stack%20Document.md)
and [Heimdall Testing Specification](https://github.com/artur-rios/heimdall-api/blob/main/docs/requirements/Testing%20Specification%20Document.md).
