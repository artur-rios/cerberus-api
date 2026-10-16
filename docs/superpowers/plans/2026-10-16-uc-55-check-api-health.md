# Operational Health Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans inline task-by-task. Steps use checkbox syntax.

**Goal:** Minimal public liveness/readiness and separately authorized redacted operational details.
**Architecture:** Focused shared caller authorization and dependency probe services; factory-owned read-only database probe; explicit controllers and narrow health metadata integrate with current middleware.
**Tech Stack:** Existing .NET10/ASP.NET/EF-Npgsql/PostgreSQL18/xUnit, no added dependencies.
**Spec:** docs/superpowers/specs/2026-10-16-uc-55-check-api-health.md

## Global Constraints
- GET /health/live public200 only status healthy; GET /health/ready public200/503 only status healthy/unhealthy; identity/query/header never opts into details.
- GET /health/details requires typed explicit separate operational grant AND actual caller live authority; no configured credential substitution. Role3 people, role2 multi-scope owners and applications can hold grants; ordinary vault/software grants cannot.
- Optional two canonical UUID CSV settings empty-default deny; invalid/duplicates startup rejection without values. Existing service/user/app contracts unchanged.
- Readiness checks database account SELECT/schema, configured non-user Heimdall scope authority, final restore gate; each dependency cooperative5second budget, request cancellation propagates, no login/register or mutations.
- Details fixed three names/states/durations; safe ProcessOutput401/403/503 errors, no protected data/topology/stacks. Narrow health metadata bypasses restore; vault remains blocked. Global no-store/limits retained.
- Unattended authorized develop only, one UC/branch/PR. All local gates, unfiltered six-project Release/fresh six-assembly>=90% line+reported branch, one fresh final reviewer and required exact-headCI before normal squash/Done/cleanup.

## Review Focus
1. Mixed person/application/duplicate claims and equal subject UUIDs must never cross separately typed operational grants (Task1).
2. Granted actor loses scope ownership/credential or expires during provider wait: actual caller authority must fail closed, never configured service substitution (Task1/3).
3. Database accepts connections but account schema/SELECT is unusable: readiness must503 without user rows or mutations (Task2/3).
4. Restore gate closes while dependencies run: final readiness must be unhealthy, health stays reachable and ordinary vault traffic stays blocked (Task2/3).
5. Stalled dependency/body, client cancellation and protected provider/database exceptions: bounded cancellable cleanup and only fixed redacted output (Tasks1–3).

### Task 1: Explicit operational caller authorization
**Files:** modify Shared/Configuration/CerberusOptions.cs, CerberusOptionsValidator.cs, WebApi/Configuration/CerberusConfiguration.cs; create Shared/Operations/OperationalGrants.cs, OperationalHealthAuthorizer.cs, Shared/Identity/HeimdallTokenValidator.Operational.cs, HeimdallClient.Operational.cs; modify original validator/client declarations only for partial/interface. Tests Shared.Tests/OperationalHealthAuthorizationTests.cs and WebApi.Tests/CerberusConfigurationTests.cs.
**Interfaces:** OperationalGrants.TryParse(string?,out IReadOnlySet<Guid>) and IsValid(CerberusOptions); HeimdallTokenValidator.ValidateOperationalAsync(string):Task<ClaimsPrincipal?>; IHeimdallOperationalClient.RevalidateOperationalAsync(string,ct):Task<HeimdallAuthorization>, implemented by partial HeimdallClient. OperationalHealthAuthorizer(options,tokens,appTokens,people,apps).AuthorizeAsync(string,ct):Task<OperationalAuthorization> enum Authorized,Unauthenticated,Forbidden,Unavailable. CerberusOptions adds nullable string OperationalPersonIds/OperationalApplicationIds.
- [ ] Write tests for granted role3/role2 multiple scopes/app success using actual token validators and real adapter with controlled HTTP transport; ensure exact actual caller GET routes/headers and current subject/role/scope/credential, separate typed grants and no login/register.
- [ ] Add malformed/duplicate/mixed/wrong audience/scope/MFA/expiry/absent bearer, ungranted actors, cross-kind UUID and invalid CSV/startup cases; provider denial/outage/malformed/deleted/ownership mismatch, stalled response cancellation and token expiry after network. Add configuration precedence/default denial tests.
- [ ] Run `dotnet test tests/Application/ArturRios.Cerberus.Shared.Tests --filter 'FullyQualifiedName~OperationalHealth' --no-restore` after mechanical signatures compile; Expected actual assertion failures against empty authorization, not compiler errors as RED.
- [ ] Implement strict separate grant parsing/caller authentication and live HTTP binding. Existing configured-service validation remains unchanged.
- [ ] Run focused command plus affected Heimdall/configuration tests; Expected allPASS. Commit `feat: authorize explicit operational callers` and task-done focused command.

