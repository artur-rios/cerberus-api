# UC-37 Revoke Collection Access Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an owner-only collection-grant revocation endpoint that atomically revokes one active grant, erases its wrapper and invalidates the recipient's current online access contexts.

**Architecture:** Introduce a strict one-field command contract, a purpose-built PostgreSQL store and a DELETE action. The store reuses the established target-first sharing authority model, locks owner and recipient accounts together in canonical order, then performs one guarded grant-CAS plus recipient-generation update and verifies the exact committed state.

**Tech Stack:** .NET 10, ASP.NET Core, FluentValidation, EF Core, Npgsql/PostgreSQL 18, xUnit, native Java/Python protocol harness, generated OpenAPI.

**Spec:** `docs/superpowers/specs/2026-10-12-uc-37-revoke-collection-access-design.md`

## Global Constraints

- Branch from clean `develop` as `feature/uc-37-revoke-collection-access`; issue #38 and its project item are the only delivery targets.
- Public route is exactly `DELETE /api/collections/{id}/grants/{grantId}` with canonical lowercase dashed nonzero UUID paths, no query and one `X-Cerberus-Vault-Access` header.
- Public body contains exactly required `expectedRevision`, positive and strictly below `9007199254740991`.
- Success is `200 collection_access_revoked` with exactly `collectionId`, `grantId`, `revision`.
- The owner path may quarantine malformed old wrapper bytes; it must not require those bytes to validate before revocation.
- Owner and recipient accounts are locked together by public UUID before session, profile, collection or grant locks.
- Grant revocation, revision advance, wrapper erasure and recipient-generation increment form one atomic database authorization statement.
- Preserve collection/member/profile/session state, owner and recipient policy/protection, unrelated grants and account revisions.
- No schema, migration, package or cryptographic algorithm change.
- Do not claim destruction of copied plaintext/keys, client-side rotation or UC-48 durable offline delivery.
- Tests run in Release with locked restore; every production assembly remains at least 90% line coverage.

## Review Focus

1. **Malformed stored wrapper on the owner path:** revocation succeeds, erases it and never upgrades malformed bytes into authority; the Task 2 owner-corruption test pins this behavior.
2. **Foreign visibility classification:** only valid current native evidence produces 403; hidden evidence remains 404 and corrupt relevant evidence is 503; Task 2 and Task 3 foreign tests pin the boundary.
3. **Concurrent revoke/change/regrant:** one locked winner persists and every loser returns the correct conflict/hidden result without a partial generation change; Task 2 PostgreSQL races pin this behavior.
4. **Partial atomic-write suppression:** a trigger suppressing either the grant side or account side produces 503 and rolls back both; Task 2 trigger-fault tests pin this behavior.
5. **Online versus cryptographic revocation:** stale handles fail immediately, fresh unlock has no visibility, retained profile links stay inert and no response/document claims destruction of copied keys; Task 3 HTTP/document tests pin this behavior.

---

### Task 1: Domain and application revocation contract

**Files:**

- Create: `src/Domain/ArturRios.Cerberus.Domain/Collections/CollectionGrantRevocationContracts.cs`
- Create: `src/Application/ArturRios.Cerberus.Command/Collections/RevokeCollectionAccessCommand.cs`
- Create: `src/Application/ArturRios.Cerberus.Command/Collections/RevokeCollectionAccessValidator.cs`
- Create: `src/Application/ArturRios.Cerberus.Command/Collections/RevokeCollectionAccessHandler.cs`
- Create: `src/Application/ArturRios.Cerberus.Command/Collections/RevokeCollectionAccessOutput.cs`
- Create: `src/Application/ArturRios.Cerberus.Command/Collections/CollectionGrantRevocationMessages.cs`
- Create: `tests/Application/ArturRios.Cerberus.Command.Tests/RevokeCollectionAccessContractTests.cs`
- Create: `tests/Application/ArturRios.Cerberus.Command.Tests/RevokeCollectionAccessHandlerTests.cs`
- Create: `.superpowers/sdd/2026-10-12-uc-37-revoke-collection-access/ledger.md`

