using System.Data.Common;
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
public class SoftwareGrantRevocationStoreTests(PostgresFixture fixture)
{
    // Break: retaining recipient material, changing stable grant identity, or mutating the owner's vault.
    [FunctionalTheory][InlineData("mixed")][InlineData("record")][InlineData("collection")]
    public async Task GivenOwnedTargets_WhenRevoking_ThenEraseExactAggregateAndPreserveVault(string kind)
    {
        using var s = await SoftwareGrantSetup.Create(fixture);
        var create = s.Request();
        if (kind != "mixed") create = create with { Input = create.Input with { Targets = create.Input.Targets.Where(x => x.Kind == kind).ToArray() } };
        Assert.Null((await new SoftwareGrantStore(fixture).GrantAsync(create, default)).Error);
        var siblingId = Guid.NewGuid();
        Assert.Null((await new SoftwareGrantStore(fixture).GrantAsync(s.Request(grant: siblingId, identity: Guid.NewGuid()), default)).Error);
        var profile = await s.Profile(fixture);
        using var nativeRecipientKey = new ProtectionFixture();
        var nativeRecipient = await ProfileSetup.Create(fixture, nativeRecipientKey);
        var native = await AssociationSetup.Grant(fixture, s.Owner, s.Protection, nativeRecipient, nativeRecipientKey, s.Items.Collection);
        await using var db = fixture.CreateContext();
        db.CollectionRecords.Add(new() { AccountId = s.Owner.InternalId, CollectionId = s.Items.Collection.Id, RecordId = s.Items.Record.Id });
        db.ProfileRecords.Add(new() { AccountId = s.Owner.InternalId, ProfileId = profile, RecordId = s.Items.Record.Id });
        await db.SaveChangesAsync();
        var old = await db.SoftwareGrants.AsNoTracking().SingleAsync(x => x.PublicId == s.Grant);
        var vault = await SoftwareGrantSetup.Vault(fixture, s.Owner.InternalId);
        var sibling = await SoftwareGrantSetup.Aggregate(fixture, siblingId);
        var nativeBefore = JsonSerializer.Serialize(await db.CollectionGrants.AsNoTracking().SingleAsync(x => x.Id == native.Id));
        var result = await Store().RevokeAsync(Request(s), default);
        Assert.Null(result.Error); Assert.Equal(new SoftwareGrantRevocationDetails(s.Grant, 2), result.Data);
        Assert.Equal(vault, await SoftwareGrantSetup.Vault(fixture, s.Owner.InternalId));
        Assert.Equal(sibling, await SoftwareGrantSetup.Aggregate(fixture, siblingId));
        Assert.Equal(nativeBefore, JsonSerializer.Serialize(await db.CollectionGrants.AsNoTracking().SingleAsync(x => x.Id == native.Id)));
        var saved = await db.SoftwareGrants.AsNoTracking().SingleAsync(x => x.PublicId == s.Grant);
        Assert.Equal(old.Id, saved.Id); Assert.Equal(old.AccountId, saved.AccountId); Assert.Equal(old.PublicId, saved.PublicId);
        Assert.Equal(old.HeimdallSoftwarePublicId, saved.HeimdallSoftwarePublicId);
        Assert.Equal(SoftwareGrantState.Revoked, saved.State); Assert.Equal(2, saved.Revision);
        Assert.NotEqual(old.ConcurrencyStamp, saved.ConcurrencyStamp); Assert.NotEqual(Guid.Empty, saved.ConcurrencyStamp);
        Assert.Empty(saved.RecipientKey);
        Assert.Empty(await db.SoftwareGrantRecords.Where(x => x.GrantId == saved.Id).ToArrayAsync());
        Assert.Empty(await db.SoftwareGrantCollections.Where(x => x.GrantId == saved.Id).ToArrayAsync());
    }

