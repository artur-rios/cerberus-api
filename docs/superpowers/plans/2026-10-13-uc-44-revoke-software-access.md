# UC-44 Revoke Software Access Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement strict, atomic revocation of one active software grant through `DELETE /api/software-grants/{grantId}`, erasing every server-held recipient key and target envelope for that grant.

**Architecture:** Add a dedicated revocation command boundary and PostgreSQL store over the existing normalized software-grant aggregate. The store authenticates the human owner and current vault session, linearizes on a guarded active-to-revoked revision update, deletes both typed target sets, verifies the reduced aggregate, and commits without changing owner sessions or unrelated vault data.

**Tech Stack:** .NET 10, ASP.NET Core Web API, ArturRios mediator/output libraries, FluentValidation, Entity Framework Core 10, Npgsql/PostgreSQL, xUnit/Moq, Testcontainers, System.Text.Json, and the repository's Python verification scripts.

**Spec:** `docs/superpowers/specs/2026-10-13-uc-44-revoke-software-access-design.md`

## Global Constraints

- Implement only GitHub issue #45 / UC-44, FR-SW-05, AF-01 through AF-05, BR-10, BR-15 and BR-17.
- Base and PR target are exact `develop` commit `750457fb2785d29b111a6124755dc263cea920e9`; use branch `feature/uc-44-revoke-software-access`.
- Preserve one issue, one branch and one pull request; transition project #15 through Todo, In Progress, Testing and Done only at the documented gates.
- Keep blocked issue #44 / UC-43 in Todo with its `blocked` label; add no application-authentication substitute, software credential, retrieval path or Heimdall call.
- Use `DELETE /api/software-grants/{grantId}` with one human bearer, one `X-Cerberus-Vault-Access` header, strict bounded UTF-8 JSON, no query parameters and no-store responses.
- Require a canonical lowercase nonzero dashed UUID and an exact body containing only positive integer `expectedRevision < 9007199254740991`.
- Keep success output to exactly `grantId` and `revision`; never expose the software identity, targets, recipient key, key envelopes or ciphertext.
- Resolve authority from the authenticated actor only; missing, foreign, terminal and already-revoked grants are indistinguishable `not_found` results.
- Change only the exact active software-grant aggregate: state `Revoked`, revision `expectedRevision + 1`, a fresh concurrency stamp, empty recipient key, and zero record/collection target rows.
- Do not change account policy/revocation generation, vault sessions, profiles, records, collections, memberships, ciphertext, key epochs, unrelated grants or schema.
- Erase old recipient bytes without parsing them; relational corruption that prevents exact aggregate identification or deletion is `persistence_unavailable`.
- Propagate caller cancellation; classify safe failures as 400, 401, 403, nonrevealing 404, 409, 413 or 503 exactly as the spec defines.
- Meet at least 90% line coverage in every merged production assembly and report aggregate branch coverage.
- Independent protocol/security/client qualification remains a release gate under the recorded development review deferral.

## Review Focus

1. A foreign, terminal, or already-revoked grant ID must be indistinguishable from an absent ID and must not leak owner/software metadata; Tasks 2 and 4 pin these cases.
2. Corrupt opaque recipient-key or envelope bytes must still be erased, while duplicate/cross-owner relational rows must fail closed and roll back; Task 2 pins both classes.
3. Two revocations of revision N and a concurrent UC-42 reactivation must have one linearized winner, exact loser classification and no partial target deletion; Task 3 pins these races.
4. Natural session expiry after database-time authorization must not split the transaction, but session revocation/policy/profile change while waiting for a lock must prevent the write; Task 3 pins each boundary.
5. Duplicate headers, media parameters, body members, noncanonical UUIDs/numbers, BOMs and invalid UTF-8 must fail before persistence with no-store and no secret-bearing response; Task 4 pins the transport boundary.

---

## Gate transition before Task 1

- [ ] Confirm `git status --short`, `git rev-parse HEAD` and `git rev-parse origin/develop` show only the approved spec and plan on exact base `750457fb2785d29b111a6124755dc263cea920e9`.
- [ ] Create `feature/uc-44-revoke-software-access` from that base, move issue #45 to In Progress in project #15, and initialize `.superpowers/sdd/2026-10-13-uc-44-revoke-software-access/ledger.md`.
- [ ] Commit the approved design, plan and initialized ledger with `docs: design software access revocation` before product changes.