**Interfaces:**

- Consumes: `OpaqueAccessHandle.TryHash`, `ProtocolBinary.MaxInteger`, `VaultResult<T>`, `DataOutput<T>`, FluentValidation and mediator command interfaces.
- Produces:

```csharp
public sealed record CollectionGrantRevocationInput(long ExpectedRevision)
{
    public bool IsValid(Guid grantId) => grantId != Guid.Empty
        && ExpectedRevision is > 0 and < ProtocolBinary.MaxInteger;
}

public sealed record CollectionGrantRevocationRequest(
    Guid Actor,
    string AccessVerifier,
    Guid CollectionId,
    Guid GrantId,
    CollectionGrantRevocationInput Input);

public sealed record CollectionGrantRevocationDetails(
    Guid CollectionId,
    Guid GrantId,
    long Revision);

public interface ICollectionGrantRevocationStore
{
    Task<VaultResult<CollectionGrantRevocationDetails>> RevokeAsync(
        CollectionGrantRevocationRequest request,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 1: Write an executable missing-contract RED probe**

Create a temporary ignored runner that restores the existing Command test project, then compiles and executes reflection assertions for the absent input and command types. The two assertions must fail because the types are absent; do not count unrelated compiler failures.

Run:

```text
wsl.exe -d Ubuntu -- bash /mnt/d/Repositories/cerberus-api/.superpowers/sdd/2026-10-12-uc-37-revoke-collection-access/task-1-linux.sh cerberus-uc37-task1-contract-red focused
```

Expected: two behavior failures, zero passes, nonzero exit.

- [ ] **Step 2: Write the typed contract and handler tests before production code**

Pin strict JSON presence and unmapped-member handling, positive/safe revision, trusted actor/access/path context, authentication precedence, invalid-handle rejection, cancellation propagation, safe store error allowlisting, contradictory/null store-result rejection and exact output validation.

Use this success assertion shape:

```csharp
var result = await handler.HandleAsync(command);
Assert.Empty(result.Errors);
Assert.Equal(new[] { "collection_access_revoked" }, result.Messages);
Assert.Equal(collectionId, result.Data!.CollectionId);
Assert.Equal(grantId, result.Data.GrantId);
Assert.Equal(command.ExpectedRevision + 1, result.Data.Revision);
Assert.Equal(
    new[] { "CollectionId", "GrantId", "Revision" },
    typeof(RevokeCollectionAccessOutput).GetProperties().Select(x => x.Name).Order().ToArray());
