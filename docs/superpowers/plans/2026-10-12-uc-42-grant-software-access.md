Attempting to perform the InitializeDefaultDrives operation on the 'FileSystem' provider failed.
Hello world!
[ERROR] Failed on checkpwshupdate command
Program 'winget.exe' failed to run: An error occurred trying to start process 'C:\Users\Artur\AppData\Local\Microsoft\WindowsApps\winget.exe' with working directory 'D:\Repositories\cerberus-api'. The file cannot be accessed by the system.At C:\Users\Artur\OneDrive\Documentos\PowerShell\Microsoft.PowerShell_profile.ps1:19 char:18
+ . eckOutput = winget list --id Microsoft.PowerShell --upgrade-available .
+               ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~.
# UC-42 Grant Software Access Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement strict, atomic creation and explicit reactivation of read-only software grants over owned records and collections through `POST /api/software-grants`.

**Architecture:** Add a narrow Heimdall application verifier, immutable software-grant command contracts, and a normalized PostgreSQL aggregate with one grant row plus typed record/collection target rows. Validate each client-provisioned recipient envelope against the current owner pin, target key epoch, Heimdall software identity and submitted software public key before one guarded transaction writes the complete aggregate.

**Tech Stack:** .NET 10, ASP.NET Core Web API, ArturRios mediator/output libraries, FluentValidation, Entity Framework Core 10, Npgsql/PostgreSQL, xUnit/Moq, Testcontainers, System.Text.Json, the existing native P-256 recipient-envelope protocol and Python verification scripts.

**Spec:** `docs/superpowers/specs/2026-10-12-uc-42-grant-software-access-design.md`

## Global Constraints

- Implement only GitHub issue #43 / UC-42, FR-SW-01 and FR-SW-02, with AF-01 through AF-05 and BR-10, BR-15 and BR-28.
- Base and PR target are exact `develop` commit `d730c91c1b3577802fc12568959054900d14b71d`; use branch `feature/uc-42-grant-software-access`.
- Preserve one issue, one branch and one pull request; transition project #15 through Todo, In Progress, Testing and Done at the documented gates.
- Add no dependency, plaintext field, usable decryption key, software credential, retrieval path, revocation path, target-update path or generic Heimdall application-management proxy.
- Use `POST /api/software-grants`, strict bounded UTF-8 JSON, one canonical vault-access header, no query and no-store responses.
- Keep success output to exactly `grantId` and `revision`; never echo targets, accounts, application metadata, keys, envelopes or ciphertext.
- Verify the Heimdall application before opening a database transaction; no PostgreSQL lock may span the external call.
- Require a nonempty unique target set of exact `record` or `collection` targets and one `cerberus-recipient-wrap-v1` envelope per target.
- Enforce active ownership and the exact current Master or selected-profile scope for every target; a shared foreign target is never redelegable.
- Use an explicit nullable expected revision: null creates revision 1; a matching positive revision reactivates only the same revoked aggregate at revision + 1.
- Store one normalized grant plus typed target associations atomically; do not encode the target set as JSON or create one grant per target.
- Preserve all record, collection, profile, membership, ciphertext, key epoch, revision, sequence and owner metadata.
- Propagate caller cancellation; classify safe failures as 400, 401, 403, nonrevealing 404, 409 or 503 exactly as the spec defines.
- Meet at least 90% line coverage in every merged production assembly and report aggregate branch coverage.
- Independent protocol/security/client qualification remains a release gate under the recorded development review deferral.

## Review Focus

1. A successful but mismatched Heimdall application ID/scope must be 503, while an explicitly missing or deleted application must be nonrevealing 404; Task 1 pins both.
2. Two differently ordered mixed-target requests must acquire the same canonical `(kind, public_id)` lock order and complete without deadlock; Task 4 pins this with real PostgreSQL concurrency.
3. A selected-profile caller must not grant a directly owned but unselected target, and must not redelegate a visible shared target; Task 4 pins both visibility classes.
4. Reactivation must replace the whole old target/key aggregate only after the exact revoked revision wins; Task 4 pins stale, concurrent and post-write-failure rollback cases.
5. Unknown nested public-key/target/envelope members and alternate UUID/number spellings must fail before Heimdall or persistence calls; Tasks 2 and 5 pin the serializer and HTTP boundaries.

---

## Gate transition before Task 1