### Task 1: Domain and command boundary

**Files:**

- Create: `src/Domain/ArturRios.Cerberus.Domain/Software/SoftwareGrantRevocationContracts.cs`
- Create: `src/Application/ArturRios.Cerberus.Command/Software/RevokeSoftwareAccessCommand.cs`
- Create: `src/Application/ArturRios.Cerberus.Command/Software/RevokeSoftwareAccessValidator.cs`
- Create: `src/Application/ArturRios.Cerberus.Command/Software/RevokeSoftwareAccessHandler.cs`
- Create: `src/Application/ArturRios.Cerberus.Command/Software/RevokeSoftwareAccessOutput.cs`
- Create: `src/Application/ArturRios.Cerberus.Command/Software/SoftwareGrantRevocationMessages.cs`
- Create: `tests/Application/ArturRios.Cerberus.Command.Tests/RevokeSoftwareAccessContractTests.cs`
- Create: `tests/Application/ArturRios.Cerberus.Command.Tests/RevokeSoftwareAccessHandlerTests.cs`

**Interfaces:**

- Consumes: `ProtocolBinary.MaxInteger`, `OpaqueAccessHandle.TryHash`, `VaultResult<T>`, FluentValidation and `ICommandHandlerAsync<TCommand,TOutput>`.
- Produces: `SoftwareGrantRevocationInput`, `SoftwareGrantRevocationRequest`, `SoftwareGrantRevocationDetails`, `ISoftwareGrantRevocationStore.RevokeAsync`, and `RevokeSoftwareAccessCommand` returning `RevokeSoftwareAccessOutput`.

- [ ] **Step 1: Write failing strict JSON and revision tests**

Add contract tests that deserialize with `ProtectionFixture.Json` and prove the body accepts only the exact lowercase `expectedRevision` property with a canonical integer. Cover omitted, null, duplicate, unknown, case-forged, string, fractional, exponent, zero, negative, maximum, over-maximum and overflow values.

```csharp
var command = JsonSerializer.Deserialize<RevokeSoftwareAccessCommand>(
    "{\"expectedRevision\":1}", ProtectionFixture.Json)!;
command.SetContext(Actor, Handle, Grant);
Assert.Equal(1, command.ExpectedRevision);
Assert.True(command.ToInput().IsValid(Grant));

Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RevokeSoftwareAccessCommand>(
    "{\"expectedRevision\":1,\"ExpectedRevision\":1}", ProtectionFixture.Json));
```

- [ ] **Step 2: Run the focused tests and confirm RED**

```powershell
dotnet test tests/Application/ArturRios.Cerberus.Command.Tests/ArturRios.Cerberus.Command.Tests.csproj --filter "FullyQualifiedName~RevokeSoftwareAccess"
```

Expected: compile failure because the revocation contracts and command do not exist.

- [ ] **Step 3: Add the immutable domain contract and strict command**

Create the exact domain boundary:

```csharp
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record SoftwareGrantRevocationInput(
    [property: JsonRequired] long ExpectedRevision)
{
    public bool IsValid(Guid grantId) => grantId != Guid.Empty
        && ExpectedRevision is > 0 and < ProtocolBinary.MaxInteger;
}

public sealed record SoftwareGrantRevocationRequest(
    Guid Actor, string AccessVerifier, Guid GrantId,
    SoftwareGrantRevocationInput Input);

public sealed record SoftwareGrantRevocationDetails(Guid GrantId, long Revision);

public interface ISoftwareGrantRevocationStore
{
    Task<VaultResult<SoftwareGrantRevocationDetails>> RevokeAsync(
        SoftwareGrantRevocationRequest request,
        CancellationToken cancellationToken);
}
```

Create `RevokeSoftwareAccessCommand` with `[JsonRequired] public long ExpectedRevision`, internal read-only `Actor`, `GrantId`, and nullable `VaultAccess`, plus:

```csharp
public void SetContext(Guid actor, string? access, Guid grantId)
{
    Actor = actor;
    VaultAccess = access;
    GrantId = grantId;
}

public SoftwareGrantRevocationInput ToInput() => new(ExpectedRevision);
```

