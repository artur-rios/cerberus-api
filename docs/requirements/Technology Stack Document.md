# Technology Stack Document — Cerberus API

## 1. Purpose

Own the technology choices and version policy. All other specifications link here instead of
copying version numbers. Derives from [initial Technology Stack](../initial/Technology%20Stack.md).
At scaffolding, resolve mutually compatible stable versions, pin them consistently, and record
the actual versions here. A policy is not a claim that package compatibility has been tested.

---

## 2. Platform & Language

| Concern | Choice | Version policy |
| --- | --- | --- |
| Runtime / framework | .NET / ASP.NET Core | Latest stable at implementation time |
| Language | C# | Default supported by the selected stable SDK |
| Compiler settings | Nullable and implicit usings enabled | Applied project-wide |
| Hosting | Ubuntu VPS | Latest stable at implementation time |
| Container runtime | Docker-compatible runtime | Latest stable at implementation time |

Use the layered conventions approved from Heimdall. Flutter is a separate client project and
is not a package dependency of this API.

---

## 3. Libraries

### 3.1 First-party packages

| Package | Version policy | Used by | Role |
| --- | --- | --- | --- |
| ArturRios.Output | Latest stable at implementation time | Application / Presentation | Expected-error and data/pagination envelopes |
| ArturRios.Util | Latest stable at implementation time | Application / Infrastructure | Applicable HTTP, retry and general helpers; review security helpers before use |
| ArturRios.Extensions | Latest stable at implementation time | Applicable layers / transitive | Shared extensions without redundant abstractions |
| ArturRios.Configuration | Latest stable at implementation time | Presentation / tests | Layered configuration |
| ArturRios.Logging | Latest stable at implementation time | Infrastructure / Presentation | Applicable correlation helpers within the single structured logging pipeline |
| ArturRios.Jwt | Latest stable at implementation time | Presentation / transitive | Heimdall-compatible JWT reading and validation, not identity issuance |
| ArturRios.Validation | Latest stable at implementation time | Application | Validation result integration |
| ArturRios.Mediator | Latest stable at implementation time | Application / Presentation | Command/query dispatch |
| ArturRios.Data.Relational.Core | Latest stable at implementation time | Domain / Application / Data | Repository contracts and EF context foundation |
| ArturRios.Data.PostgreSql | Latest stable at implementation time | Data | Approved database provider |
| ArturRios.Util.WebApi | Latest stable at implementation time | Presentation | Startup, middleware, typed HTTP clients and result mapping |
| ArturRios.Util.Test | Latest stable at implementation time | Tests | Repository fakes and functional API host |

### 3.2 Other production packages

| Package / tool | Version policy | Used by | Role |
| --- | --- | --- | --- |
| Microsoft.EntityFrameworkCore | Latest stable at implementation time | Data | ORM |
| Microsoft.EntityFrameworkCore.Design | Latest stable at implementation time | Data | Migrations |
| Npgsql.EntityFrameworkCore.PostgreSQL | Latest stable at implementation time | Data / provider dependency | PostgreSQL integration |
| EFCore.NamingConventions | Latest stable at implementation time | Data | Singular snake_case mapping |
| FluentValidation | Latest stable at implementation time | Application | Input validation |
| Serilog / Serilog.AspNetCore | Latest stable at implementation time | Presentation | Structured logging |
| OpenAPI tooling supplied by ArturRios.Util.WebApi | Latest stable at implementation time | Presentation | API contract publication |

### 3.3 Assessed but conditional or excluded

| Package / family | Decision |
| --- | --- |
| ArturRios.Data.Dapper | Install only if measured read performance justifies it; preserve access filtering. |
| ArturRios.Data.Sqlite / MySql / MongoDb / DynamoDb | Excluded: PostgreSQL is the sole approved persistence engine. |
| ArturRios.Data.Export / Export.Excel | Excluded initially: no CSV/Excel feature; ciphertext privacy export does not justify adding all exporters. |
| ArturRios.Messaging | Excluded: its Mailgun integration would add an unapproved runtime service. |
| Dotnet Tools | Optional developer wrappers; official SDK commands remain the baseline. |