- [ ] Confirm `git status --short`, `git rev-parse HEAD` and `git rev-parse origin/develop` show only the approved spec and plan on exact base `d730c91c1b3577802fc12568959054900d14b71d`.
- [ ] Create `feature/uc-42-grant-software-access` from that base, move issue #43 to In Progress in project #15, and create the tracked review ledger at `.superpowers/sdd/2026-10-12-uc-42-grant-software-access/ledger.md`.
- [ ] Commit the approved design, plan and initialized ledger with `docs: design software access grant` before production changes.

### Task 1: Narrow Heimdall application verifier

**Files:**

- Modify: `src/Application/ArturRios.Cerberus.Shared/Identity/HeimdallContracts.cs`
- Modify: `src/Application/ArturRios.Cerberus.Shared/Identity/HeimdallClient.cs`
- Modify: `tests/Application/ArturRios.Cerberus.Shared.Tests/HeimdallClientTests.cs`

**Interfaces:**

- Consumes: `CerberusOptions.HeimdallScopeId`, `CerberusOptions.HeimdallServiceCredential`, `HeimdallTokenValidator.ValidateServiceAsync`, and the existing bounded `HeimdallClient.SendAsync` response parser.
- Produces: `HeimdallApplicationStatus { Active, NotFound, Unavailable }` and `IHeimdallClient.VerifyApplicationAsync(Guid applicationId, CancellationToken cancellationToken)`.

- [ ] **Step 1: Write failing active-application contract tests**

Add tests whose fake Heimdall response is an exact success envelope containing the requested public application ID, configured scope ID and `isDeleted: false`. Assert the client sends exactly `GET /api/scopes/{scopeId:D}/applications/{applicationId:D}` with the configured bearer service credential and returns `HeimdallApplicationStatus.Active`.

```csharp
var status = await Client(handler).VerifyApplicationAsync(applicationId, cancellation.Token);
Assert.Equal(HeimdallApplicationStatus.Active, status);
Assert.Equal(HttpMethod.Get, handler.Request!.Method);
Assert.Equal($"/api/scopes/{scopeId:D}/applications/{applicationId:D}", handler.Request.RequestUri!.PathAndQuery);
Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
```

- [ ] **Step 2: Run the focused test and confirm RED**

Run:

```powershell
dotnet test tests/Application/ArturRios.Cerberus.Shared.Tests/ArturRios.Cerberus.Shared.Tests.csproj --filter "FullyQualifiedName~HeimdallClientTests"
```

Expected: compile failure because `VerifyApplicationAsync` and `HeimdallApplicationStatus` do not exist.

- [ ] **Step 3: Add the exact verifier contract and minimal active path**

Add this public contract and a private strict DTO; do not return application name, owner or timestamps to callers:

```csharp
public enum HeimdallApplicationStatus { Active, NotFound, Unavailable }

public interface IHeimdallClient
{
    Task<HeimdallApplicationStatus> VerifyApplicationAsync(
        Guid applicationId, CancellationToken cancellationToken);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
private sealed record ApplicationSnapshot(
    [property: JsonRequired] Guid Id,
    [property: JsonRequired] string Name,
    [property: JsonRequired] Guid ScopeId,
    [property: JsonRequired] Guid OwnerId,
    [property: JsonRequired] bool IsDeleted,
    [property: JsonRequired] DateTime CreatedAt,
    [property: JsonRequired] DateTime UpdatedAt);
```

Validate the configured service credential locally, call the scoped GET route, deserialize with duplicate-property rejection, require exact requested ID/scope and explicit nondeleted state, and return only the enum.
The DTO mirrors Heimdall's complete inspected `ApplicationOutput`; Cerberus validates
the shape but discards name, owner and timestamps after classification.

- [ ] **Step 4: Add failing classification and robustness tests**

Cover empty IDs, invalid service credentials, 401, 403, 404, 500, timeout, transport error, wrong media type, oversized body, duplicate/unknown/missing/case-forged fields, invalid UUIDs, mismatched ID, mismatched scope, explicit deletion and caller cancellation. Assert only 404 or explicit deletion produces `NotFound`; malformed/mismatched successes and dependency/configuration failures produce `Unavailable`; cancellation propagates.

```csharp
[InlineData("wrong-id", HeimdallApplicationStatus.Unavailable)]
[InlineData("wrong-scope", HeimdallApplicationStatus.Unavailable)]
[InlineData("deleted", HeimdallApplicationStatus.NotFound)]
[InlineData("not-found", HeimdallApplicationStatus.NotFound)]
[InlineData("forbidden", HeimdallApplicationStatus.Unavailable)]
public async Task GivenApplicationResponse_WhenVerifying_ThenClassifyFailClosed(
    string kind, HeimdallApplicationStatus expected)
```