    // Break: disclosing foreign/hidden grants, authorizing an invalid session, or accepting stale revisions.
    [FunctionalTheory]
    [InlineData("missing", "not_found")][InlineData("foreign", "not_found")][InlineData("revoked", "not_found")]
    [InlineData("terminal", "not_found")][InlineData("inactive", "not_found")][InlineData("actorTerminal", "not_found")]
    [InlineData("sessionMissing", "vault_access_denied")][InlineData("sessionRevoked", "vault_access_denied")]
    [InlineData("policy", "vault_access_denied")][InlineData("generation", "vault_access_denied")]
    [InlineData("expired", "vault_access_denied")][InlineData("future", "vault_access_denied")]
    [InlineData("profileDeleted", "vault_access_denied")][InlineData("profileTerminal", "vault_access_denied")]
    [InlineData("stale", "revision_conflict")][InlineData("exhausted", "revision_conflict")]
    [InlineData("zero", "persistence_unavailable")][InlineData("unsafe", "persistence_unavailable")]
    public async Task GivenUnavailableGrantState_WhenRevoking_ThenFailWithoutMutation(string mode, string error)
    {
        using var s = await Setup(); var request = Request(s);
        switch (mode)
        {
            case "missing": request = request with { GrantId = Guid.NewGuid() }; break;
            case "foreign":
                using (var other = await SoftwareGrantSetup.Create(fixture)) request = request with { Actor = other.Owner.Actor, AccessVerifier = other.Owner.Verifier };
                break;
            case "revoked": await Sql("UPDATE cerberus.software_grant SET state=1 WHERE public_id={0}", s.Grant); break;
            case "terminal": await Terminal("grant", s.Grant); break;
            case "actorTerminal": await Terminal("account", s.Owner.AccountId); break;
            case "inactive": await Sql("UPDATE cerberus.account SET state=1 WHERE id={0}", s.Owner.InternalId); break;
            case "sessionMissing": request = request with { AccessVerifier = "missing" }; break;
            case "sessionRevoked": await Sql("UPDATE cerberus.vault_access_session SET revoked=true WHERE handle_verifier={0}", s.Owner.Verifier); break;
            case "policy": await Sql("UPDATE cerberus.vault_access_session SET policy_revision=2 WHERE handle_verifier={0}", s.Owner.Verifier); break;
            case "generation": await Sql("UPDATE cerberus.vault_access_session SET revocation_generation=2 WHERE handle_verifier={0}", s.Owner.Verifier); break;
            case "expired": await Sql("UPDATE cerberus.vault_access_session SET expires_at=statement_timestamp()-interval '1 minute' WHERE handle_verifier={0}", s.Owner.Verifier); break;
            case "future": await Sql("UPDATE cerberus.vault_access_session SET issued_at=statement_timestamp()+interval '1 hour' WHERE handle_verifier={0}", s.Owner.Verifier); break;
            case "profileDeleted": case "profileTerminal":
                var profile = await s.Profile(fixture);
                await Sql("UPDATE cerberus.vault_access_session SET profile_id={1} WHERE handle_verifier={0}", s.Owner.Verifier, profile);
                if (mode == "profileDeleted") await Sql("UPDATE cerberus.profile SET deleted_at=statement_timestamp() WHERE id={0}", profile);
                else await Sql("INSERT INTO cerberus.terminal_erasure(resource_kind,resource_id,deleted_at) SELECT 'profile',public_id,statement_timestamp() FROM cerberus.profile WHERE id={0}", profile);
                break;
            case "stale": request = request with { Input = new(2) }; break;
            default: await Sql("UPDATE cerberus.software_grant SET revision={1} WHERE public_id={0}", s.Grant, mode == "zero" ? 0L : mode == "unsafe" ? ProtocolBinary.MaxInteger + 1 : ProtocolBinary.MaxInteger); break;
        }
        await Outcome(s, request, error);
    }