- [ ] **Step 4: Write failing handler trust-boundary tests**

Cover cancellation precedence; absent actor; absent, empty, padded and noncanonical access handles; zero route ID; every revision boundary; exact SHA-256 access verifier forwarding; caller token forwarding; safe error allowlisting; store error-with-data; null result/data; wrong grant/revision success; and exact two-field output.

```csharp
var result = await Handler(store).HandleAsync(Command(expected), cancellation.Token);
Assert.Equal(new[] { "software_access_revoked" }, result.Messages);
Assert.Equal(Grant, result.Data!.GrantId);
Assert.Equal(expected + 1, result.Data.Revision);
Assert.Equal(new[] { "GrantId", "Revision" },
    typeof(RevokeSoftwareAccessOutput).GetProperties().Select(x => x.Name).Order());
Assert.Equal("66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925",
    received!.AccessVerifier);
```

- [ ] **Step 5: Implement validator, handler, output and message map**

Use this exact handler order: cancellation, actor, present vault header, canonical-handle hash, validator, store call, data/error consistency, allowlisted error, exact success evidence. The success check is:

```csharp
if (data is null || data.GrantId != command.GrantId
    || data.Revision != command.ExpectedRevision + 1
    || data.Revision is <= 0 or > ProtocolBinary.MaxInteger)
    return output.WithError("persistence_unavailable");

return output.WithData(new RevokeSoftwareAccessOutput
{
    GrantId = data.GrantId,
    Revision = data.Revision
}).WithMessage("software_access_revoked");
```

Define `SoftwareGrantRevocationMessages.StatusCodes` for success 200; validation 400; authentication/access-required 401; access-denied 403; not-found 404; revision-conflict 409; persistence-unavailable 503. `SafeError` exposes only validation, access-denied, not-found, revision-conflict and persistence-unavailable.

- [ ] **Step 6: Run focused and complete Command tests**

```powershell
dotnet test tests/Application/ArturRios.Cerberus.Command.Tests/ArturRios.Cerberus.Command.Tests.csproj --filter "FullyQualifiedName~RevokeSoftwareAccess"
dotnet test tests/Application/ArturRios.Cerberus.Command.Tests/ArturRios.Cerberus.Command.Tests.csproj
```

Expected: all Command tests pass and no existing command contract changes.

- [ ] **Step 7: Commit and task-review**

```powershell
git add src/Domain/ArturRios.Cerberus.Domain/Software/SoftwareGrantRevocationContracts.cs src/Application/ArturRios.Cerberus.Command/Software tests/Application/ArturRios.Cerberus.Command.Tests/RevokeSoftwareAccessContractTests.cs tests/Application/ArturRios.Cerberus.Command.Tests/RevokeSoftwareAccessHandlerTests.cs
git commit -m "feat: add software revocation command"
```

Record the fresh task-review verdict and evidence in the ledger before Task 2.

### Task 2: Atomic PostgreSQL revocation

**Files:**

- Create: `src/Infrastructure/ArturRios.Cerberus.Data/Software/SoftwareGrantRevocationStore.cs`
- Create: `tests/Infrastructure/ArturRios.Cerberus.Data.Tests/SoftwareGrantRevocationStoreTests.cs`
- Modify: `tests/Infrastructure/ArturRios.Cerberus.Data.Tests/SoftwareGrantStoreTests.cs` only to expose a reusable setup member required by both stores.

**Interfaces:**

- Consumes: `ISoftwareGrantRevocationStore`, `SoftwareGrantSetup`, `SoftwareGrantState`, the existing three software-grant DbSets, account/session/profile lifecycle tables, Npgsql transactions and `ProtocolBinary.MaxInteger`.
- Produces: `SoftwareGrantRevocationStore.RevokeAsync(SoftwareGrantRevocationRequest, CancellationToken)` with the exact safe error contract and one verified reduced aggregate.

- [ ] **Step 1: Write failing mixed-target success and preservation tests**

Create a valid grant with `SoftwareGrantStore.GrantAsync`, snapshot the owner vault and grant aggregate, revoke revision 1, and assert revision 2, `Revoked`, fresh stamp, empty recipient key and zero typed targets. Compare explicit before/after evidence to prove the owner account/session/protection, records, collections, profiles, memberships, ciphertext, key epochs, revisions, sequences and unrelated grant remain unchanged.