```

Run the focused filter and retain the expected `CS0246` missing-type RED separately from the executable probe.

- [ ] **Step 3: Implement the domain and command types**

Implement the interfaces above. The command has one public required property and trusted internal context:

```csharp
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class RevokeCollectionAccessCommand : BaseCommand
{
    [JsonRequired] public long ExpectedRevision { get; set; }
    internal Guid Actor { get; private set; }
    internal Guid CollectionId { get; private set; }
    internal Guid GrantId { get; private set; }
    internal string? VaultAccess { get; private set; }

    public void SetContext(Guid actor, string? access, Guid collectionId, Guid grantId)
    {
        Actor = actor;
        VaultAccess = access;
        CollectionId = collectionId;
        GrantId = grantId;
    }

    public CollectionGrantRevocationInput ToInput() => new(ExpectedRevision);
}
```

The validator requires nonzero collection ID and `ToInput().IsValid(GrantId)`.

- [ ] **Step 4: Implement handler fail-closed behavior and status map**

The handler must check cancellation, identity, header, handle hashing and validation in that order; call `RevokeAsync`; allowlist `validation_failed`, `vault_access_denied`, `not_found`, `revision_conflict` and `persistence_unavailable`; and map every other store error or contradictory result to `persistence_unavailable`.

Validate that returned IDs equal trusted path IDs and returned revision equals expected plus one within the safe range. Return only `RevokeCollectionAccessOutput` and `collection_access_revoked`.

Define the status map:

```csharp
public static readonly IReadOnlyDictionary<string, int> StatusCodes = new Dictionary<string, int>
{
    ["collection_access_revoked"] = 200,
    ["validation_failed"] = 400,
    ["authentication_required"] = 401,
    ["vault_access_required"] = 401,
    ["vault_access_denied"] = 403,
    ["not_found"] = 404,
    ["revision_conflict"] = 409,
    ["persistence_unavailable"] = 503
};
```

- [ ] **Step 5: Run focused and complete Command GREEN**

Run the focused `FullyQualifiedName~RevokeCollectionAccess` filter, then the entire unfiltered Command test project in Release with warnings as errors and locked restore. Record exact counts, commands, runner/source hashes and limitations in the ledger.

- [ ] **Step 6: Commit Task 1**

```text
git add src/Domain/ArturRios.Cerberus.Domain/Collections/CollectionGrantRevocationContracts.cs src/Application/ArturRios.Cerberus.Command/Collections/RevokeCollectionAccessCommand.cs src/Application/ArturRios.Cerberus.Command/Collections/RevokeCollectionAccessValidator.cs src/Application/ArturRios.Cerberus.Command/Collections/RevokeCollectionAccessHandler.cs src/Application/ArturRios.Cerberus.Command/Collections/RevokeCollectionAccessOutput.cs src/Application/ArturRios.Cerberus.Command/Collections/CollectionGrantRevocationMessages.cs tests/Application/ArturRios.Cerberus.Command.Tests/RevokeCollectionAccessContractTests.cs tests/Application/ArturRios.Cerberus.Command.Tests/RevokeCollectionAccessHandlerTests.cs
git add -f .superpowers/sdd/2026-10-12-uc-37-revoke-collection-access/ledger.md
git commit -m "feat: add collection access revocation contract"
```

Dispatch a fresh reviewer for the exact Task 1 range. Resolve every Critical or Important finding with a fresh correction implementer and fresh re-review before Task 2.

---

### Task 2: Atomic PostgreSQL revocation writer

**Files:**

- Create: `src/Infrastructure/ArturRios.Cerberus.Data/Collections/CollectionGrantRevocationStore.cs`
- Create: `tests/Infrastructure/ArturRios.Cerberus.Data.Tests/CollectionGrantRevocationStoreTests.cs`
- Create: `tests/Infrastructure/ArturRios.Cerberus.Data.Tests/CollectionGrantRevocationStoreConcurrencyTests.cs`
- Modify: `.superpowers/sdd/2026-10-12-uc-37-revoke-collection-access/ledger.md`

**Interfaces:**

- Consumes: `ICollectionGrantRevocationStore`, UC-35/36 target-first authority SQL, `CollectionGrantState`, `CollectionGrantBinding`, account/session/profile/collection/grant tables.
- Produces: `CollectionGrantRevocationStore.RevokeAsync(CollectionGrantRevocationRequest, CancellationToken)` returning exact details or stable safe errors.

- [ ] **Step 1: Write real-PostgreSQL transition RED tests**

Create active read-only and read-write grants, capture exact database snapshots, call the absent store, and assert intended results:

```csharp
Assert.Null(result.Error);
Assert.Equal(expectedRevision + 1, result.Data!.Revision);
Assert.Equal(CollectionGrantState.Revoked, saved.State);
Assert.Empty(saved.RecipientKeyEnvelope);
Assert.Equal(beforeRecipientGeneration + 1, savedRecipient.RevocationGeneration);
Assert.Equal(beforeOwner, await OwnerSnapshot(ownerId));
Assert.Equal(beforeCollection, await CollectionSnapshot(collectionId));
```

The executable RED must fail because the production store type is absent, not because the fixture or database failed.

- [ ] **Step 2: Implement target-first classification and canonical locks**

Classify current owner/foreign visibility before requested-recipient locks. On the owner path, capture the exact active path grant and recipient. Lock both account rows in one canonical statement:

```sql
SELECT FROM cerberus.account
WHERE public_id = ANY(@account_ids)
ORDER BY public_id
FOR UPDATE
```

Then lock owner session `FOR UPDATE`, selected owner profile `FOR NO KEY UPDATE`, collection `FOR UPDATE` and exact grant `FOR UPDATE`, rechecking captured authority after every wait.

- [ ] **Step 3: Implement owner-reduction and foreign-native rules**

For a foreign target, validate the actor's relevant active grant with current collection identity/epoch, grant ID/revision, Heimdall identity and owner/actor pins before returning 403. Return 404 for hidden foreign targets and 503 for corrupt relevant evidence.

For an owner target, validate exact active grant relationship, safe grant revision and safe recipient account generation. Accept any old envelope bytes, including malformed bytes, because success erases them.

- [ ] **Step 4: Implement the atomic authorization statement**

Use a guarded CTE whose second update depends on the first:

```sql
, revoked AS (
  UPDATE cerberus.collection_grant AS g
  SET state = @revoked,
      revision = @revision,
      recipient_key_envelope = ''::bytea,
      concurrency_stamp = @stamp
  FROM own_target c
  CROSS JOIN actor a
  CROSS JOIN recipient r
  CROSS JOIN session s
  CROSS JOIN captured frozen
  WHERE g.id = @pair
    AND g.collection_id = c.id
    AND c.account_id = a.id
    AND g.recipient_account_id = r.id
    AND g.public_id = @grant
    AND g.state = @active
    AND g.revision = @previous
    AND r.revocation_generation = @generation
    AND @generation > 0
    AND @generation < @max
    AND frozen.stable = @stable
    AND frozen.grants = @grants
  RETURNING g.recipient_account_id
)
UPDATE cerberus.account AS r
SET revocation_generation = r.revocation_generation + 1
FROM revoked g
WHERE r.id = g.recipient_account_id
  AND r.revocation_generation = @generation
