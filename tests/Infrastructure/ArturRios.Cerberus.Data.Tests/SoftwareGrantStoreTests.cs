using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using ArturRios.Cerberus.Data.Software;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.Domain.Software;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class SoftwareGrantStoreTests(PostgresFixture fixture)
{
    // These cases detect scope widening, missing epoch/signature checks, partial
    // writes, and accepting a stale or differently owned revoked aggregate.
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenMasterAndMixedOwnedTargets_WhenGranting_ThenSaveExactAggregateWithoutChangingVault(bool equalPublicIds)
    {
        using var s = await SoftwareGrantSetup.Create(fixture, equalPublicIds);
        var request = s.Request(); var before = await SoftwareGrantSetup.Vault(fixture, s.Owner.InternalId);
        var result = await new SoftwareGrantStore(fixture).GrantAsync(request, default);
        Assert.Null(result.Error); Assert.Equal(new SoftwareGrantDetails(request.Input.GrantId, 1), result.Data);
        Assert.Equal(before, await SoftwareGrantSetup.Vault(fixture, s.Owner.InternalId));
        await using var db = fixture.CreateContext();
        var grant = Assert.Single(await db.SoftwareGrants.Where(x => x.AccountId == s.Owner.InternalId).ToArrayAsync());
        Assert.Equal(request.Input.GrantId, grant.PublicId); Assert.Equal(request.Input.SoftwareIdentityId, grant.HeimdallSoftwarePublicId);
        Assert.Equal(SoftwareGrantState.Active, grant.State); Assert.Equal(1, grant.Revision); Assert.NotEqual(Guid.Empty, grant.ConcurrencyStamp);
        Assert.Equal(SoftwareGrantSetup.Bytes(request.Input.RecipientKey), grant.RecipientKey);
        var record = Assert.Single(await db.SoftwareGrantRecords.Where(x => x.GrantId == grant.Id).ToArrayAsync());
        var collection = Assert.Single(await db.SoftwareGrantCollections.Where(x => x.GrantId == grant.Id).ToArrayAsync());
        Assert.Equal(s.Owner.InternalId, record.AccountId); Assert.Equal(s.Owner.InternalId, collection.AccountId);
        Assert.Equal(s.Items.Record.Id, record.RecordId); Assert.Equal(s.Items.Collection.Id, collection.CollectionId);
        Assert.Equal(SoftwareGrantSetup.Bytes(request.Input.Targets.Single(x => x.Kind == "record").KeyEnvelope), record.RecipientKeyEnvelope);
        Assert.Equal(SoftwareGrantSetup.Bytes(request.Input.Targets.Single(x => x.Kind == "collection").KeyEnvelope), collection.RecipientKeyEnvelope);
    }

    [FunctionalTheory][InlineData("record")][InlineData("collection")]
    public async Task GivenOneTypedTarget_WhenGranting_ThenPersistOnlyThatKind(string kind)
    {
        using var s = await SoftwareGrantSetup.Create(fixture); var request = s.Request();
        request = request with { Input = request.Input with { Targets = request.Input.Targets.Where(x => x.Kind == kind).ToArray() } };
        await Outcome(s, request, null); await using var db = fixture.CreateContext();
        var grant = await db.SoftwareGrants.SingleAsync(x => x.PublicId == s.Grant);
        Assert.Equal(kind == "record" ? 1 : 0, await db.SoftwareGrantRecords.CountAsync(x => x.GrantId == grant.Id));
        Assert.Equal(kind == "collection" ? 1 : 0, await db.SoftwareGrantCollections.CountAsync(x => x.GrantId == grant.Id));
    }

    [FunctionalTheory]
    [InlineData("direct", true)][InlineData("folder", true)][InlineData("collection", true)][InlineData("PerProfile", true)]
    [InlineData("unselectedRecord", false)][InlineData("unselectedCollection", false)]
    public async Task GivenSelectedProfile_WhenGrantingMixedTargets_ThenRequireExactCurrentVisibility(string mode, bool success)
    {
        using var s = await SoftwareGrantSetup.Create(fixture);
        var profile = await s.Profile(fixture, mode == "PerProfile" ? "PerProfile" : "Master");
        await using (var db = fixture.CreateContext())
        {
            if (mode != "unselectedCollection") db.ProfileCollections.Add(new() { ProfileId = profile, CollectionId = s.Items.Collection.Id });
            if (mode is "direct" or "unselectedCollection" or "PerProfile") db.ProfileRecords.Add(new() { AccountId = s.Owner.InternalId, ProfileId = profile, RecordId = s.Items.Record.Id });
            if (mode == "folder")
            {
                await db.Records.Where(x => x.Id == s.Items.Record.Id).ExecuteUpdateAsync(x => x.SetProperty(r => r.FolderId, s.Items.Folder.Id));
                db.ProfileFolders.Add(new() { AccountId = s.Owner.InternalId, ProfileId = profile, FolderId = s.Items.Folder.Id });
            }
            if (mode == "collection") db.CollectionRecords.Add(new() { AccountId = s.Owner.InternalId, CollectionId = s.Items.Collection.Id, RecordId = s.Items.Record.Id });
            await db.SaveChangesAsync();
            await db.VaultAccessSessions.Where(x => x.HandleVerifier == s.Owner.Verifier).ExecuteUpdateAsync(x => x.SetProperty(v => v.ProfileId, profile));
        }
        await Outcome(s, s.Request(), success ? null : "not_found");
    }

    [FunctionalTheory]
    [InlineData("record", "missing", "not_found")][InlineData("collection", "missing", "not_found")]
    [InlineData("record", "trash", "not_found")][InlineData("collection", "trash", "not_found")]
    [InlineData("record", "terminal", "not_found")][InlineData("collection", "terminal", "not_found")]
    [InlineData("record", "envelope", "persistence_unavailable")][InlineData("collection", "envelope", "persistence_unavailable")]
    [InlineData("record", "revision", "persistence_unavailable")][InlineData("collection", "revision", "persistence_unavailable")]
    [InlineData("record", "sequence", "persistence_unavailable")][InlineData("collection", "sequence", "persistence_unavailable")]
    [InlineData("record", "purge", "persistence_unavailable")][InlineData("collection", "epoch", "persistence_unavailable")]
    [InlineData("record", "ancestorTrash", "not_found")][InlineData("record", "cycle", "persistence_unavailable")]
    public async Task GivenMissingHiddenOrCorruptTarget_WhenGranting_ThenRejectEntireMixedAggregate(string kind, string mode, string error)
    {
        using var s = await SoftwareGrantSetup.Create(fixture); var request = s.Request();
        var id = kind == "record" ? s.Items.Record.Id : s.Items.Collection.Id;
        if (mode == "missing") request = request with { Input = request.Input with { Targets = request.Input.Targets.Select(t => t.Kind == kind ? s.Target(kind, Guid.NewGuid()) : t).ToArray() } };
        else if (mode == "terminal") await Terminal(kind, kind == "record" ? s.Items.Record.PublicId : s.Items.Collection.PublicId);
        else if (mode is "ancestorTrash" or "cycle")
        {
            await Sql("UPDATE cerberus.record SET folder_id={0} WHERE id={1}", s.Items.Folder.Id, s.Items.Record.Id);
            await Sql(mode == "cycle" ? "UPDATE cerberus.folder SET parent_folder_id=id WHERE id={0}" : "UPDATE cerberus.folder SET deleted_at=statement_timestamp() WHERE id={0}", s.Items.Folder.Id);
        }
        else
        {
            var statement = "UPDATE cerberus." + kind + " SET " + (mode switch
            { "trash" => "deleted_at=statement_timestamp()", "envelope" => "envelope='bad'::bytea", "revision" => "revision=0", "sequence" => "server_sequence=0", "purge" => "purge_at=statement_timestamp()", _ => "key_epoch=2" }) + " WHERE id={0}";
            await Sql(statement, id);
        }
        await Outcome(s, request, error);
    }

    [FunctionalTheory]
    [InlineData("ownerMissing", "not_found")][InlineData("ownerInactive", "not_found")][InlineData("ownerTerminal", "not_found")]
    [InlineData("sessionMissing", "vault_access_denied")][InlineData("sessionExpired", "vault_access_denied")]
    [InlineData("sessionFuture", "vault_access_denied")][InlineData("sessionRevoked", "vault_access_denied")]
    [InlineData("sessionPolicy", "vault_access_denied")][InlineData("sessionGeneration", "vault_access_denied")]
    [InlineData("pinsMissing", "persistence_unavailable")][InlineData("pinsBad", "persistence_unavailable")][InlineData("pinsEpoch", "persistence_unavailable")]
    public async Task GivenInvalidActorSessionOrProtection_WhenGranting_ThenFailClosed(string mode, string error)
    {
        using var s = await SoftwareGrantSetup.Create(fixture); var request = s.Request();
        if (mode == "ownerMissing") request = request with { Actor = Guid.NewGuid() };
        else if (mode == "ownerTerminal") await Terminal("account", s.Owner.AccountId);
        else await Sql(mode switch
        {
            "ownerInactive" => "UPDATE cerberus.account SET state=1 WHERE id={0}",
            "sessionMissing" => "DELETE FROM cerberus.vault_access_session WHERE account_id={0}",
            "sessionExpired" => "UPDATE cerberus.vault_access_session SET expires_at=statement_timestamp() WHERE account_id={0}",
            "sessionFuture" => "UPDATE cerberus.vault_access_session SET issued_at=statement_timestamp()+interval '2 hours' WHERE account_id={0}",
            "sessionRevoked" => "UPDATE cerberus.vault_access_session SET revoked=true WHERE account_id={0}",
            "sessionPolicy" => "UPDATE cerberus.vault_access_session SET policy_revision=2 WHERE account_id={0}",
            "sessionGeneration" => "UPDATE cerberus.vault_access_session SET revocation_generation=2 WHERE account_id={0}",
            "pinsMissing" => "DELETE FROM cerberus.vault_protection WHERE account_id={0}",
            "pinsEpoch" => "UPDATE cerberus.vault_protection SET key_epoch=2 WHERE account_id={0}",
            _ => "UPDATE cerberus.vault_protection SET material='bad'::bytea WHERE account_id={0}"
        }, s.Owner.InternalId);
        await Outcome(s, request, error);
    }

    [FunctionalTheory]
    [InlineData("record", "visible", "vault_access_denied")][InlineData("collection", "visible", "vault_access_denied")]
    [InlineData("record", "hidden", "not_found")][InlineData("collection", "hidden", "not_found")]
    [InlineData("record", "revoked", "not_found")][InlineData("collection", "revoked", "not_found")]
    [InlineData("record", "unselected", "not_found")][InlineData("collection", "unselected", "not_found")]
    [InlineData("record", "badNative", "persistence_unavailable")][InlineData("collection", "badNative", "persistence_unavailable")]
    [InlineData("record", "badPins", "persistence_unavailable")][InlineData("collection", "badPins", "persistence_unavailable")]
    public async Task GivenForeignTarget_WhenGranting_ThenClassifyOnlyVisibleRelevantNativeEvidence(string kind, string mode, string error)
    {
        using var s = await SoftwareGrantSetup.Create(fixture); using var foreignPins = new ProtectionFixture();
        var foreign = await ProfileSetup.Create(fixture, foreignPins); var items = await AssociationSetup.Items(fixture, foreign);
        var native = await AssociationSetup.Grant(fixture, foreign, foreignPins, s.Owner, s.Protection, items.Collection);
        await Sql("INSERT INTO cerberus.collection_record(account_id,collection_id,record_id) VALUES({0},{1},{2})", foreign.InternalId, items.Collection.Id, items.Record.Id);
        if (mode is "hidden" or "revoked") await Sql("UPDATE cerberus.collection_grant SET state=1 WHERE id={0}", native.Id);
        if (mode == "badNative") await Sql("UPDATE cerberus.collection_grant SET recipient_key_envelope='bad'::bytea WHERE id={0}", native.Id);
        if (mode == "badPins") await Sql("UPDATE cerberus.vault_protection SET material='bad'::bytea WHERE account_id={0}", foreign.InternalId);
        if (mode == "unselected")
        {
            var profile = await s.Profile(fixture);
            await Sql("UPDATE cerberus.vault_access_session SET profile_id={0} WHERE handle_verifier={1}", profile, s.Owner.Verifier);
            // Corruption outside selection must not turn hidden 404 into a probe.
            await Sql("UPDATE cerberus.collection_grant SET recipient_key_envelope='bad'::bytea WHERE id={0}", native.Id);
        }
        var request = s.Request(); request = request with { Input = request.Input with { Targets = [s.Target(kind, kind == "record" ? items.Record.PublicId : items.Collection.PublicId)] } };
        await Outcome(s, request, error);
    }

    [FunctionalTheory]
    [InlineData("owner")][InlineData("kind")][InlineData("id")][InlineData("epoch")][InlineData("grant")]
    [InlineData("revision")][InlineData("identity")][InlineData("recipient")][InlineData("author")][InlineData("signature")]
    [InlineData("duplicate")][InlineData("empty")][InlineData("inputMax")][InlineData("actor")][InlineData("access")]
    public async Task GivenInvalidBindingOrInput_WhenGranting_ThenPersistNothing(string mode)
    {
        using var s = await SoftwareGrantSetup.Create(fixture); using var wrong = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = s.Request(); var target = s.Target("record", s.Items.Record.PublicId, epoch: mode == "epoch" ? 2 : 1,
            owner: mode == "owner" ? Guid.NewGuid() : null, signedKind: mode == "kind" ? "collection" : null,
            signedId: mode == "id" ? Guid.NewGuid() : null, author: mode == "author" ? wrong : null,
            recipient: mode == "recipient" ? ProtectionFixture.Public(wrong) : null);
        target = target with { KeyEnvelope = mode switch
        {
            "grant" => target.KeyEnvelope with { GrantId = Guid.NewGuid() }, "revision" => target.KeyEnvelope with { GrantRevision = 2 },
            "identity" => target.KeyEnvelope with { RecipientIdentityId = Guid.NewGuid() },
            "signature" => target.KeyEnvelope with { Signature = ProtectionFixture.Encode(new byte[64]) }, _ => target.KeyEnvelope
        } };
        request = request with { Input = request.Input with { Targets = [target, request.Input.Targets[1]] } };
        if (mode == "duplicate") request = request with { Input = request.Input with { Targets = [target, target] } };
        if (mode == "empty") request = request with { Input = request.Input with { Targets = [] } };
        if (mode == "inputMax") request = request with { Input = request.Input with { ExpectedRevision = ProtocolBinary.MaxInteger } };
        if (mode == "actor") request = request with { Actor = Guid.Empty };
        if (mode == "access") request = request with { AccessVerifier = "" };
        await Outcome(s, request, mode == "actor" ? "authentication_required" : mode == "access" ? "vault_access_required" : "validation_failed");
    }

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenExactRevokedAggregate_WhenReactivating_ThenReplaceWholeKeyAndTargetSetOnce(bool differentSet)
    {
        using var s = await SoftwareGrantSetup.Create(fixture); Assert.Null((await Store().GrantAsync(s.Request(), default)).Error);
        await Sql("UPDATE cerberus.software_grant SET state=1 WHERE public_id={0}", s.Grant);
        await using var db = fixture.CreateContext(); var old = await db.SoftwareGrants.AsNoTracking().SingleAsync(x => x.PublicId == s.Grant);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); var next = differentSet ? await AssociationSetup.Items(fixture, s.Owner) : s.Items;
        var request = s.Request(1, items: next, recipient: key); var before = await SoftwareGrantSetup.Vault(fixture, s.Owner.InternalId);
        var result = await Store().GrantAsync(request, default); Assert.Null(result.Error); Assert.Equal(new SoftwareGrantDetails(s.Grant, 2), result.Data);
        var saved = await db.SoftwareGrants.AsNoTracking().SingleAsync(x => x.PublicId == s.Grant);
        Assert.Equal(old.Id, saved.Id); Assert.Equal(old.PublicId, saved.PublicId); Assert.Equal(old.AccountId, saved.AccountId);
        Assert.Equal(old.HeimdallSoftwarePublicId, saved.HeimdallSoftwarePublicId); Assert.Equal(2, saved.Revision); Assert.Equal(SoftwareGrantState.Active, saved.State);
        Assert.NotEqual(old.ConcurrencyStamp, saved.ConcurrencyStamp); Assert.Equal(SoftwareGrantSetup.Bytes(request.Input.RecipientKey), saved.RecipientKey);
        var record = Assert.Single(await db.SoftwareGrantRecords.Where(x => x.GrantId == old.Id).ToArrayAsync());
        var collection = Assert.Single(await db.SoftwareGrantCollections.Where(x => x.GrantId == old.Id).ToArrayAsync());
        Assert.Equal(next.Record.Id, record.RecordId); Assert.Equal(next.Collection.Id, collection.CollectionId);
        Assert.Equal(SoftwareGrantSetup.Bytes(request.Input.Targets[0].KeyEnvelope), record.RecipientKeyEnvelope);
        Assert.Equal(SoftwareGrantSetup.Bytes(request.Input.Targets[1].KeyEnvelope), collection.RecipientKeyEnvelope);
        Assert.Equal(before, await SoftwareGrantSetup.Vault(fixture, s.Owner.InternalId));
    }

    [FunctionalTheory]
    [InlineData("active")][InlineData("activeForeign")][InlineData("stale")][InlineData("missingExpected")][InlineData("identity")][InlineData("owner")]
    [InlineData("collision")][InlineData("terminal")][InlineData("missing")][InlineData("exhausted")][InlineData("unsafe")][InlineData("invalidState")]
    public async Task GivenConflictingStoredGrant_WhenGranting_ThenPreserveCompleteAggregate(string mode)
    {
        using var s = await SoftwareGrantSetup.Create(fixture); using var other = await SoftwareGrantSetup.Create(fixture);
        if (mode is not ("terminal" or "missing"))
        {
            Assert.Null((await Store().GrantAsync(s.Request(), default)).Error);
            if (mode is not ("active" or "activeForeign")) await Sql("UPDATE cerberus.software_grant SET state=1 WHERE public_id={0}", s.Grant);
        }
        if (mode == "terminal") await Terminal("grant", s.Grant);
        if (mode is "exhausted" or "unsafe" or "invalidState") await Sql(mode == "invalidState"
            ? "UPDATE cerberus.software_grant SET state=7 WHERE public_id={0}"
            : "UPDATE cerberus.software_grant SET revision={1} WHERE public_id={0}", s.Grant, mode == "unsafe" ? ProtocolBinary.MaxInteger + 1 : ProtocolBinary.MaxInteger);
        var request = mode is "owner" or "collision" or "activeForeign" ? other.Request(mode == "owner" ? 1 : null, grant: s.Grant, identity: s.Identity)
            : s.Request(mode is "active" or "missingExpected" or "terminal" ? null : mode == "stale" ? 2 : 1, identity: mode == "identity" ? Guid.NewGuid() : null);
        var before = await SoftwareGrantSetup.Aggregate(fixture, s.Grant);
        await Outcome(mode is "owner" or "collision" or "activeForeign" ? other : s, request, "revision_conflict");
        Assert.Equal(before, await SoftwareGrantSetup.Aggregate(fixture, s.Grant));
    }

    [FunctionalTheory]
    [InlineData("grant", false)][InlineData("record", false)][InlineData("collection", false)][InlineData("post", false)]
    [InlineData("record", true)][InlineData("collection", true)][InlineData("post", true)]
    [InlineData("mismatchKey", true)][InlineData("mismatchChild", true)]
    public async Task GivenWriteOrPostVerificationFault_WhenGranting_ThenRollbackAndAllowExactRetry(string stage, bool reactivation)
    {
        using var s = await SoftwareGrantSetup.Create(fixture);
        if (reactivation) { Assert.Null((await Store().GrantAsync(s.Request(), default)).Error); await Sql("UPDATE cerberus.software_grant SET state=1 WHERE public_id={0}", s.Grant); }
        var next = await AssociationSetup.Items(fixture, s.Owner); var request = s.Request(reactivation ? 1 : null, items: next);
        await Outcome(s, request, "persistence_unavailable", new ProfileSetup.Factory(fixture, new Fault(stage)));
        Assert.Null((await Store().GrantAsync(request, default)).Error);
    }

    [FunctionalFact]
    public async Task GivenCallerCancellation_WhenGranting_ThenPropagateWithoutMutation()
    {
        using var s = await SoftwareGrantSetup.Create(fixture); using var ct = new CancellationTokenSource(); ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store().GrantAsync(s.Request(), ct.Token));
        await using var db = fixture.CreateContext(); Assert.False(await db.SoftwareGrants.AnyAsync(x => x.PublicId == s.Grant));
    }

    [FunctionalTheory]
    [InlineData("23505", "revision_conflict")][InlineData("23514", "revision_conflict")][InlineData("40001", "revision_conflict")]
    [InlineData("40P01", "revision_conflict")][InlineData("concurrency", "revision_conflict")]
    [InlineData("cancel", "persistence_unavailable")][InlineData("database", "persistence_unavailable")][InlineData("update", "persistence_unavailable")]
    [InlineData("json", "persistence_unavailable")][InlineData("format", "persistence_unavailable")][InlineData("wrapped", "persistence_unavailable")]
    public async Task GivenPersistenceFailure_WhenGranting_ThenReturnOnlySafeErrorAndRollback(string kind, string error)
    {
        using var s = await SoftwareGrantSetup.Create(fixture);
        Exception failure = kind switch
        {
            "23505" or "23514" or "40001" or "40P01" => new PostgresException("private database evidence", "ERROR", "ERROR", kind),
            "concurrency" => new DbUpdateConcurrencyException("private evidence"), "cancel" => new OperationCanceledException("private evidence"),
            "database" => new NpgsqlException("private evidence"), "update" => new DbUpdateException("private evidence"),
            "json" => new JsonException("private evidence"), "format" => new FormatException("private evidence"),
            _ => new InvalidOperationException("private evidence", new TimeoutException("private evidence"))
        };
        await Outcome(s, s.Request(), error, new ProfileSetup.Factory(fixture, new Failure(failure)));
    }

    private SoftwareGrantStore Store(IDbContextFactory<AppDbContext>? factory = null) => new(factory ?? fixture);
    private async Task Outcome(SoftwareGrantSetup s, SoftwareGrantRequest request, string? error, IDbContextFactory<AppDbContext>? factory = null)
    {
        var before = await SoftwareGrantSetup.Aggregate(fixture, request.Input.GrantId); var vault = await SoftwareGrantSetup.Vault(fixture, s.Owner.InternalId);
        var result = await Store(factory).GrantAsync(request, default); Assert.Equal(error, result.Error);
        Assert.Equal(vault, await SoftwareGrantSetup.Vault(fixture, s.Owner.InternalId));
        if (error is not null) { Assert.Null(result.Data); Assert.Equal(before, await SoftwareGrantSetup.Aggregate(fixture, request.Input.GrantId)); }
    }
    private async Task Sql(string statement, params object[] values) { await using var db = fixture.CreateContext(); await db.Database.ExecuteSqlRawAsync(statement, values); }
    private async Task Terminal(string kind, Guid id)
    { await using var db = fixture.CreateContext(); db.TerminalErasures.Add(new() { ResourceKind = kind, ResourceId = id, DeletedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync(); }
    private sealed class Fault(string stage) : DbCommandInterceptor
    {
        private bool written;
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand c, CommandExecutedEventData d, int result, CancellationToken ct = default)
        {
            if (c.CommandText.Contains("INSERT INTO cerberus.software_grant(") || c.CommandText.Contains("UPDATE cerberus.software_grant AS")) written = true;
            if (stage == "mismatchKey" && c.CommandText.Contains("INSERT INTO cerberus.software_grant_collection"))
            {
                await using var cmd = c.Connection!.CreateCommand(); cmd.Transaction = c.Transaction;
                cmd.CommandText = "UPDATE cerberus.software_grant SET recipient_key='bad'::bytea WHERE id=@id";
                cmd.Parameters.Add(new NpgsqlParameter("id", c.Parameters[1].Value)); await cmd.ExecuteNonQueryAsync(ct);
            }
            if (stage == "mismatchChild" && c.CommandText.Contains("INSERT INTO cerberus.software_grant_record"))
            {
                await using var cmd = c.Connection!.CreateCommand(); cmd.Transaction = c.Transaction;
                cmd.CommandText = "UPDATE cerberus.software_grant_record SET recipient_key_envelope='bad'::bytea WHERE grant_id=@id";
                cmd.Parameters.Add(new NpgsqlParameter("id", c.Parameters[1].Value)); await cmd.ExecuteNonQueryAsync(ct);
            }
            return result;
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (stage == "grant" && c.CommandText.Contains("INSERT INTO cerberus.software_grant(")
                || stage is "record" or "collection" && c.CommandText.Contains("INSERT INTO cerberus.software_grant_" + stage)) throw new TimeoutException("Injected aggregate write failure");
            return ValueTask.FromResult(result);
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        { if (stage == "post" && written) throw new TimeoutException("Injected post-write read failure"); return ValueTask.FromResult(result); }
    }
    private sealed class Failure(Exception exception) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<DbDataReader> result, CancellationToken ct = default) => throw exception;
    }
}

