Closes #6.

Add protected `PUT /api/identity/me` for the current owner's name and email. Require current account-wide vault access, bind the original bearer to Heimdall's own public person route, and return only public identity details and verification state. Reject role, scope, password, owner and vault fields.

Lock account and session before checking current permission with PostgreSQL statement time, then hold locks across the bounded provider call. No encrypted payload is retrieved and no Cerberus account, profile, revision, policy or session changes. Provider conflicts return409; dependency failures return503. A lost response can follow an upstream commit, so retry the same replacement or verify identity state; no distributed rollback is claimed. The inspected provider accepts no expected identity revision.

Validation: full unfiltered555 tests passed with zero failures/skips; production line96.7%, branch86.7%; helper23 tests, specs, OpenAPI generation/drift and diff checks passed. Includes real PostgreSQL permission/lock/expiry cases, controlled Heimdall HTTP tests, malformed contracts and post-commit response loss. One fresh final code review complete: no Critical/Important findings. Two deferred minors: known schema metadata and design wording about discarded credential-bearing extra provider fields (no public leakage).

Known deferred minor: generator required/nullable metadata for name/email/header follows the previously recorded OpenAPI schema limitation; runtime validation is strict and tested. Owner deferred independent protocol/security and client-operability approval for development only; actual release approval remains pending and main/tag gate remains enforced.