    [FunctionalFact]
    public async Task GivenSelectedProfileWithoutTargets_WhenRevoking_ThenAdministerAccountGrant()
    {
        using var s = await Setup(); var profile = await s.Profile(fixture);
        await Sql("UPDATE cerberus.vault_access_session SET profile_id={1} WHERE handle_verifier={0}", s.Owner.Verifier, profile);
        Assert.Null((await Store().RevokeAsync(Request(s), default)).Error);
    }

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenMalformedOpaqueKeys_WhenRevoking_ThenEraseWithoutParsing(bool empty)
    {
        using var s = await Setup(); var bytes = empty ? Array.Empty<byte>() : new byte[] { 255, 0, 1 };
        await Sql("UPDATE cerberus.software_grant SET recipient_key={1} WHERE public_id={0}", s.Grant, bytes);
        await Sql("UPDATE cerberus.software_grant_record SET recipient_key_envelope={1} WHERE account_id={0}", s.Owner.InternalId, bytes);
        await Sql("UPDATE cerberus.software_grant_collection SET recipient_key_envelope={1} WHERE account_id={0}", s.Owner.InternalId, bytes);
        Assert.Null((await Store().RevokeAsync(Request(s), default)).Error);
        await using var db = fixture.CreateContext(); Assert.Empty((await db.SoftwareGrants.SingleAsync(x => x.PublicId == s.Grant)).RecipientKey);
        Assert.False(await db.SoftwareGrantRecords.AnyAsync(x => x.AccountId == s.Owner.InternalId));
        Assert.False(await db.SoftwareGrantCollections.AnyAsync(x => x.AccountId == s.Owner.InternalId));
    }

    // Break: partial writes surviving faults, internal failures becoming conflicts, or retry remaining poisoned.
    public static IEnumerable<object[]> Faults() =>
        from stage in new[] { "read", "cas", "record", "collection", "post" }
        from failure in new[] { "database", "timeout", "cancel", "40001", "40P01" }
        select new object[] { stage, failure };

    [FunctionalTheory][MemberData(nameof(Faults))]
    public async Task GivenPersistenceFault_WhenRevoking_ThenRollbackAndAllowCleanRetry(string stage, string failure)
    {
        using var s = await Setup();
        await Outcome(s, Request(s), "persistence_unavailable", new ProfileSetup.Factory(fixture, new Fault(stage, Failure(failure))));
        Assert.Null((await Store().RevokeAsync(Request(s), default)).Error);
    }

    [FunctionalTheory][InlineData("cas")][InlineData("record")][InlineData("collection")]
    public async Task GivenIncorrectAffectedCount_WhenRevoking_ThenRollbackAndAllowCleanRetry(string stage)
    {
        using var s = await Setup();
        await Outcome(s, Request(s), "persistence_unavailable", new ProfileSetup.Factory(fixture, new WrongCount(stage)));
        Assert.Null((await Store().RevokeAsync(Request(s), default)).Error);
    }

    [FunctionalTheory][InlineData("key")][InlineData("identity")][InlineData("stamp")][InlineData("revision")]
    [InlineData("account")][InlineData("session")][InlineData("record")][InlineData("protection")]
    public async Task GivenPostWriteMutation_WhenRevoking_ThenRollbackFailedPostcondition(string kind)
    {
        using var s = await Setup();
        var hook = new Mutation(false, c => Execute(c, kind switch
        {
            "key" => "UPDATE cerberus.software_grant SET recipient_key='bad'::bytea WHERE public_id=@grant",
            "identity" => "UPDATE cerberus.software_grant SET heimdall_software_public_id=gen_random_uuid() WHERE public_id=@grant",
            "stamp" => "UPDATE cerberus.software_grant SET concurrency_stamp=gen_random_uuid() WHERE public_id=@grant",
            "revision" => "UPDATE cerberus.software_grant SET revision=revision+1 WHERE public_id=@grant",
            "account" => "UPDATE cerberus.account SET revision=revision+1 WHERE id=@owner",
            "session" => "UPDATE cerberus.vault_access_session SET revoked=true WHERE account_id=@owner",
            "record" => "UPDATE cerberus.record SET envelope='bad'::bytea WHERE account_id=@owner",
            _ => "UPDATE cerberus.vault_protection SET material='bad'::bytea WHERE account_id=@owner"
        }, new("grant", s.Grant), new("owner", s.Owner.InternalId)));
        await Outcome(s, Request(s), "persistence_unavailable", new ProfileSetup.Factory(fixture, hook));
        Assert.Null((await Store().RevokeAsync(Request(s), default)).Error);
    }