- [ ] **Step 5: Implement the complete fail-closed mapping and run Shared tests**

Run:

```powershell
dotnet test tests/Application/ArturRios.Cerberus.Shared.Tests/ArturRios.Cerberus.Shared.Tests.csproj
```

Expected: all Shared tests pass and no existing person/login behavior changes.

- [ ] **Step 6: Commit and task-review**

```powershell
git add src/Application/ArturRios.Cerberus.Shared/Identity tests/Application/ArturRios.Cerberus.Shared.Tests/HeimdallClientTests.cs
git commit -m "feat: verify software identities"
```

Record the fresh reviewer verdict and evidence in the UC-42 ledger before Task 2.

### Task 2: Domain and command boundary

**Files:**

- Create: `src/Domain/ArturRios.Cerberus.Domain/Software/SoftwareGrantContracts.cs`
- Create: `src/Application/ArturRios.Cerberus.Command/Software/GrantSoftwareAccessCommand.cs`
- Create: `src/Application/ArturRios.Cerberus.Command/Software/GrantSoftwareAccessValidator.cs`
- Create: `src/Application/ArturRios.Cerberus.Command/Software/GrantSoftwareAccessHandler.cs`
- Create: `src/Application/ArturRios.Cerberus.Command/Software/GrantSoftwareAccessOutput.cs`
- Create: `src/Application/ArturRios.Cerberus.Command/Software/SoftwareGrantMessages.cs`
- Create: `tests/Application/ArturRios.Cerberus.Command.Tests/GrantSoftwareAccessHandlerTests.cs`

**Interfaces:**

- Consumes: `IHeimdallClient.VerifyApplicationAsync`, `PublicJwk`, `RecipientEnvelope`, `OpaqueAccessHandle.TryHash`, `VaultResult<T>`, FluentValidation and mediator command interfaces.
- Produces: `SoftwareRecipientKey`, `SoftwareGrantTargetInput`, `SoftwareGrantInput`, `SoftwareGrantRequest`, `SoftwareGrantDetails`, `ISoftwareGrantStore.GrantAsync`, and `GrantSoftwareAccessCommand` returning `GrantSoftwareAccessOutput`.

- [ ] **Step 1: Write failing strict-shape and semantic input tests**

Define tests for exactly five required top-level members, explicit null/positive `expectedRevision`, exactly four recipient-key members, exactly three target members, nonempty targets, unique `(kind,id)` tuples, canonical nonzero UUIDs, exact lowercase kinds, valid P-256 key material, valid envelopes, one identity/key/grant/revision across all envelopes and rejection of every unknown, duplicate, omitted, case-forged, string-number, fractional-number and noncanonical UUID form.

```csharp
AssertFields(command, ["expectedRevision", "grantId", "recipientKey", "softwareIdentityId", "targets"]);
AssertFields(command.RecipientKey, ["crv", "kty", "x", "y"]);
AssertFields(command.Targets.Single(), ["id", "keyEnvelope", "kind"]);
Assert.True(command.ToInput().IsValid());
```

- [ ] **Step 2: Run the focused test and confirm RED**

```powershell
dotnet test tests/Application/ArturRios.Cerberus.Command.Tests/ArturRios.Cerberus.Command.Tests.csproj --filter "FullyQualifiedName~GrantSoftwareAccessHandlerTests"
```

Expected: compile failure because the software grant types do not exist.

- [ ] **Step 3: Implement immutable domain inputs and the store interface**

Use these exact shapes with `JsonUnmappedMemberHandling.Disallow`, `JsonNumberHandling.Strict` and `JsonRequired` on every public input member:

```csharp
public sealed record SoftwareRecipientKey(string Crv, string Kty, string X, string Y)
{
    public PublicJwk ToPublicJwk() => new(Crv, Kty, X, Y);
    public bool IsValid() => ToPublicJwk().IsValid();
}

public sealed record SoftwareGrantTargetInput(
    string Kind, Guid Id, RecipientEnvelope KeyEnvelope);

public sealed record SoftwareGrantInput(
    Guid GrantId, Guid SoftwareIdentityId, long? ExpectedRevision,
    SoftwareRecipientKey RecipientKey, SoftwareGrantTargetInput[] Targets);

public sealed record SoftwareGrantRequest(
    Guid Actor, string AccessVerifier, SoftwareGrantInput Input);

public sealed record SoftwareGrantDetails(Guid GrantId, long Revision);

public interface ISoftwareGrantStore
{
    Task<VaultResult<SoftwareGrantDetails>> GrantAsync(
        SoftwareGrantRequest request, CancellationToken cancellationToken);
}
```