internal sealed class SoftwareGrantSetup : IDisposable
{
    internal ProtectionFixture Protection { get; } = new();
    internal ECDsa Author { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    internal ECDsa Recipient { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    internal ProfileSetup.State Owner { get; set; } = null!;
    internal AssociationSetup.ResourceSet Items { get; private set; } = null!;
    internal Guid Grant { get; } = Guid.NewGuid();
    internal Guid Identity { get; } = Guid.NewGuid();
    internal static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, ProtectionFixture.Json);
    internal static async Task<SoftwareGrantSetup> Create(PostgresFixture fixture, bool equalPublicIds = false)
    {
        var s = new SoftwareGrantSetup();
        s.Owner = await ProfileSetup.Create(fixture, s.Protection);
        s.Items = await AssociationSetup.Items(fixture, s.Owner, equalPublicIds ? Guid.NewGuid() : null);
        await using var db = fixture.CreateContext();
        await db.VaultProtections.Where(x => x.AccountId == s.Owner.InternalId).ExecuteUpdateAsync(x =>
            x.SetProperty(p => p.Material, Bytes(s.Protection.Material with { AuthorKey = ProtectionFixture.Public(s.Author) })));
        return s;
    }
    internal SoftwareGrantRequest Request(long? expected = null, Guid? grant = null, Guid? identity = null,
        AssociationSetup.ResourceSet? items = null, ECDsa? recipient = null)
    {
        var key = ProtectionFixture.Public(recipient ?? Recipient); var targets = items ?? Items;
        return new(Owner.Actor, Owner.Verifier, new(grant ?? Grant, identity ?? Identity, expected,
            new(key.Crv, key.Kty, key.X, key.Y),
            [Target("record", targets.Record.PublicId, expected, grant, identity, key),
             Target("collection", targets.Collection.PublicId, expected, grant, identity, key)]));
    }
    internal async Task<long> Profile(PostgresFixture fixture, string mode = "Master")
    {
        using var scoped = new ProtectionFixture();
        var input = ProfileSetup.Input(Owner, Protection, scoped, mode);
        var wrapper = Target("profile", input.ProfileId, grant: input.ProfileId, identity: Owner.Actor,
            recipient: Protection.Material.RecipientKey).KeyEnvelope;
        input = input with { KeyWrappers = input.KeyWrappers with { MasterKeyWrapper = wrapper } };
        Assert.Null((await new ArturRios.Cerberus.Data.Profiles.ProfileCreateStore(fixture).CreateAsync(new(Owner.Actor, Owner.Verifier, input), default)).Error);
        await using var db = fixture.CreateContext(); return await db.Profiles.Where(x => x.PublicId == input.ProfileId).Select(x => x.Id).SingleAsync();
    }
    internal SoftwareGrantTargetInput Target(string kind, Guid id, long? expected = null, Guid? grant = null, Guid? identity = null,
        PublicJwk? recipient = null, long epoch = 1, Guid? owner = null, string? signedKind = null, Guid? signedId = null, ECDsa? author = null)
    {
        using var ephemeral = ECDsa.Create(ECCurve.NamedCurves.nistP256); var point = ephemeral.ExportParameters(false).Q;
        var signer = author ?? Author;
        var envelope = new RecipientEnvelope("cerberus-recipient-wrap-v1", epoch, grant ?? Grant, (expected ?? 0) + 1,
            identity ?? Identity, (recipient ?? ProtectionFixture.Public(Recipient)).Fingerprint(), ProtectionFixture.Public(signer).Fingerprint(),
            ProtectionFixture.Encode([4, ..point.X!, ..point.Y!]), ProtectionFixture.Encode(RandomNumberGenerator.GetBytes(48)), "");
        envelope = envelope with { Signature = ProtectionFixture.Encode(signer.SignData(envelope.SigningBytes(owner ?? Owner.AccountId,
            signedKind ?? kind, signedId ?? id), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) };
        return new(kind, id, envelope);
    }
    internal static async Task<string> Vault(PostgresFixture fixture, long owner)
    {
        await using var db = fixture.CreateContext();
        return await db.Database.SqlQuery<string>($"""
            SELECT jsonb_build_object(
              'accounts',(SELECT jsonb_agg(to_jsonb(a) ORDER BY id) FROM cerberus.account a WHERE id={owner}),
              'sessions',(SELECT jsonb_agg(to_jsonb(s) ORDER BY id) FROM cerberus.vault_access_session s WHERE account_id={owner}),
              'protection',(SELECT jsonb_agg(to_jsonb(p) ORDER BY id) FROM cerberus.vault_protection p WHERE account_id={owner}),
              'profiles',(SELECT jsonb_agg(to_jsonb(p) ORDER BY id) FROM cerberus.profile p WHERE account_id={owner}),
              'records',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM cerberus.record r WHERE account_id={owner}),
              'collections',(SELECT jsonb_agg(to_jsonb(c) ORDER BY id) FROM cerberus.collection c WHERE account_id={owner}),
              'folders',(SELECT jsonb_agg(to_jsonb(f) ORDER BY id) FROM cerberus.folder f WHERE account_id={owner}),
              'pr',(SELECT jsonb_agg(to_jsonb(p) ORDER BY profile_id,record_id) FROM cerberus.profile_record p WHERE account_id={owner}),
              'pf',(SELECT jsonb_agg(to_jsonb(p) ORDER BY profile_id,folder_id) FROM cerberus.profile_folder p WHERE account_id={owner}),
              'pc',(SELECT jsonb_agg(to_jsonb(p) ORDER BY profile_id,collection_id) FROM cerberus.profile_collection p JOIN cerberus.profile pr ON pr.id=p.profile_id WHERE pr.account_id={owner}),
              'cr',(SELECT jsonb_agg(to_jsonb(c) ORDER BY collection_id,record_id) FROM cerberus.collection_record c WHERE account_id={owner}),
              'cf',(SELECT jsonb_agg(to_jsonb(c) ORDER BY collection_id,folder_id) FROM cerberus.collection_folder c WHERE account_id={owner}),
              'sequence',(SELECT last_value FROM cerberus.server_sequence))::text AS "Value"
            """).SingleAsync();
    }
    internal static async Task<string> Aggregate(PostgresFixture fixture, Guid grant)
    {
        await using var db = fixture.CreateContext();
        return await db.Database.SqlQuery<string>($"""
            SELECT jsonb_build_object('grant',(SELECT to_jsonb(g) FROM cerberus.software_grant g WHERE public_id={grant}),
              'records',(SELECT jsonb_agg(to_jsonb(r) ORDER BY record_id) FROM cerberus.software_grant_record r JOIN cerberus.software_grant g ON g.id=r.grant_id WHERE g.public_id={grant}),
              'collections',(SELECT jsonb_agg(to_jsonb(c) ORDER BY collection_id) FROM cerberus.software_grant_collection c JOIN cerberus.software_grant g ON g.id=c.grant_id WHERE g.public_id={grant}))::text AS "Value"
            """).SingleAsync();
    }
    public void Dispose() { Protection.Dispose(); Author.Dispose(); Recipient.Dispose(); }
}