```csharp
Assert.Null((await GrantStore().GrantAsync(s.Request(), default)).Error);
var beforeVault = await SoftwareGrantSetup.Vault(fixture, s.Owner.InternalId);
var result = await Store().RevokeAsync(
    new(s.Owner.Actor, s.Owner.Verifier, s.Grant, new(1)), default);
Assert.Equal(new SoftwareGrantRevocationDetails(s.Grant, 2), result.Data);
Assert.Equal(beforeVault, await SoftwareGrantSetup.Vault(fixture, s.Owner.InternalId));
Assert.Equal(SoftwareGrantState.Revoked, saved.State);
Assert.Empty(saved.RecipientKey);
Assert.Empty(await db.SoftwareGrantRecords.Where(x => x.GrantId == saved.Id).ToArrayAsync());
Assert.Empty(await db.SoftwareGrantCollections.Where(x => x.GrantId == saved.Id).ToArrayAsync());
```

- [ ] **Step 2: Run the focused store tests and confirm RED**

```powershell
dotnet test tests/Infrastructure/ArturRios.Cerberus.Data.Tests/ArturRios.Cerberus.Data.Tests.csproj --filter "FullyQualifiedName~SoftwareGrantRevocationStoreTests"
```

Expected: compile failure because `SoftwareGrantRevocationStore` does not exist.

- [ ] **Step 3: Implement validation, snapshot and canonical lock prefix**

Reject invalid request objects before opening a context. Inside one transaction, capture exact actor/account/session/selected-profile/grant/record-target/collection-target evidence. Classify authority in this order:

```csharp
private static string? AuthorityError(Snapshot snapshot) => snapshot switch
{
    { ActorActive: false } => "not_found",
    { SessionAllowed: false } => "vault_access_denied",
    { GrantVisible: false } => "not_found",
    { RelationallyCorrupt: true } => "persistence_unavailable",
    _ => null
};
```

Lock owner account, matching vault session, optional selected profile, then exact software grant. Re-run the snapshot after every lock wait. An initially authorized grant that changes while waiting is `revision_conflict`; a grant hidden before the first snapshot is `not_found`.

- [ ] **Step 4: Add failing denial, corruption and rollback tests**

Cover missing/foreign/terminal/already-revoked grants; inactive/terminal actor; missing, revoked, stale, expired and profile-invalid sessions; stale expected revision; revision exhaustion; wrong owner links; duplicate public IDs; cross-owner child rows; child rows pointing outside the exact grant; database/timeout/internal-cancellation faults at initial read, CAS, each delete and post-write read; caller cancellation; then retry after every injected rollback. Prove malformed opaque `RecipientKey` and `RecipientKeyEnvelope` bytes do not block erasure.

```csharp
[InlineData("missing", "not_found")]
[InlineData("foreign", "not_found")]
[InlineData("revoked", "not_found")]
[InlineData("stale", "revision_conflict")]
[InlineData("exhausted", "revision_conflict")]
public async Task GivenUnavailableGrantState_WhenRevoking_ThenFailWithoutMutation(
    string mode, string error)
```

- [ ] **Step 5: Implement the guarded transition, exact deletes and postconditions**

Use a single database-time authorization statement as the linearization point. It must constrain internal ID, public ID, owner, Active state, expected revision and captured evidence:

```sql
UPDATE cerberus.software_grant AS g
SET state=@revoked,
    revision=@revision,
    recipient_key=''::bytea,
    concurrency_stamp=@stamp
FROM actor a CROSS JOIN session s CROSS JOIN captured frozen
WHERE g.id=@id
  AND g.public_id=@grant
  AND g.account_id=a.id
  AND g.state=@active_grant
  AND g.revision=@previous
  AND frozen.stable=@stable
  AND frozen.aggregate=@aggregate
```

Require exactly one affected row. Delete record and collection children with both `account_id=@owner` and `grant_id=@id`; compare each affected count with its captured array length. Re-read with only the natural time predicate bypassed and require: same owner/public/software identity, `Revoked`, exact revision/stamp, empty recipient key, no children, unchanged stable/collateral evidence. Commit only after all checks pass.