    [FunctionalTheory][InlineData("recordOwner")][InlineData("collectionOwner")][InlineData("recordTarget")][InlineData("collectionTarget")]
    [InlineData("wrongGrant")][InlineData("grantOwner")][InlineData("duplicate")]
    public async Task GivenRelationalCorruption_WhenRevoking_ThenRollbackWithoutErasingAmbiguousRows(string kind)
    {
        using var s = await Setup(); using var other = await Setup();
        var hook = new Mutation(true, async c =>
        {
            await Execute(c, "SET LOCAL session_replication_role=replica");
            if (kind == "duplicate")
            {
                await Execute(c, "DROP INDEX cerberus.ix_software_grant_public_id");
                await Execute(c, "UPDATE cerberus.software_grant SET public_id=@grant WHERE public_id=@other", new("grant", s.Grant), new("other", other.Grant));
            }
            else await Execute(c, kind switch
            {
                "recordOwner" => "UPDATE cerberus.software_grant_record SET account_id=@other WHERE account_id=@owner",
                "collectionOwner" => "UPDATE cerberus.software_grant_collection SET account_id=@other WHERE account_id=@owner",
                "recordTarget" => "UPDATE cerberus.software_grant_record SET record_id=@target WHERE account_id=@owner",
                "collectionTarget" => "UPDATE cerberus.software_grant_collection SET collection_id=@target WHERE account_id=@owner",
                "wrongGrant" => "UPDATE cerberus.software_grant_record SET grant_id=(SELECT id FROM cerberus.software_grant WHERE public_id=@otherGrant) WHERE account_id=@owner",
                _ => "UPDATE cerberus.software_grant SET account_id=@other WHERE public_id=@grant"
            }, new("owner", s.Owner.InternalId), new("other", other.Owner.InternalId), new("grant", s.Grant),
                new("otherGrant", other.Grant),
                new("target", kind == "recordTarget" ? other.Items.Record.Id : other.Items.Collection.Id));
        });
        // Changing the grant's owner makes it foreign before the first snapshot; children cannot grant authority.
        await Outcome(s, Request(s), kind == "grantOwner" ? "not_found" : "persistence_unavailable", new ProfileSetup.Factory(fixture, hook));
        Assert.Null((await Store().RevokeAsync(Request(s), default)).Error);
    }

    [FunctionalTheory][InlineData("actor", "authentication_required")][InlineData("access", "vault_access_required")]
    [InlineData("grant", "validation_failed")][InlineData("input", "validation_failed")][InlineData("revision", "validation_failed")]
    [InlineData("null", "validation_failed")]
    public async Task GivenInvalidRequest_WhenRevoking_ThenRejectBeforeOpeningDatabase(string mode, string error)
    {
        var request = new SoftwareGrantRevocationRequest(mode == "actor" ? Guid.Empty : Guid.NewGuid(), mode == "access" ? " " : "verifier",
            mode == "grant" ? Guid.Empty : Guid.NewGuid(), mode == "input" ? null! : new(mode == "revision" ? ProtocolBinary.MaxInteger : 1));
        Assert.Equal(error, (await Store(new ThrowingFactory(new Exception("must not open"))).RevokeAsync(mode == "null" ? null! : request, default)).Error);
    }

    [FunctionalTheory][InlineData("json")][InlineData("format")][InlineData("wrapped")][InlineData("update")][InlineData("23505")]
    public async Task GivenOtherInternalFailure_WhenRevoking_ThenReturnSafeError(string failure)
    {
        using var s = await Setup(); await Outcome(s, Request(s), "persistence_unavailable", new ThrowingFactory(Failure(failure)));
    }