`SoftwareGrantInput.IsValid()` must enforce revision range `< ProtocolBinary.MaxInteger` for reactivation, target uniqueness, exact target kinds, the common software identity/key fingerprint, and envelope revision `(ExpectedRevision ?? 0) + 1` without verifying target epochs or owner signatures prematurely.

- [ ] **Step 4: Write failing handler boundary tests**

Cover authentication/access/validation precedence, exact access-handle hashing, cancellation, Active/NotFound/Unavailable Heimdall outcomes, no store call on non-Active identity, exact request forwarding, safe store error allowlisting, malformed store success and exact two-field output.

```csharp
var result = await handler.HandleAsync(command, cancellation.Token);
Assert.Equal(new[] { "software_access_granted" }, result.Messages);
Assert.Equal(command.GrantId, result.Data!.GrantId);
Assert.Equal((command.ExpectedRevision ?? 0) + 1, result.Data.Revision);
AssertFields(result.Data, ["grantId", "revision"]);
```

- [ ] **Step 5: Implement validator, handler, messages and output**

The handler order is: cancellation, authenticated actor, present vault header, access-handle hash/input validation, Heimdall application verification, store call, safe result validation. Map application NotFound to `not_found`, Unavailable to `identity_unavailable`, and unknown store errors or malformed successes to `persistence_unavailable`. Define status codes for `software_access_granted` 201, `validation_failed` 400, authentication/access 401, `vault_access_denied` 403, `not_found` 404, `revision_conflict` 409 and both unavailable errors 503.

- [ ] **Step 6: Run focused and full Command tests**

```powershell
dotnet test tests/Application/ArturRios.Cerberus.Command.Tests/ArturRios.Cerberus.Command.Tests.csproj --filter "FullyQualifiedName~GrantSoftwareAccessHandlerTests"
dotnet test tests/Application/ArturRios.Cerberus.Command.Tests/ArturRios.Cerberus.Command.Tests.csproj
```

Expected: all Command tests pass.

- [ ] **Step 7: Commit and task-review**

```powershell
git add src/Domain/ArturRios.Cerberus.Domain/Software src/Application/ArturRios.Cerberus.Command/Software tests/Application/ArturRios.Cerberus.Command.Tests/GrantSoftwareAccessHandlerTests.cs
git commit -m "feat: add software grant contract"
```

Record the fresh reviewer verdict and evidence in the ledger before Task 3.

### Task 3: Normalized software-grant schema

**Files:**

- Create: `src/Domain/ArturRios.Cerberus.Domain/Resources/SoftwareGrant.cs`
- Create: `src/Domain/ArturRios.Cerberus.Domain/Resources/SoftwareGrantRecord.cs`
- Create: `src/Domain/ArturRios.Cerberus.Domain/Resources/SoftwareGrantCollection.cs`
- Modify: `src/Infrastructure/ArturRios.Cerberus.Data/AppDbContext.cs`
- Modify: `src/Infrastructure/ArturRios.Cerberus.Data/Configuration/ResourceModel.cs`
- Generate: `src/Infrastructure/ArturRios.Cerberus.Data/Migrations/*_SoftwareGrants.cs`
- Generate: `src/Infrastructure/ArturRios.Cerberus.Data/Migrations/*_SoftwareGrants.Designer.cs`
- Modify: `src/Infrastructure/ArturRios.Cerberus.Data/Migrations/AppDbContextModelSnapshot.cs`
- Create: `tests/Infrastructure/ArturRios.Cerberus.Data.Tests/SoftwareGrantModelTests.cs`

**Interfaces:**

- Consumes: `VersionedEntity`, `Account`, `VaultRecord`, `VaultCollection`, current snake-case model conventions and PostgreSQL migrations.
- Produces: `SoftwareGrantState`, three EF entities and `AppDbContext.SoftwareGrants`, `SoftwareGrantRecords`, `SoftwareGrantCollections`.

- [ ] **Step 1: Write failing model metadata tests**

Assert table names, columns, required byte arrays, concurrency revision, unique public ID, owner/application lookup index, composite owner/grant alternate key, typed association primary keys, same-account composite foreign keys, reverse target indexes and cascade behavior only for physical account/grant/target removal.