Map SQL constraint/serialization/deadlock conflicts that represent a concurrent winner to `revision_conflict`; database, JSON, format, timeout, affected-row and postcondition faults to `persistence_unavailable`; propagate caller cancellation.

- [ ] **Step 6: Run focused and complete Data tests**

```powershell
dotnet test tests/Infrastructure/ArturRios.Cerberus.Data.Tests/ArturRios.Cerberus.Data.Tests.csproj --filter "FullyQualifiedName~SoftwareGrantRevocationStoreTests"
dotnet test tests/Infrastructure/ArturRios.Cerberus.Data.Tests/ArturRios.Cerberus.Data.Tests.csproj
```

Expected: all Data tests pass; every injected failure leaves the byte-for-byte pre-call aggregate and unrelated vault evidence intact.

- [ ] **Step 7: Commit and task-review**

```powershell
git add src/Infrastructure/ArturRios.Cerberus.Data/Software/SoftwareGrantRevocationStore.cs tests/Infrastructure/ArturRios.Cerberus.Data.Tests/SoftwareGrantRevocationStoreTests.cs tests/Infrastructure/ArturRios.Cerberus.Data.Tests/SoftwareGrantStoreTests.cs
git commit -m "feat: revoke software grants atomically"
```

Omit the existing setup file from `git add` when no shared-helper edit was required. Record the fresh task-review verdict and evidence before Task 3.

### Task 3: Concurrency and state-transition hardening

**Files:**

- Create: `tests/Infrastructure/ArturRios.Cerberus.Data.Tests/SoftwareGrantRevocationStoreConcurrencyTests.cs`
- Modify: `src/Infrastructure/ArturRios.Cerberus.Data/Software/SoftwareGrantRevocationStore.cs`

**Interfaces:**

- Consumes: the Task 2 store, `SoftwareGrantStore.GrantAsync` reactivation path, real PostgreSQL row locks and the existing test fixture's command interceptors/barriers.
- Produces: deterministic conflict/not-found behavior across concurrent revocation, reactivation, account, session, profile and aggregate changes.

- [ ] **Step 1: Write two-revoker and UC-42 reactivation race tests**

Block each contender at the guarded statement and prove one revision-1 revocation returns revision 2 while the other returns `revision_conflict` if it captured Active, or `not_found` if it begins after commit. From a revoked revision-2 row, race UC-42 reactivation against a stale revocation attempt and assert the winner's complete aggregate, never a mixed state.

```csharp
var results = await Task.WhenAll(first, second);
Assert.Single(results, x => x.Data == new SoftwareGrantRevocationDetails(s.Grant, 2));
Assert.Single(results, x => x.Error == "revision_conflict");
AssertCompleteRevokedAggregate(await SoftwareGrantSetup.Aggregate(fixture, s.Grant), 2);
```

- [ ] **Step 2: Run the concurrency tests and confirm RED**

```powershell
dotnet test tests/Infrastructure/ArturRios.Cerberus.Data.Tests/ArturRios.Cerberus.Data.Tests.csproj --filter "FullyQualifiedName~SoftwareGrantRevocationStoreConcurrencyTests"
```

Expected: at least one test fails until wait/recheck and postauthorization behavior is complete.

- [ ] **Step 3: Add lock-boundary authority-change tests**

At the owner, session and grant waits, independently change account lifecycle, policy revision, revocation generation, session revocation, session profile selection, selected-profile lifecycle, grant revision/state and child membership. Assert loss of actor/grant visibility follows the initial-versus-captured rule, current-session loss is `vault_access_denied`, other captured authorized-state changes are `revision_conflict`, and every loser preserves the winner's full aggregate.

Add one database-time expiry test: set expiry after capture but before the guarded statement and require denial; set expiry immediately after the guarded statement and require the already-authorized transaction to finish atomically.

- [ ] **Step 4: Harden snapshot/recheck logic and canonical locks**

Keep the lock prefix `account -> session -> optional profile -> software_grant`, which is compatible with UC-42's longer `account -> session -> optional profile -> targets -> software_grant` prefix. The guarded statement must repeat database-time authority. Post-write snapshot may bypass only natural time expiry:

```csharp
var post = await SnapshotAsync(db, request, ct, authorized: true);
if (post.Stable != captured.Stable || post.Collateral != captured.Collateral
    || !post.GrantRevoked || post.RecordTargetCount != 0
    || post.CollectionTargetCount != 0)
    return new(Error: "persistence_unavailable");
```