    [FunctionalTheory][InlineData("before")][InlineData("cas")][InlineData("record")][InlineData("collection")][InlineData("post")]
    public async Task GivenCallerCancellation_WhenRevoking_ThenPropagateAndRollback(string stage)
    {
        using var s = await Setup(); using var ct = new CancellationTokenSource();
        var before = await SoftwareGrantSetup.Aggregate(fixture, s.Grant); var vault = await SoftwareGrantSetup.Vault(fixture, s.Owner.InternalId);
        if (stage == "before") ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store(new ProfileSetup.Factory(fixture,
            new Fault(stage, new OperationCanceledException(ct.Token), ct))).RevokeAsync(Request(s), ct.Token));
        Assert.Equal(before, await SoftwareGrantSetup.Aggregate(fixture, s.Grant));
        Assert.Equal(vault, await SoftwareGrantSetup.Vault(fixture, s.Owner.InternalId));
        Assert.Null((await Store().RevokeAsync(Request(s), default)).Error);
    }

    private async Task<SoftwareGrantSetup> Setup()
    {
        var s = await SoftwareGrantSetup.Create(fixture);
        Assert.Null((await new SoftwareGrantStore(fixture).GrantAsync(s.Request(), default)).Error); return s;
    }
    private async Task Outcome(SoftwareGrantSetup s, SoftwareGrantRevocationRequest request, string error, IDbContextFactory<AppDbContext>? factory = null)
    {
        var before = await SoftwareGrantSetup.Aggregate(fixture, s.Grant); var vault = await SoftwareGrantSetup.Vault(fixture, s.Owner.InternalId);
        var result = await Store(factory).RevokeAsync(request, default); Assert.Equal(error, result.Error); Assert.Null(result.Data);
        Assert.Equal(before, await SoftwareGrantSetup.Aggregate(fixture, s.Grant));
        Assert.Equal(vault, await SoftwareGrantSetup.Vault(fixture, s.Owner.InternalId));
    }
    private async Task Sql(string sql, params object[] values) { await using var db = fixture.CreateContext(); await db.Database.ExecuteSqlRawAsync(sql, values); }
    private Task Terminal(string kind, Guid id) => Sql("INSERT INTO cerberus.terminal_erasure(resource_kind,resource_id,deleted_at) VALUES({0},{1},statement_timestamp())", kind, id);
    private static Exception Failure(string kind) => kind switch
    {
        "database" => new NpgsqlException("private evidence"), "timeout" => new TimeoutException("private evidence"),
        "cancel" => new OperationCanceledException("private evidence"), "json" => new JsonException("private evidence"),
        "format" => new FormatException("private evidence"), "update" => new DbUpdateException("private evidence"),
        "wrapped" => new InvalidOperationException("private evidence", new TimeoutException("private evidence")),
        _ => new PostgresException("private evidence", "ERROR", "ERROR", kind)
    };
    private static bool Matches(DbCommand c, string stage) => stage switch
    {
        "cas" => c.CommandText.Contains("UPDATE cerberus.software_grant AS g"),
        "record" or "collection" => c.CommandText.StartsWith("DELETE FROM cerberus.software_grant_" + stage), _ => false
    };
    private sealed class Fault(string stage, Exception failure, CancellationTokenSource? caller = null) : DbCommandInterceptor
    {
        private bool written;
        private void Fail() { caller?.Cancel(); throw failure; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<int> r, CancellationToken ct = default)
        { if (Matches(c, stage)) Fail(); return ValueTask.FromResult(r); }
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand c, CommandExecutedEventData d, int r, CancellationToken ct = default)
        { if (Matches(c, "cas")) written = true; return ValueTask.FromResult(r); }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<DbDataReader> r, CancellationToken ct = default)
        { if (stage == "read" || stage == "post" && written) Fail(); return ValueTask.FromResult(r); }
    }
    private sealed class WrongCount(string stage) : DbCommandInterceptor
    {
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand c, CommandExecutedEventData d, int r, CancellationToken ct = default)
            => ValueTask.FromResult(Matches(c, stage) ? r + 1 : r);
    }
    private sealed class Mutation(bool initial, Func<DbCommand, Task> action) : DbCommandInterceptor
    {
        private bool used;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<DbDataReader> r, CancellationToken ct = default)
        { if (initial && !used) { used = true; await action(c); } return r; }
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand c, CommandExecutedEventData d, int r, CancellationToken ct = default)
        { if (!initial && !used && Matches(c, "collection")) { used = true; await action(c); } return r; }
    }
    private static async Task Execute(DbCommand c, string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = c.Connection!.CreateCommand(); command.Transaction = c.Transaction; command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.Add(parameter); await command.ExecuteNonQueryAsync();
    }
    private sealed class ThrowingFactory(Exception failure) : IDbContextFactory<AppDbContext>
    { public AppDbContext CreateDbContext() => throw failure; }
    private SoftwareGrantRevocationStore Store(IDbContextFactory<AppDbContext>? factory = null) => new(factory ?? fixture);
    private static SoftwareGrantRevocationRequest Request(SoftwareGrantSetup s) => new(s.Owner.Actor, s.Owner.Verifier, s.Grant, new(1));
}