```

Treat a suppressed account side after the grant CAS as `persistence_unavailable`; transaction disposal must roll back the grant side.

- [ ] **Step 5: Implement exact post-write verification**

Reload the exact grant and recipient account. Require revoked state, expected plus one revision, empty wrapper, fresh stamp, exact recipient generation plus one and no active grant for the same pair. Compare captured stable JSON/snapshots to prove collection, members, profile links, sessions, protection, policy, account revision and unrelated grants did not change before committing.

- [ ] **Step 6: Grow the focused matrix red/green**

Add owner wide/selected success, both access values, malformed old wrapper success, missing/revoked/terminal/cross-collection grant 404, recipient and collection lifecycle cases, stale/max revision 409, foreign hidden/valid/corrupt native cases, unsafe counters 503, cancellation, database outage, and a trigger fault on each atomic side with full rollback.

- [ ] **Step 7: Add actual concurrency and query-bound proof**

Use `pg_blocking_pids` to prove the expected blocker before releasing held transactions. Cover same-grant double revoke, reciprocal revokes, UC-36 permission change, UC-35 regrant, recipient session issuance, lifecycle, protection and profile-association writers in both acquisition orders. Assert no deadlock, one winner, one generation increment and one grant mutation. Use `EXPLAIN (ANALYZE, FORMAT JSON)` with unrelated inventory to require one target collection, one path grant and one recipient row.

- [ ] **Step 8: Run focused and complete Data GREEN**

Run `FullyQualifiedName~CollectionGrantRevocationStoreTests` and the complete unfiltered Data project against PostgreSQL 18.6. Record counts, commands and hashes in the ledger; check Task 2 implementation only.

- [ ] **Step 9: Commit Task 2**

```text
git add src/Infrastructure/ArturRios.Cerberus.Data/Collections/CollectionGrantRevocationStore.cs tests/Infrastructure/ArturRios.Cerberus.Data.Tests/CollectionGrantRevocationStoreTests.cs tests/Infrastructure/ArturRios.Cerberus.Data.Tests/CollectionGrantRevocationStoreConcurrencyTests.cs
git add -f .superpowers/sdd/2026-10-12-uc-37-revoke-collection-access/ledger.md
git commit -m "feat: add atomic collection access revocation"
```

Dispatch a fresh Task 2 reviewer with the exact commit range and the five review-focus cases. Independently rerun the focused PostgreSQL suite and resolve every blocking finding before Task 3.

---

### Task 3: HTTP route, integration, documentation and exhaustive verification

**Files:**

- Modify: `src/Presentation/ArturRios.Cerberus.WebApi/Controllers/CollectionsController.cs`
- Modify: `src/Presentation/ArturRios.Cerberus.WebApi/Startup.cs`
- Create: `tests/Presentation/ArturRios.Cerberus.WebApi.Tests/CollectionGrantRevocationHttpTests.cs`
- Modify: `docs/contracts/openapi.json`
- Modify: `docs/security/collection-api.md`
- Modify: `docs/operations/foundation-status.md`
- Modify: `CHANGELOG.md`
- Modify: `README.md`
- Modify: `.superpowers/sdd/2026-10-12-uc-37-revoke-collection-access/ledger.md`

**Interfaces:**

- Consumes: Task 1 command/handler/messages and Task 2 store.
- Produces: strict DELETE endpoint, DI registrations, generated contract, operational documentation and full verification evidence.

- [ ] **Step 1: Write the missing-route HTTP RED suite**

Register no production changes yet. Send strict DELETE requests with real owner/recipient native fixtures and assert the absent route fails. Cover success shape, canonical paths, no query, repeated/missing header, exact JSON, media type, UTF-8/BOM/compression/body limits, all status mappings and no-store headers. Keep the shared oversized-body 413 as an existing-pass disclosure rather than counting it as a new RED failure.

- [ ] **Step 2: Register DI and add the DELETE action**

Add scoped validator, handler and store registrations. Add this action shape:

```csharp
[HttpDelete("{id}/grants/{grantId}")]
[VaultProofBody]
[ProducesResponseType(typeof(DataOutput<RevokeCollectionAccessOutput>), StatusCodes.Status200OK)]
public async Task<ActionResult<DataOutput<RevokeCollectionAccessOutput?>>> RevokeAccess(
    [FromRoute] string id,
    [FromRoute] string grantId,
    [FromBody] RevokeCollectionAccessCommand command,
    [FromHeader(Name = "X-Cerberus-Vault-Access")] string? vaultAccess,
    CancellationToken cancellationToken)