Do not bypass session revocation, policy/revocation-generation mismatch, invalid selected profile, account lifecycle or ownership.

- [ ] **Step 5: Run focused concurrency and complete Data suites**

```powershell
dotnet test tests/Infrastructure/ArturRios.Cerberus.Data.Tests/ArturRios.Cerberus.Data.Tests.csproj --filter "FullyQualifiedName~SoftwareGrantRevocationStore"
dotnet test tests/Infrastructure/ArturRios.Cerberus.Data.Tests/ArturRios.Cerberus.Data.Tests.csproj
```

Expected: one bounded winner per revision, no deadlocks, no partial loser mutations and all Data tests pass.

- [ ] **Step 6: Commit and task-review**

```powershell
git add src/Infrastructure/ArturRios.Cerberus.Data/Software/SoftwareGrantRevocationStore.cs tests/Infrastructure/ArturRios.Cerberus.Data.Tests/SoftwareGrantRevocationStoreConcurrencyTests.cs
git commit -m "test: cover software revocation races"
```

Record the fresh task-review verdict and evidence before Task 4.

### Task 4: HTTP route, dependency composition and OpenAPI

**Files:**

- Modify: `src/Presentation/ArturRios.Cerberus.WebApi/Controllers/SoftwareGrantsController.cs`
- Modify: `src/Presentation/ArturRios.Cerberus.WebApi/Startup.cs`
- Create: `tests/Presentation/ArturRios.Cerberus.WebApi.Tests/SoftwareGrantRevocationHttpTests.cs`
- Modify: `docs/contracts/openapi.json`

**Interfaces:**

- Consumes: `RevokeSoftwareAccessCommand`, `SoftwareGrantRevocationMessages.StatusCodes`, `ISoftwareGrantRevocationStore`, `SoftwareGrantRevocationStore`, `VaultProofBody`, `NoStoreMiddleware` and existing protected human authentication.
- Produces: externally reachable `DELETE /api/software-grants/{id}` with exact 200/400/401/403/404/409/413/503 behavior.

- [ ] **Step 1: Write a failing successful DELETE test**

Use the real web host and PostgreSQL fixture to create a software grant, then send a human-authenticated DELETE with body `{"expectedRevision":1}` and the vault handle. Assert 200, exact message/data, no-store and the fully reduced database aggregate.

```csharp
using var request = new HttpRequestMessage(HttpMethod.Delete,
    $"/api/software-grants/{s.Grant:D}")
{
    Content = JsonContent.Create(new { expectedRevision = 1 })
};
request.Headers.TryAddWithoutValidation("X-Cerberus-Vault-Access", s.Access);
using var response = await Gateway.Client.SendAsync(request);
Assert.Equal(HttpStatusCode.OK, response.StatusCode);
Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
```

- [ ] **Step 2: Run the focused HTTP test and confirm RED**

```powershell
dotnet test tests/Presentation/ArturRios.Cerberus.WebApi.Tests/ArturRios.Cerberus.WebApi.Tests.csproj --filter "FullyQualifiedName~SoftwareGrantRevocationHttpTests"
```

Expected: 405 because the DELETE action and registrations do not exist.

- [ ] **Step 3: Register services and implement the strict controller action**

Register `ISoftwareGrantRevocationStore`/`SoftwareGrantRevocationStore`, validator and handler beside the existing software grant registrations. Add `[HttpDelete("{id}")] [VaultProofBody]` with exact response annotations. Reject query parameters, duplicate vault headers, invalid media parameters and a noncanonical route before mediator dispatch:

```csharp
if (Request.Query.Count != 0
    || Request.Headers["X-Cerberus-Vault-Access"].Count > 1
    || !MediaTypeHeaderValue.TryParse(Request.ContentType, out var media)
    || media.Parameters.Count > 1
    || media.Parameters.Any(p => !string.Equals(
        p.Name, "charset", StringComparison.OrdinalIgnoreCase))
    || !Guid.TryParseExact(id, "D", out var grantId)
    || grantId == Guid.Empty || id != grantId.ToString("D"))
    return BadRequest(ProcessOutput.New.WithError("validation_failed"));
```