### Task 2: Redacted readiness and database probe
**Files:** create Shared/Operations/OperationalHealthProbe.cs, Data/Operations/DatabaseReadinessProbe.cs, Shared.Tests/OperationalHealthProbeTests.cs, Data.Tests/DatabaseReadinessProbeTests.cs.
**Interfaces:** HealthStatus(string Status), HealthComponent(string Name,string Status,long ElapsedMilliseconds), HealthDetails(string Status,IReadOnlyList<HealthComponent> Components); IDatabaseReadinessProbe.CheckAsync(ct):Task<bool>; DatabaseReadinessProbe(factory); OperationalHealthProbe(database,identity,gate,options,TimeProvider).CheckAsync(ct):Task<HealthDetails>. All DTOs closed. Probe independently runs database + configured VerifyScopeAsync, bounded5seconds via TimeProvider, returns fixed database/identity/restore components and final gate state. Cancellation propagates.
- [ ] Write tests for all dependency combinations, expected faults redacted, request cancellation, controlled probe timeout and restore closing during waits. Test actual PostgreSQL healthy/missing account schema/SELECT denied, restricted SELECT role and zero native mutations/backend cleanup.
- [ ] Run focused `dotnet test tests/Application/ArturRios.Cerberus.Shared.Tests --filter 'FullyQualifiedName~OperationalHealth' --no-restore` and `dotnet test tests/Infrastructure/ArturRios.Cerberus.Data.Tests --filter 'FullyQualifiedName~DatabaseReadiness' --no-restore`; Expected actual missing probe behavior failures after signatures compile.
- [ ] Implement factory-owned SELECT LIMIT0 and redacted application probe, cancellation/resource lifetime. Run both focused commands; Expected allPASS. Commit `feat: probe redacted operational readiness`, task-done applicable broader checks.

### Task 3: Health HTTP integration and final local gates
**Files:** create WebApi/Controllers/HealthController.cs, HealthEndpointAttribute.cs, HealthSchemaFilter.cs; modify Startup.cs, middleware ProtectedEndpointMiddleware/RestoreBarrierMiddleware, ContractRegistration.cs, generated OpenAPI; create WebApi.Tests/OperationalHealthHttpTests.cs and dedicated actual PostgreSQL/loopback identity fixture (test-only worker isolation); docs/operations/health.md and README.
**Interfaces:** [HealthEndpoint] marks exactly health actions; public methods AllowAnonymous; details method [OperationalAuthentication] handled before generic auth in ProtectedEndpointMiddleware using OperationalHealthAuthorizer. DI separate typed IHeimdallOperationalClient avoids casting/replacing existing clients. Controllers use OperationalHealthProbe directly as infrastructure health (no vault/CQRS state mutation).
- [ ] Write real HTTP tests first: missing routeRED, minimal raw public bodies ignoring opt-ins and bearer; no DB/provider calls for live, configured non-user GETs/no POST for ready, healthy/degraded DB/provider/gate, no-store/conditional handling, fixed redacted details and actual operator/app caller grants;401/403/503 no components, scoped app/person isolation, unchanged native state.
- [ ] Add closed restore integration: health live200/ready503/details stillauth and blocked vault503, final gate closure after started probes, actual database outage/SELECT/schema failures, stalled body/request cancellation and cleanup, raw body/schema comparison for all response families.
- [ ] Run `dotnet test tests/Presentation/ArturRios.Cerberus.WebApi.Tests --filter 'FullyQualifiedName~OperationalHealth' --no-restore`; Expected missing routes/authorization behavior failures. Implement narrow middleware/DI/controllers/closed schemas; repeat untilPASS.
- [ ] Update operations config/probe/qualification docs and README56Done0Todo (foundation+55business). Run all local restore/build/audit/helper/spec/native322x4/actual client/OpenAPI/Docker gates; Expected0warnings/errors/allPASS, existingAPI unchanged.
- [ ] Commit `feat: expose minimal and authorized health` to freeze clean head. Run unfiltered six-project Release+fresh coverage once at frozen source. Expected0fail/skip/sixassemblies>=90line,branchreported. Task-done validates exact frozen full-run proof rather than redundantly repeating an unchanged full run.

## Delivery
- [ ] Whole-branch review package, exactly one fresh most-capable read-only reviewer; regrade findings, Critical/Important one TDDfix pass+fullsuite, Minor defer, rule on every declined judgment with cost.
- [ ] PR closes56; exact-headbranch-policy/test/dockerSUCCESS with downloaded proof, freshdevelop/rules, normalsquash, sourcetreeequality, issueClosed/projectDone/README56Done0Todo/M06all9closed, ownedcleanup/hasharchive; verify no other open unblocked issues.

## Gate1 self-check
FR-OP01 Task3/live; FR-OP02 Tasks2/3/readiness; FR-OP03 Tasks1/3/details. AF01 Task3 minimalpublic; AF02 Tasks1/3 401/403; AF03 Tasks2/3 dependency503/livenessindependent. IDs freshly read and exist. All5review risks have owning test steps. DTO/service signatures precede consumers. Auth/probe remain separate, no vault authority or new runtime service. Written design self-review: no placeholders or unresolved behavioral choice; existing unattended scope and native method preserved.