This accounts for the complete twelve-library family and all nine Data packages. Use direct
references when public types are used; track transitive compatibility. First-party evidence:
[family index](https://github.com/artur-rios/dotnet-libraries),
[data packages](https://github.com/artur-rios/dotnet-data), and
[Web API foundation](https://github.com/artur-rios/dotnet-webapi-util).

---

## 4. Data Storage

| Concern | Choice |
| --- | --- |
| Engine | PostgreSQL in local, staging, production, and functional tests. |
| Stored content | Client ciphertext and wrapped keys; structural/permission metadata is server-visible. |
| Connection | CERBERUS_DATA_CONNECTIONSTRING and CERBERUS_DATA_DATABASETYPE, with database type PostgreSql. |
| Versions | Latest stable at implementation time |

No vault plaintext or usable decryption key is persisted. Database/backup encryption protects
server-visible metadata in addition to client encryption; it does not replace end-to-end encryption.

---

## 5. Data Access

| Concern | Choice | Version policy |
| --- | --- | --- |
| ORM | Entity Framework Core | Latest stable at implementation time |
| Migrations | dotnet-ef and EF design package | Latest stable at implementation time |
| Naming | Singular snake_case | Project convention |
| Pattern | Async repository abstractions | First-party package policy above |

Application handlers use repository interfaces rather than bypassing ownership checks with
direct context queries. Database transactions and uniqueness constraints guard association,
recovery, and deletion races. Use Dapper only for justified reads over the approved provider.

Intended SDK commands after scaffolding, from the repository root:

```bash
dotnet restore src/ArturRios.Cerberus.sln
dotnet build src/ArturRios.Cerberus.sln --no-restore
dotnet run --project src/Presentation/ArturRios.Cerberus.WebApi
```

The foundation establishes this solution path. Host execution requires explicit validated
configuration and preprovisioned ledger storage; see the operational runbook. Run with
`--validate-configuration` or `--migrate` for explicit maintenance without starting traffic.

---

## 6. Cross-Cutting Technologies

| Concern | Choice | Policy / use |
| --- | --- | --- |
| Identity | Heimdall API | Only external runtime application service; scope-bound bearer identities. |
| Authentication adapter | ArturRios.Util.WebApi / ArturRios.Jwt | Validate issuer, audience, signature, expiry and configured identity scope; revalidate when required. |
| Results | ArturRios.Output | Expected failures have stable error codes and canonical status mapping. |
| Validation | ArturRios.Validation / FluentValidation | Validate visible metadata and encrypted-envelope shape; never decrypt to validate fields. |
| Logging | Serilog and applicable ArturRios.Logging helpers | One structured pipeline; redact secrets and direct identity details. |
| Configuration | ArturRios.Configuration | Environment secrets override checked-in nonsecret settings. |
| API documentation | OpenAPI utility integration | Contract generated from the API and checked for drift. |
| E2E protocol | Versioned authenticated-encryption and wrapped-key protocol | Algorithm suite and parameters explicitly deferred to a reviewed interoperability/security design before product implementation. |
| Recovery | Separate client secret and server-verifiable proof | Client-generated replacement secrets; atomic server-side consumption. |
| Offline leases | Client-verifiable signed authorization | Signature suite is reviewed with the protocol; separate signing credentials from vault decryption material. |

The crypto review must select algorithms, KDF parameters, nonce uniqueness, authenticated
metadata, recipient public-key verification, proof domain separation, key rotation and client
test vectors. Server proof verifiers and identity service credentials must not unlock ciphertext.
Record the selected suite here and the wire contract in System Requirements before coding it.
Useful primary references are [Argon2](https://www.rfc-editor.org/info/rfc9106/),
[HKDF](https://www.rfc-editor.org/info/rfc5869/), and
[authenticated-encryption constraints](https://doc.libsodium.org/secret-key_cryptography/aead).
These references are review inputs, not an unapproved algorithm selection.

---

## 7. Testing Technologies

| Technology | Version policy | Use |
| --- | --- | --- |
| xunit / xunit.runner.visualstudio | Latest stable at implementation time | Unit and functional test framework |
| Microsoft.NET.Test.Sdk | Latest stable at implementation time | Test runner integration |
| coverlet.collector | Latest stable at implementation time | Coverage collection |
| Moq | Latest stable at implementation time | Collaborator mocking |
| Bogus | Latest stable at implementation time | Generated test data |
| Testcontainers.PostgreSql | Latest stable at implementation time | Real throwaway PostgreSQL |
| Microsoft.AspNetCore.Mvc.Testing | Latest stable at implementation time | Functional HTTP host through the family test base |

Also use ArturRios.Util.Test from the first-party table. Unit and functional categories are
separate. Moq is the single mocking framework; Bogus supplies data. Real PostgreSQL containers
verify persisted behavior. Controlled Heimdall fixtures validate HTTP contracts without live
production credentials. See [Testing Specification](Testing%20Specification%20Document.md).

---

## 8. Version Summary

| Category | Package / tool | Version policy |
| --- | --- | --- |
| Platform | .NET / ASP.NET Core | Latest stable at implementation time |
| Platform | Ubuntu | Latest stable at implementation time |
| Platform | Docker-compatible runtime | Latest stable at implementation time |
| Language | C# | Framework default |
| First-party | ArturRios.Output | Latest stable at implementation time |
| First-party | ArturRios.Util | Latest stable at implementation time |
| First-party | ArturRios.Extensions | Latest stable at implementation time |
| First-party | ArturRios.Configuration | Latest stable at implementation time |
| First-party | ArturRios.Logging | Latest stable at implementation time |
| First-party | ArturRios.Jwt | Latest stable at implementation time |
| First-party | ArturRios.Validation | Latest stable at implementation time |
| First-party | ArturRios.Mediator | Latest stable at implementation time |
| First-party | ArturRios.Data.Relational.Core | Latest stable at implementation time |
| First-party | ArturRios.Data.PostgreSql | Latest stable at implementation time |
| First-party | ArturRios.Util.WebApi | Latest stable at implementation time |
| First-party | ArturRios.Util.Test | Latest stable at implementation time |
| Production | Microsoft.EntityFrameworkCore | Latest stable at implementation time |
| Production | Microsoft.EntityFrameworkCore.Design | Latest stable at implementation time |
| Production | Npgsql.EntityFrameworkCore.PostgreSQL | Latest stable at implementation time |
| Production | EFCore.NamingConventions | Latest stable at implementation time |
| Production | FluentValidation | Latest stable at implementation time |
| Production | Serilog / Serilog.AspNetCore | Latest stable at implementation time |
| Production | OpenAPI tooling supplied by ArturRios.Util.WebApi | Latest stable at implementation time |
| Tool | dotnet-ef | Latest stable at implementation time |
| Database | PostgreSQL | Latest stable at implementation time |
| Testing | xunit / xunit.runner.visualstudio | Latest stable at implementation time |
| Testing | Microsoft.NET.Test.Sdk | Latest stable at implementation time |
| Testing | coverlet.collector | Latest stable at implementation time |
| Testing | Moq | Latest stable at implementation time |
| Testing | Bogus | Latest stable at implementation time |
| Testing | Testcontainers.PostgreSql | Latest stable at implementation time |
| Testing | Microsoft.AspNetCore.Mvc.Testing | Latest stable at implementation time |
| Conditional | ArturRios.Data.Dapper | Latest stable at implementation time |
| Optional tooling | Dotnet Tools | Latest stable at implementation time |
| Protocol | E2E, recovery and lease algorithm suites | Selected and pinned after required security/interoperability review |
