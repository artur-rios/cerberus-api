# Changelog

All notable changes to the Cerberus API are recorded in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

No release has been tagged yet. Independent review of the encryption protocol was deferred for
development into `develop` only; it is still required before any release.

### Added

- The specifications: initial notes, business rules, the formal requirements and the use case specification.
- The .NET 10 solution scaffold, with typed configuration validation, request byte limits and no-store responses.
- PostgreSQL persistence with its initial migration, and durable erasure files and retention leases.
- Strict Heimdall token validation and scoped Heimdall adapters with online identity revalidation.
- Restore reconciliation that replays erasures before traffic resumes.
- Command-line modes to validate configuration, migrate the database and reconcile a restore without starting HTTP.
- A development container image, a generated OpenAPI contract, and a protocol-review record validator.
- A protocol interoperability harness, and the recorded owner deferral of independent protocol review for
  development.
- Account registration that creates the Heimdall identity and the encrypted account, with idempotent retries and
  reconciliation (UC-01).
- Authentication delegated to Heimdall, including second-factor challenges (UC-02).
- Reading and atomically updating the encrypted account details with current vault access (UC-03, UC-04), and
  updating Heimdall identity details (UC-05).
- Vault protection: initializing and changing it, recovering vault access and refreshing the recovery key
  (UC-38 to UC-41).
- Creating profiles, listing the permitted ones with opaque pagination, and retrieving one within the current vault
  scope (UC-09 to UC-11).

[Unreleased]: https://github.com/artur-rios/cerberus-api/commits/develop