Require a nonzero parsed human `id` claim, copy the single raw vault header, call `command.SetContext(actor, vaultAccess, grantId)`, forward the caller cancellation token and use `SoftwareGrantRevocationMessages.StatusCodes`.

- [ ] **Step 4: Add exhaustive transport, authority and disclosure tests**

Cover no/invalid/zero human identity; no/duplicate/empty/malformed vault header; query; uppercase/braced/compact/zero/malformed route IDs; empty/oversized body; absent/wrong/parameterized media type; invalid charset; compression; BOM; invalid UTF-8; duplicate/unknown/case-forged fields; string/fractional/exponent/overflow revisions; 401, access 403, absent/foreign/revoked 404, stale 409, identity 503 and persistence 503.

For every case assert no-store on nonempty responses, empty 413, no mutation on failures, and absence of `softwareIdentity`, `recipientKey`, `keyEnvelope`, `targets`, `record`, `collection`, `ciphertext` and known secret bytes in every response body.

- [ ] **Step 5: Run focused and complete Web tests**

```powershell
dotnet test tests/Presentation/ArturRios.Cerberus.WebApi.Tests/ArturRios.Cerberus.WebApi.Tests.csproj --filter "FullyQualifiedName~SoftwareGrantRevocationHttpTests"
dotnet test tests/Presentation/ArturRios.Cerberus.WebApi.Tests/ArturRios.Cerberus.WebApi.Tests.csproj
```

Expected: all Web API tests pass.

- [ ] **Step 6: Regenerate and verify the OpenAPI contract**

```powershell
python scripts/openapi.py --write
python scripts/openapi.py
python -m unittest discover -s scripts -p "test_openapi.py" --verbose
```

Inspect `/api/software-grants/{id}` and require DELETE, UUID path input, required JSON body with only `expectedRevision`, success schema with only `grantId` and `revision`, and 200/400/401/403/404/409/413/503 responses.

- [ ] **Step 7: Commit and task-review**

```powershell
git add src/Presentation/ArturRios.Cerberus.WebApi/Controllers/SoftwareGrantsController.cs src/Presentation/ArturRios.Cerberus.WebApi/Startup.cs tests/Presentation/ArturRios.Cerberus.WebApi.Tests/SoftwareGrantRevocationHttpTests.cs docs/contracts/openapi.json
git commit -m "feat: expose software access revocation"
```

Record the fresh task-review verdict and evidence before Task 5.

### Task 5: Runtime documentation and repository verification

**Files:**

- Create: `docs/security/software-access-revocation-api.md`
- Modify: `docs/operations/foundation-status.md`
- Modify: `CHANGELOG.md`
- Modify at Gate 3 only: `README.md`

**Interfaces:**

- Consumes: the implemented HTTP/store behavior and the repository's verification scripts.
- Produces: operator/developer documentation, release traceability and complete evidence for Gates 2 and 3.

- [ ] **Step 1: Write the runtime contract document**

Document the exact route, headers, one-field body, success envelope, status/error table, owner/session checks, active-to-revoked transition, erased key/target material, optimistic concurrency, reactivation rule, no-store behavior and the limitation that already copied ciphertext cannot be remotely retracted. State explicitly that the path does not call Heimdall application APIs and does not unblock UC-43.

- [ ] **Step 2: Update implementation status and changelog**

Add UC-44 to `docs/operations/foundation-status.md` with the concrete route, state transition, erasure/postcondition guarantees and test families. Add an Unreleased changelog entry naming the strict DELETE API, atomic target erasure, nonrevealing denial and concurrency coverage. Leave `README.md` unchanged until Gate 3 approval.

- [ ] **Step 3: Run documentation and diff checks**

```powershell
python scripts/verify_specs.py
git diff --check
rg -n "UC-44|software-access-revocation|software_access_revoked" docs CHANGELOG.md
```

Expected: specification verification and whitespace checks pass, and each documented claim maps to the implemented files/tests.

- [ ] **Step 4: Commit documentation**

```powershell
git add docs/security/software-access-revocation-api.md docs/operations/foundation-status.md CHANGELOG.md
git commit -m "docs: document software access revocation"
```

## Gate 2 — implementation complete, before testing