```csharp
Assert.Equal("software_grant", grant.GetTableName());
Assert.True(grant.FindIndex(grant.FindProperty(nameof(SoftwareGrant.PublicId))!)!.IsUnique);
Assert.True(grant.FindProperty(nameof(SoftwareGrant.Revision))!.IsConcurrencyToken);
Assert.Equal(["GrantId", "RecordId"], record.FindPrimaryKey()!.Properties.Select(x => x.Name));
```

- [ ] **Step 2: Run the model test and confirm RED**

```powershell
dotnet test tests/Infrastructure/ArturRios.Cerberus.Data.Tests/ArturRios.Cerberus.Data.Tests.csproj --filter "FullyQualifiedName~SoftwareGrantModelTests"
```

Expected: compile failure because the entities and DbSets do not exist.

- [ ] **Step 3: Add exact entity shapes and EF mapping**

Use these properties and no navigation-dependent business logic:

```csharp
public enum SoftwareGrantState { Active, Revoked }

public sealed class SoftwareGrant : VersionedEntity
{
    public Guid PublicId { get; set; }
    public long AccountId { get; set; }
    public Guid HeimdallSoftwarePublicId { get; set; }
    public byte[] RecipientKey { get; set; } = [];
    public SoftwareGrantState State { get; set; }
    public long Revision { get; set; } = 1;
}

public sealed class SoftwareGrantRecord
{
    public long AccountId { get; set; }
    public long GrantId { get; set; }
    public long RecordId { get; set; }
    public byte[] RecipientKeyEnvelope { get; set; } = [];
}

public sealed class SoftwareGrantCollection
{
    public long AccountId { get; set; }
    public long GrantId { get; set; }
    public long CollectionId { get; set; }
    public byte[] RecipientKeyEnvelope { get; set; } = [];
}
```

Map `(AccountId, Id)` as the grant alternate key. Each child must reference that key and the existing `(AccountId, target.Id)` alternate key so cross-account associations are impossible at the database layer.

- [ ] **Step 4: Generate and inspect the migration**

```powershell
dotnet ef migrations add SoftwareGrants --project src/Infrastructure/ArturRios.Cerberus.Data/ArturRios.Cerberus.Data.csproj --startup-project src/Presentation/ArturRios.Cerberus.WebApi/ArturRios.Cerberus.WebApi.csproj
```

Inspect `Up`, `Down`, designer and snapshot. Require three tables, the documented indexes/FKs, required binary columns and no change to any existing column, constraint, seed or sequence.

- [ ] **Step 5: Run model tests and a clean migration round trip**

```powershell
dotnet test tests/Infrastructure/ArturRios.Cerberus.Data.Tests/ArturRios.Cerberus.Data.Tests.csproj --filter "FullyQualifiedName~SoftwareGrantModelTests"
dotnet test tests/Infrastructure/ArturRios.Cerberus.Data.Tests/ArturRios.Cerberus.Data.Tests.csproj --filter "FullyQualifiedName~Migration"
```

Expected: metadata tests pass and the real PostgreSQL fixture can create the model from migrations.

- [ ] **Step 6: Commit and task-review**

```powershell
git add src/Domain/ArturRios.Cerberus.Domain/Resources src/Infrastructure/ArturRios.Cerberus.Data/AppDbContext.cs src/Infrastructure/ArturRios.Cerberus.Data/Configuration/ResourceModel.cs src/Infrastructure/ArturRios.Cerberus.Data/Migrations tests/Infrastructure/ArturRios.Cerberus.Data.Tests/SoftwareGrantModelTests.cs
git commit -m "feat: persist software grants"
```

Record the fresh reviewer verdict and evidence in the ledger before Task 4.

### Task 4: Guarded PostgreSQL aggregate writer

**Files:**

- Create: `src/Infrastructure/ArturRios.Cerberus.Data/Software/SoftwareGrantStore.cs`
- Create: `tests/Infrastructure/ArturRios.Cerberus.Data.Tests/SoftwareGrantStoreTests.cs`
- Create: `tests/Infrastructure/ArturRios.Cerberus.Data.Tests/SoftwareGrantStoreConcurrencyTests.cs`
- Modify: `tests/Infrastructure/ArturRios.Cerberus.Data.Tests/Support/DatabaseSeed.cs` only if a reusable software-grant seed helper is required by both new test classes.

**Interfaces:**

- Consumes: `ISoftwareGrantStore`, `SoftwareGrantRequest`, the three Task 3 DbSets, `CollectionGrantBinding.TryReadPins` for owner protection parsing, `RecipientEnvelope.Verify`, existing access-session/profile/record/collection visibility predicates and Npgsql transactions.
- Produces: `SoftwareGrantStore.GrantAsync(SoftwareGrantRequest, CancellationToken)` with exact safe errors and one atomic aggregate result.