```

Reject query parameters, repeated access headers and any noncanonical/zero path UUID before identity resolution. Set trusted context and execute through the command mediator with `CollectionGrantRevocationMessages.StatusCodes`.

- [ ] **Step 3: Prove online revocation behavior with real native material**

For both read-only and read-write grants and both wide and selected recipient handles:

1. prove pre-revocation list/read visibility and permitted/denied writes;
2. revoke as wide or exact-selected owner;
3. prove the old recipient handle is stale;
4. fresh-unlock the recipient and prove collection/list/read absence;
5. prove direct and inherited record/folder create/update/move paths cannot use the revoked collection;
6. prove unrelated recipient-owned data remains readable;
7. prove retained selected `ProfileCollection` links are inert;
8. regrant with the revoked revision and a new native envelope, then prove visibility returns only through the intended profile scope.

- [ ] **Step 4: Complete HTTP error, secrecy and rollback coverage**

Assert exact three success fields and absence of state/access/identity/profile/generation/envelope/pin/key fields. Cover owner and recipient lifecycle, valid visible foreign denial, hidden target, malformed old owner-path envelope success, stale/max revision, grant/account trigger faults, database outage, no-store, empty 413 and unchanged snapshots on every failure.

- [ ] **Step 5: Generate OpenAPI and update documentation**

Run the repository's OpenAPI generator and require only the intended DELETE operation and revocation schemas. Document online generation invalidation, wrapper erasure, inert profile links, regrant and the explicit copied-key/client-rotation/UC-48 limitations. Mark issue #38 Done only in feature content that ships with the implementation. Independently count tracker rows and set the README headline to exactly 38 implemented and 17 remaining.

- [ ] **Step 6: Run focused HTTP GREEN**

Run `FullyQualifiedName~CollectionGrantRevocationHttpTests` in Release against real PostgreSQL. Record RED/GREEN counts and exact source/evidence hashes.

- [ ] **Step 7: Run exhaustive verification**

Run locked restore/build, live NuGet audit, all six unfiltered Release test families, merged coverage, per-assembly 90% line floors, aggregate branch coverage, repository helpers, spec validation, generated OpenAPI drift/shape and whitespace checks. Run native Java, Python, helpers, repository helpers, 322-case four-direction corpus and live OSV audit. Preserve truthful failed harness attempts separately and hash every tested source/evidence file.

- [ ] **Step 8: Commit Task 3**

```text
git add src/Presentation/ArturRios.Cerberus.WebApi/Controllers/CollectionsController.cs src/Presentation/ArturRios.Cerberus.WebApi/Startup.cs tests/Presentation/ArturRios.Cerberus.WebApi.Tests/CollectionGrantRevocationHttpTests.cs docs/contracts/openapi.json docs/security/collection-api.md docs/operations/foundation-status.md CHANGELOG.md README.md
git add -f .superpowers/sdd/2026-10-12-uc-37-revoke-collection-access/ledger.md
git commit -m "feat: expose collection access revocation"
```

Dispatch a fresh Task 3 reviewer. Resolve every Critical/Important finding and record accepted review evidence in the ledger.

---

### Task 4: Whole-branch review and exact-head delivery

**Files:**

- Modify only for review corrections: files identified by a reviewer.
- Modify for review records: `.superpowers/sdd/2026-10-12-uc-37-revoke-collection-access/ledger.md`
- Create after merge: `.superpowers/checkpoints/uc-37-2026-10-12-complete/RESUME.md`

**Interfaces:**

- Consumes: reviewed Tasks 1 through 3 and retained verification evidence.
- Produces: one reviewed PR closing #38, exact-head CI evidence, normal squash merge, Done project state and clean `develop`.

- [ ] **Step 1: Run a fresh whole-branch review**

Review exact `develop-base..HEAD` with all five review-focus cases. Independently rerun focused Command, PostgreSQL Data and HTTP suites and reconcile full-suite, coverage, native, audit, manifest and OpenAPI evidence. Any Critical or Important finding blocks delivery.

- [ ] **Step 2: Record accepted review and verify the delivery head**

Commit the whole-branch review record, require a clean tree, pass `git diff --check`, and capture exact head/base/tree IDs. Move the project item to Testing only after implementation and review are complete.

- [ ] **Step 3: Push and create exactly one PR**

Push `feature/uc-37-revoke-collection-access`, create one PR targeting `develop` with `Closes #38`, attach the PR artifact to the task, and verify GitHub reports the reviewed head SHA and expected base SHA.

- [ ] **Step 4: Require exact-head CI and normal merge**

Wait for branch policy, Docker and test/coverage checks on the exact reviewed head. Re-read PR state and require `OPEN`, `MERGEABLE`, `CLEAN` and unchanged head/base. Merge only with normal exact-head squash semantics:

```text
gh pr merge <number> --squash --delete-branch --match-head-commit <reviewed-head>
```

Do not use admin, force, release or bypass options.

- [ ] **Step 5: Audit closure and clean up**

Require merged PR, closed issue #38, project item Done, absent remote/local feature branches, local `develop == origin/develop`, and exact equality between reviewed feature tree and squash-merge tree. Save the completion checkpoint, remove only verified ignored UC-37 scratch/evidence files while preserving the tracked ledger, and leave a clean tree ready for the next authorized use case.