- [ ] Complete the selected execution method's whole-branch review over base `750457fb2785d29b111a6124755dc263cea920e9` through feature HEAD. Give the reviewer the five review-focus items, design, plan and focused test evidence; resolve every Critical/Important finding and record accepted minor rulings.
- [ ] Present the implemented main flow, AF-01 through AF-05 mapping, focused test evidence, deviations and open release limitations. Obtain explicit approval before moving issue #45 from In Progress to Testing or starting the clean repository verification sequence.

- [ ] **Step 5: Run the clean repository verification sequence**

```powershell
python scripts/verify_specs.py
python -m unittest discover -s scripts -p "test_*.py" --verbose
dotnet restore src/ArturRios.Cerberus.sln --locked-mode
python scripts/vulnerabilities.py
dotnet build src/ArturRios.Cerberus.sln --configuration Release --no-restore
dotnet test src/ArturRios.Cerberus.sln --configuration Release --no-build --filter "Category=Unit" --collect:"XPlat Code Coverage"
dotnet test src/ArturRios.Cerberus.sln --configuration Release --no-build --filter "Category=Functional" --collect:"XPlat Code Coverage"
dotnet test src/ArturRios.Cerberus.sln --configuration Release --no-build --logger trx --collect:"XPlat Code Coverage"
python scripts/coverage.py --report-only
python scripts/openapi.py
python tools/protocol-harness/verify.py --check docs/security/interoperability-vectors.json
python -m unittest discover -s tools/protocol-harness/tests -v
docker build --pull=false -t cerberus-api:uc-44 .
git diff --check
```

Expected: locked restore/audit/build pass; all six test projects pass; every production assembly has at least 90% line coverage and aggregate branch coverage is reported; specs/OpenAPI/native protocol checks and Docker build pass; whitespace is clean.

## Use-case traceability

- Main flow: Tasks 1, 2 and 4 prove trusted owner context, current vault access, atomic revocation/erasure and the minimal grant/revision response.
- AF-01: Tasks 1 and 4 reject malformed route, transport, body, revision and access handle before persistence.
- AF-02: Tasks 2 and 4 return the same nonrevealing 404 for absent, foreign, terminal and already-revoked grants.
- AF-03: Tasks 1, 2 and 4 distinguish missing human authentication/access (401) from a currently authenticated but invalid vault session (403).
- AF-04: Tasks 2 and 3 prove stale revisions, exhaustion, changed captured state and concurrent winners return 409 without overwriting the winner.
- AF-05: Tasks 1 through 4 prove persistence, serialization, timeout, relational-corruption and post-write faults fail closed with 503 and no partial aggregate.
- BR-10/15/17: Tasks 2 and 3 erase server-held access material while preserving already-copied-ciphertext limits and requiring exact-revision reauthorization for later UC-42 reactivation.

## Whole-branch review and delivery gates

- [ ] At Gate 3, present the exact clean-suite commands and counts, coverage of the main flow and every AF-xx path, OpenAPI/audit/protocol/Docker evidence, and skipped or deferred checks. Obtain explicit approval before editing README, pushing or opening the PR.
- [ ] After Gate 3 approval, update only issue #45's README backlog row from Todo to Done and advance M-04 from 4/6 to 5/6, rerun `python scripts/verify_specs.py` and `git diff --check`, and commit `docs: mark UC-44 complete`.
- [ ] Push the exact reviewed feature HEAD, open exactly one PR targeting `develop` with `Closes #45`, attach the PR to this task and confirm the PR base/head did not change.
- [ ] Wait for the exact-head `branch-policy`, `test` and `docker` checks. Treat any new commit as invalidating prior approvals and exact-head evidence.
- [ ] Leave PR approval, merge and branch deletion to the user. After the user confirms the merge, present Gate 4 and obtain explicit approval before closing issue #45 or moving it to Done.
- [ ] After Gate 4 approval, confirm the README tracking landed, close issue #45 if the merge did not close it, move project status to Done, verify the squash tree equals the reviewed feature tree, and sync local `develop` without rewriting user changes.
- [ ] Save `.superpowers/checkpoints/uc-44-2026-10-13-complete/RESUME.md`, preserve the tracked ledger, remove only verified ignored UC-44 scratch/evidence files, and finish with a clean checkout whose `develop == origin/develop`.