- [ ] **Step 1: Write failing initial mixed-target success tests**

Create an active owner with Master access, valid protection, one record, one collection and two correctly signed software recipient envelopes. Assert revision 1, one active grant, two exact child rows, byte-identical stored recipient key/envelopes and no change to any account, session, protection, profile, record, collection, membership, revision, sequence, epoch or ciphertext evidence.

```csharp
var result = await Store().GrantAsync(Request(owner, record, collection), cancellation.Token);
Assert.Equal(new SoftwareGrantDetails(grantId, 1), result.Data);
Assert.Null(result.Error);
Assert.Equal(1, await db.SoftwareGrants.CountAsync());
Assert.Equal(1, await db.SoftwareGrantRecords.CountAsync());
Assert.Equal(1, await db.SoftwareGrantCollections.CountAsync());
```

- [ ] **Step 2: Run the focused store test and confirm RED**

```powershell
dotnet test tests/Infrastructure/ArturRios.Cerberus.Data.Tests/ArturRios.Cerberus.Data.Tests.csproj --filter "FullyQualifiedName~SoftwareGrantStoreTests"
```

Expected: compile failure because `SoftwareGrantStore` does not exist.

- [ ] **Step 3: Implement initial snapshot, authority classification and canonical locks**

Implement `SnapshotAsync` so one round trip captures actor/account/session/selection, every requested target's owner/lifecycle/epoch/envelope and profile visibility, relevant native collection-grant evidence for visible foreign targets, owner protection and candidate/colliding software grants. Reject before writes using these exact classes:

```csharp
private static string? AuthorityError(Snapshot snapshot) => snapshot switch
{
    { ActorActive: false } => "not_found",
    { SessionAllowed: false } => "vault_access_denied",
    { MissingOrHiddenTarget: true } => "not_found",
    { CorruptRelevantEvidence: true } => "persistence_unavailable",
    { VisibleForeignTarget: true } => "vault_access_denied",
    _ => null
};
```

Acquire owner account, session, optional selected profile, targets ordered by kind then public UUID, and grant rows ordered by public UUID. Re-run the snapshot after every wait and return `revision_conflict` when captured authorized state changes.

- [ ] **Step 4: Add failing target authority and envelope tests**

Cover Master and selected-profile success; unselected owned record/collection; missing, trashed, terminal and corrupt targets; visible valid foreign target 403; hidden/revoked/corrupt-native foreign target classification; malformed owner protection; stale target epoch; wrong owner, kind, ID, grant, revision, identity, recipient pin or author pin; mixed valid/invalid targets; duplicate target rows; and exhausted stored/input revisions. Assert zero grant/child mutations for every rejection.

- [ ] **Step 5: Implement native envelope validation and guarded initial insert**

Parse owner protection once after the owner lock. Serialize the already validated recipient key canonically. For each target call:

```csharp
target.KeyEnvelope.Verify(
    ownerPublicId,
    target.Kind,
    target.PublicId,
    target.KeyEpoch,
    input.GrantId,
    revision,
    input.SoftwareIdentityId,
    input.RecipientKey.ToPublicJwk(),
    ownerPins.AuthorKey)
```

Use one guarded grant insert followed by typed child inserts inside the same transaction. Reassert the captured JSON evidence/current session and reject any affected-row mismatch. Reload every row and compare IDs, state, revision, stamp and byte arrays before commit.

- [ ] **Step 6: Add failing duplicate and revoked-reactivation tests**

Cover active same-ID replay, active different-owner collision, revoked exact reactivation, stale/missing expectation, changed software identity, public-ID collision, terminal grant reservation, replacement with a different target set/key, child insert fault and post-write mismatch. Assert reactivation keeps the internal row/public ID/owner/software identity, advances exactly once, replaces all children/key/envelopes, and rolls back to the complete old revoked aggregate on any failure.

- [ ] **Step 7: Implement exact reactivation and safe exception mapping**

Lock the existing grant after targets, require owner/identity/public ID/state/revision to match, delete old typed children only inside the guarded transaction, insert the replacement set, and update state/revision/key/stamp once. Map PostgreSQL unique/check/concurrency conflicts to `revision_conflict`; map database, JSON, format, timeout and internal cancellation to `persistence_unavailable`; propagate caller cancellation.

- [ ] **Step 8: Write and run real concurrency tests**

Use explicit test barriers/transactions to prove same-ID initial races, same revoked revision races, session expiry/selection removal/target trash or update at every waiting phase, reversed mixed-target request order, and coexistence with record update, collection update, collection membership, trash and protection writers. Capture `pg_locks`/blocking evidence where the existing test utilities support it. Assert one winner, preserved loser-visible state, canonical target lock order and bounded completion with no deadlock.

```powershell
dotnet test tests/Infrastructure/ArturRios.Cerberus.Data.Tests/ArturRios.Cerberus.Data.Tests.csproj --filter "FullyQualifiedName~SoftwareGrantStore"
```

Expected: focused store and concurrency tests pass against real PostgreSQL.

- [ ] **Step 9: Run the complete Data family**

```powershell
dotnet test tests/Infrastructure/ArturRios.Cerberus.Data.Tests/ArturRios.Cerberus.Data.Tests.csproj
```

Expected: all Data tests pass with no deadlock or leaked transaction.

- [ ] **Step 10: Commit and task-review**

```powershell
git add src/Infrastructure/ArturRios.Cerberus.Data/Software tests/Infrastructure/ArturRios.Cerberus.Data.Tests/SoftwareGrantStoreTests.cs tests/Infrastructure/ArturRios.Cerberus.Data.Tests/SoftwareGrantStoreConcurrencyTests.cs tests/Infrastructure/ArturRios.Cerberus.Data.Tests/Support/DatabaseSeed.cs
git commit -m "feat: grant software target access"
```

Omit the seed-helper path from `git add` if unchanged. Record the fresh reviewer verdict and evidence before Task 5.

### Task 5: HTTP route, composition, documentation and exhaustive verification

**Files:**

- Create: `src/Presentation/ArturRios.Cerberus.WebApi/Controllers/SoftwareGrantsController.cs`
- Modify: `src/Presentation/ArturRios.Cerberus.WebApi/Startup.cs`
- Create: `tests/Presentation/ArturRios.Cerberus.WebApi.Tests/SoftwareGrantHttpTests.cs`
- Create: `docs/security/software-access-api.md`
- Modify: `docs/contracts/openapi.json`
- Modify: `docs/operations/foundation-status.md`
- Modify: `README.md`
- Modify: `CHANGELOG.md`

**Interfaces:**

- Consumes: `GrantSoftwareAccessCommand`, `SoftwareGrantMessages.StatusCodes`, `ISoftwareGrantStore`, `SoftwareGrantStore`, `IHeimdallClient`, the shared `VaultProofBody` strict-body filter and mediator DI conventions.
- Produces: externally reachable `POST /api/software-grants` and the documented generated OpenAPI contract.

- [ ] **Step 1: Write a failing happy-path HTTP test**

Use the real web host, PostgreSQL fixture, valid human token, vault-access handle, fake active Heimdall application response, native owner protection and two real recipient envelopes. Assert 201, `software_access_granted`, exact `{grantId,revision}`, no-store, no extra fields and exact persisted rows.

```csharp
using var response = await Client.SendAsync(Post("/api/software-grants", body, token, handle));
var output = await Success<GrantSoftwareAccessOutput>(response, 201);
Assert.Equal(grantId, output.GrantId);
Assert.Equal(1, output.Revision);
Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
```

- [ ] **Step 2: Run the focused HTTP test and confirm RED**

```powershell
dotnet test tests/Presentation/ArturRios.Cerberus.WebApi.Tests/ArturRios.Cerberus.WebApi.Tests.csproj --filter "FullyQualifiedName~SoftwareGrantHttpTests"
```

Expected: 404 because the route and registrations do not exist.

- [ ] **Step 3: Register services and implement the controller**

Register `ISoftwareGrantStore`/`SoftwareGrantStore`, the validator and mediator handler. Add a sealed `[ApiController]` at `[Route("api/software-grants")]` with one `[HttpPost] [VaultProofBody]` action. Reject any query or duplicate vault-access header, require the authenticated human `id` claim, set only trusted actor/header context, forward the caller token and map exact 201/400/401/403/404/409/413/503 responses.

```csharp
command.SetContext(actor, vaultAccess);
var result = await commands.ExecuteCommandAsync<GrantSoftwareAccessCommand,
    GrantSoftwareAccessOutput>(command, cancellationToken);
return result.ToActionResult(statusMap: SoftwareGrantMessages.StatusCodes);
```

- [ ] **Step 4: Add exhaustive transport and end-to-end denial tests**

Cover no/invalid human identity, no/duplicate/invalid vault header, query, empty/oversized body, missing/wrong/parameterized media type, invalid charset, compression, BOM, invalid UTF-8, duplicate/unknown/case-forged nested fields, noncanonical UUIDs and numeric forms. Cover Heimdall 404/deleted/malformed/unavailable, selected visibility, visible foreign redelegation, bad target envelope, active duplicate, successful revoked reactivation and database failure. Assert zero Heimdall calls for transport/auth/input rejection, zero store calls for non-Active application, no-store on every nonempty response and empty 413.

- [ ] **Step 5: Run focused and full Web tests**

```powershell
dotnet test tests/Presentation/ArturRios.Cerberus.WebApi.Tests/ArturRios.Cerberus.WebApi.Tests.csproj --filter "FullyQualifiedName~SoftwareGrantHttpTests"
dotnet test tests/Presentation/ArturRios.Cerberus.WebApi.Tests/ArturRios.Cerberus.WebApi.Tests.csproj
```

Expected: all Web API tests pass.

- [ ] **Step 6: Document the implemented boundary and regenerate OpenAPI**

Document request/response shapes, strict transport, status mapping, target visibility, Heimdall lookup, key binding, reactivation, non-atomic cross-service caveat and deferred release gates in `software-access-api.md`. Update foundation status and changelog. Mark UC-42 Done in the feature content and correct M-04 progress to 4/6 only after all implementation tests pass. Generate and verify OpenAPI:

```powershell
python scripts/openapi.py --write
python scripts/openapi.py
```

- [ ] **Step 7: Run repository verification from a clean build**

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
docker build --pull=false -t cerberus-api:uc-42 .
git diff --check
```

Expected: locked restore/audit/build pass; all six test projects pass; every production assembly has at least 90% line coverage and aggregate branch coverage is reported; specs/OpenAPI/native protocol checks and Docker build pass; whitespace is clean.

- [ ] **Step 8: Commit and task-review**

```powershell
git add src/Presentation/ArturRios.Cerberus.WebApi tests/Presentation/ArturRios.Cerberus.WebApi.Tests/SoftwareGrantHttpTests.cs docs/security/software-access-api.md docs/contracts/openapi.json docs/operations/foundation-status.md README.md CHANGELOG.md
git commit -m "feat: expose software access grants"
```

Record the fresh reviewer verdict and all final command evidence in the ledger.

## Use-case traceability

- Main flow: Tasks 1, 2, 4 and 5 prove active Heimdall application lookup,
  trusted owner scope, native target envelopes, atomic persistence and the minimal
  grant identifier/revision response.
- AF-01: Tasks 2 and 5 reject malformed transport, identifiers, revisions, public
  keys, target sets and envelopes before mutation.
- AF-02: Tasks 1, 4 and 5 return nonrevealing 404 for absent/deleted software
  identities and absent, hidden, unselected, trashed or terminal targets.
- AF-03: Tasks 2, 4 and 5 distinguish missing authentication/access (401) from a
  currently authenticated but unauthorized owner or validated foreign target
  (403).
- AF-04: Task 4 proves active duplicates, stale revoked reactivation, collisions,
  changed captured state and concurrent winners return 409 without overwriting the
  winner.
- AF-05: Tasks 1, 2, 4 and 5 prove Heimdall, persistence, serialization, timeout
  and post-write faults fail closed with 503 and no partial grant.

## Whole-branch review and delivery gates

- [ ] Dispatch one fresh whole-branch reviewer over base `d730c91c1b3577802fc12568959054900d14b71d` through feature HEAD with the five design priorities, plan, spec and actual test evidence. Resolve every Critical/Important finding and record accepted minor rulings.
- [ ] Present the implementation summary, full verification evidence and review verdict at Gate 2. Obtain explicit approval before moving issue #43 from In Progress to Testing, pushing or opening the PR.
- [ ] Push the exact reviewed feature HEAD, open exactly one PR targeting `develop` with `Closes #43`, attach the PR to this task and confirm the PR base/head did not change.
- [ ] Wait for the exact-head `branch-policy`, `test` and `docker` checks. Treat any new commit as invalidating prior approvals and exact-head evidence.
- [ ] Present Gate 3 evidence and obtain explicit approval before squash merge, project Done transition or branch deletion.
- [ ] After approval, squash merge only the reviewed tree, delete local/remote feature branches, close issue #43, set project status Done and verify the squash tree equals the reviewed feature tree.
- [ ] Save `.superpowers/checkpoints/uc-42-2026-10-12-complete/RESUME.md`, preserve the tracked ledger, remove only verified ignored UC-42 scratch/evidence files, and finish with clean `develop == origin/develop`.

