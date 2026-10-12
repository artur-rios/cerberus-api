using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using ArturRios.Cerberus.Data.Collections;
using ArturRios.Cerberus.Data.Protection;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public partial class CollectionGrantChangeStoreTests(PostgresFixture fixture)
{
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenActiveGrant_WhenChangingEitherDirection_ThenOnlyGrantAndRecipientGenerationChange(bool write)
    {
        using var s = await Setup(write);
        await Members(s);
        var other = await AssociationSetup.Items(fixture, s.Owner);
        await AssociationSetup.Grant(fixture, s.Owner, s.OwnerKey, s.Recipient, s.RecipientKey, other.Collection);
        var before = await Stable(s); var all = await Grants(s);
        var input = s.Input; var result = await Change(s, input);
        Assert.Null(result.Error); Assert.NotNull(result.Data);
        Assert.Equal(s.Items.Collection.PublicId, result.Data.CollectionId); Assert.Equal(s.Grant.PublicId, result.Data.GrantId);
        Assert.Equal(2, result.Data.Revision); Assert.Equal(write ? "read-only" : "read-write", result.Data.Access);
        Assert.Equal(new[] { s.Items.Record.PublicId }, result.Data.RecordIds); Assert.Equal(new[] { s.Items.Folder.PublicId }, result.Data.FolderIds);
        Assert.Equal(before, await Stable(s)); Assert.NotEqual(all, await Grants(s));
        await using var db = fixture.CreateContext(); var row = await db.CollectionGrants.SingleAsync(x => x.Id == s.Grant.Id);
        Assert.Equal(s.Grant.PublicId, row.PublicId); Assert.Equal(s.Grant.CollectionId, row.CollectionId); Assert.Equal(s.Recipient.InternalId, row.RecipientAccountId);
        Assert.Equal(CollectionGrantState.Active, row.State); Assert.Equal(2, row.Revision);
        Assert.Equal(write ? CollectionGrantAccess.ReadOnly : CollectionGrantAccess.ReadWrite, row.Access);
        Assert.Equal(Bytes(input.KeyEnvelope), row.RecipientKeyEnvelope); Assert.NotEqual(s.Grant.ConcurrencyStamp, row.ConcurrencyStamp);
        Assert.Equal(2, await Generation(s));
        Assert.Equal("vault_access_denied", (await new CollectionReadStore(fixture).ReadAsync(new(s.Recipient.Actor, s.Recipient.Verifier, s.Items.Collection.PublicId), default)).Error);
        var fresh = await Unlock(s.Recipient, s.RecipientKey);
        Assert.Null((await new CollectionReadStore(fixture).ReadAsync(new(s.Recipient.Actor, fresh, s.Items.Collection.PublicId), default)).Error);
    }

    [FunctionalTheory][InlineData("Master", true)][InlineData("Master", false)][InlineData("PerProfile", true)][InlineData("PerProfile", false)]
    public async Task GivenSelectedOwner_WhenChanging_ThenRequireExactCurrentLink(string mode, bool linked)
    {
        using var s = await Setup(); using var scoped = new ProtectionFixture();
        var input = ProfileSetup.Input(s.Owner, s.OwnerKey, scoped, mode) with { CollectionIds = linked ? [s.Items.Collection.PublicId] : [] };
        Assert.Null((await new ArturRios.Cerberus.Data.Profiles.ProfileCreateStore(fixture).CreateAsync(new(s.Owner.Actor, s.Owner.Verifier, input), default)).Error);
        await using var db = fixture.CreateContext(); var profile = await db.Profiles.SingleAsync(x => x.PublicId == input.ProfileId);
        s.Owner = s.Owner with { Verifier = await Selected(s.Owner, profile.Id) };
        await Outcome(s, linked ? null : "not_found");
    }

    [FunctionalTheory]
    [InlineData("missing", "not_found")][InlineData("revoked", "not_found")][InlineData("terminal", "not_found")][InlineData("crossCollection", "not_found")]
    [InlineData("recipientClosing", "not_found")][InlineData("recipientTerminal", "not_found")][InlineData("recipientUnprotected", "not_found")]
    [InlineData("ownerClosing", "not_found")][InlineData("ownerTerminal", "not_found")][InlineData("collectionTrash", "not_found")][InlineData("collectionTerminal", "not_found")]
    [InlineData("sessionRevoked", "vault_access_denied")][InlineData("sessionExpired", "vault_access_denied")][InlineData("sessionFuture", "vault_access_denied")]
    [InlineData("sessionPolicy", "vault_access_denied")][InlineData("sessionGeneration", "vault_access_denied")]
    [InlineData("stale", "revision_conflict")][InlineData("noop", "revision_conflict")][InlineData("exhausted", "revision_conflict")][InlineData("generationExhausted", "revision_conflict")]
    public async Task GivenHiddenOrConflictingState_WhenChanging_ThenRejectAndPreserveEveryRow(string state, string error)
    {
        using var s = await Setup();
        if (state == "missing") s.PathGrant = Guid.NewGuid();
        if (state == "revoked") await Sql("UPDATE cerberus.collection_grant SET state=1 WHERE id={0}", s.Grant.Id);
        if (state == "terminal") await Terminal("grant", s.Grant.PublicId);
        if (state == "crossCollection") { var other = await AssociationSetup.Items(fixture, s.Owner); s.PathCollection = other.Collection.PublicId; }
        if (state.EndsWith("Closing")) await Sql("UPDATE cerberus.account SET state={0} WHERE id={1}", (int)AccountState.ClosurePending, state.StartsWith("owner") ? s.Owner.InternalId : s.Recipient.InternalId);
        if (state is "ownerTerminal" or "recipientTerminal") await Terminal("account", state.StartsWith("owner") ? s.Owner.AccountId : s.Recipient.AccountId);
        if (state == "recipientUnprotected") await Sql("DELETE FROM cerberus.vault_protection WHERE account_id={0}", s.Recipient.InternalId);
        if (state == "collectionTrash") await Sql("UPDATE cerberus.collection SET deleted_at=statement_timestamp() WHERE id={0}", s.Items.Collection.Id);
        if (state == "collectionTerminal") await Terminal("collection", s.Items.Collection.PublicId);
        if (state.StartsWith("session")) await Sql("UPDATE cerberus.vault_access_session SET " + (state switch { "sessionRevoked" => "revoked=true", "sessionExpired" => "expires_at=statement_timestamp()", "sessionFuture" => "issued_at=statement_timestamp()+interval '1 hour'", "sessionPolicy" => "policy_revision=2", _ => "revocation_generation=2" }) + " WHERE handle_verifier={0}", s.Owner.Verifier);
        if (state == "stale") s.Expected = 2;
        if (state == "noop") s.Access = "read-only";
        if (state == "exhausted") await Rebind(s, ProtocolBinary.MaxInteger);
        if (state == "generationExhausted") await Sql("UPDATE cerberus.account SET revocation_generation={0} WHERE id={1}", ProtocolBinary.MaxInteger, s.Recipient.InternalId);
        await Outcome(s, error);
    }

    [FunctionalTheory]
    [InlineData("ownerPins")][InlineData("ownerMissing")][InlineData("recipientPins")][InlineData("recipientEpoch")][InlineData("recipientProtectionRevision")]
    [InlineData("ownerProtectionGeneration")][InlineData("collectionEnvelope")][InlineData("collectionEpoch")][InlineData("collectionRevision")][InlineData("collectionSequence")][InlineData("collectionPurge")]
    [InlineData("grantEnvelope")][InlineData("grantAccess")][InlineData("grantZero")][InlineData("grantUnsafe")][InlineData("generationZero")][InlineData("generationUnsafe")]
    [InlineData("storedSignature")][InlineData("storedOwner")][InlineData("storedRecipient")][InlineData("storedEpoch")][InlineData("storedRevision")][InlineData("storedGrant")]
    public async Task GivenRequiredStoredCorruption_WhenChanging_Then503WithoutRepair(string state)
    {
        using var s = await Setup();
        if (state == "ownerMissing") await Sql("DELETE FROM cerberus.vault_protection WHERE account_id={0}", s.Owner.InternalId);
        else if (state is "ownerPins" or "recipientPins" or "recipientEpoch" or "recipientProtectionRevision" or "ownerProtectionGeneration")
            await Sql("UPDATE cerberus.vault_protection SET " + (state.EndsWith("Pins") ? "material='bad'::bytea" : state.EndsWith("Epoch") ? "key_epoch=2" : state.EndsWith("Revision") ? "revision=0" : "recovery_generation=2") + " WHERE account_id={0}", state.StartsWith("owner") ? s.Owner.InternalId : s.Recipient.InternalId);
        else if (state.StartsWith("collection")) await Sql("UPDATE cerberus.collection SET " + (state switch { "collectionEnvelope" => "envelope='bad'::bytea", "collectionEpoch" => "key_epoch=2", "collectionRevision" => "revision=0", "collectionSequence" => "server_sequence=0", _ => "purge_at=statement_timestamp()" }) + " WHERE id={0}", s.Items.Collection.Id);
        else if (state.StartsWith("generation")) await Sql("UPDATE cerberus.account SET revocation_generation={0} WHERE id={1}", state == "generationZero" ? 0L : ProtocolBinary.MaxInteger + 1, s.Recipient.InternalId);
        else if (state.StartsWith("stored"))
        {
            var e = s.OwnerKey.WrapGrant(state == "storedOwner" ? Guid.NewGuid() : s.Owner.AccountId, s.Items.Collection.PublicId, state == "storedGrant" ? Guid.NewGuid() : s.Grant.PublicId,
                state == "storedRecipient" ? Guid.NewGuid() : s.Recipient.Actor, s.RecipientKey.Material.RecipientKey, state == "storedEpoch" ? 2 : 1, state == "storedRevision" ? 2 : 1);
            if (state == "storedSignature") e = e with { Signature = ProtectionFixture.Encode(new byte[64]) };
            await Sql("UPDATE cerberus.collection_grant SET recipient_key_envelope={0} WHERE id={1}", Bytes(e), s.Grant.Id);
        }
        else await Sql("UPDATE cerberus.collection_grant SET " + (state switch { "grantEnvelope" => "recipient_key_envelope='bad'::bytea", "grantAccess" => "access=99", "grantZero" => "revision=0", _ => "revision=9007199254740992" }) + " WHERE id={0}", s.Grant.Id);
        await Outcome(s, "persistence_unavailable");
    }

    [FunctionalTheory][InlineData("owner")][InlineData("collection")][InlineData("epoch")][InlineData("identity")][InlineData("recipientPin")][InlineData("authorPin")][InlineData("signature")]
    public async Task GivenShapeValidCallerBindingFailure_WhenChanging_Then400WithoutMutation(string state)
    {
        using var s = await Setup(); using var other = new ProtectionFixture();
        var e = s.OwnerKey.WrapGrant(state == "owner" ? Guid.NewGuid() : s.Owner.AccountId, state == "collection" ? Guid.NewGuid() : s.PathCollection,
            s.PathGrant, state == "identity" ? Guid.NewGuid() : s.Recipient.Actor, state == "recipientPin" ? other.Material.RecipientKey : s.RecipientKey.Material.RecipientKey, state == "epoch" ? 2 : 1, 2);
        if (state == "authorPin") e = other.WrapGrant(s.Owner.AccountId, s.PathCollection, s.PathGrant, s.Recipient.Actor, s.RecipientKey.Material.RecipientKey, 1, 2);
        if (state == "signature") e = e with { Signature = ProtectionFixture.Encode(new byte[64]) };
        Assert.True(e.IsValid()); await Outcome(s, "validation_failed", s.Input with { KeyEnvelope = e });
    }

    [FunctionalTheory][InlineData("visible", "vault_access_denied")][InlineData("write", "vault_access_denied")][InlineData("corrupt", "persistence_unavailable")]
    [InlineData("hidden", "not_found")][InlineData("revoked", "not_found")][InlineData("terminal", "not_found")][InlineData("unselected", "not_found")]
    public async Task GivenForeignTarget_WhenChanging_ThenClassifyBeforePathGrantAndAcquireNoLocks(string state, string error)
    {
        using var s = await Setup(state == "write");
        if (state is "corrupt" or "hidden") await Sql("UPDATE cerberus.collection_grant SET recipient_key_envelope='bad'::bytea WHERE id={0}", s.Grant.Id);
        if (state is "hidden" or "revoked") await Sql("UPDATE cerberus.collection_grant SET state=1 WHERE id={0}", s.Grant.Id);
        if (state == "terminal") await Terminal("grant", s.Grant.PublicId);
        var actor = s.Recipient;
        if (state == "unselected") actor = actor with { Verifier = await Selected(actor, await AssociationSetup.Profile(fixture, actor, s.RecipientKey)) };
        var capture = new Capture(); var before = await All(s);
        var result = await Store(new ProfileSetup.Factory(fixture, capture)).ChangeAsync(new(actor.Actor, actor.Verifier, s.PathCollection, Guid.NewGuid(), s.Input), default);
        // The envelope grant id must agree structurally with the path, even for a denied request.
        Assert.Equal("validation_failed", result.Error);
        var path = Guid.NewGuid(); var input = s.Input with { KeyEnvelope = s.OwnerKey.WrapGrant(s.Owner.AccountId, s.PathCollection, path, s.Recipient.Actor, s.RecipientKey.Material.RecipientKey, 1, 2) };
        result = await Store(new ProfileSetup.Factory(fixture, capture)).ChangeAsync(new(actor.Actor, actor.Verifier, s.PathCollection, path, input), default);
        Assert.Equal(error, result.Error); Assert.Empty(capture.Locks); Assert.Equal(before, await All(s));
    }

    [FunctionalTheory][InlineData("active")][InlineData("trash")][InlineData("terminal")][InlineData("cycle")][InlineData("hiddenCycle")][InlineData("missing")][InlineData("foreign")]
    public async Task GivenDirectMembersAndAncestry_WhenChanging_ThenProjectOnlyCompleteActiveRoots(string state)
    {
        using var s = await Setup(); await Members(s); var parent = await AssociationSetup.Items(fixture, s.Owner);
        await Sql("UPDATE cerberus.record SET folder_id={0} WHERE id={1}", parent.Folder.Id, s.Items.Record.Id);
        await Sql("UPDATE cerberus.folder SET parent_folder_id={0} WHERE id={1}", parent.Folder.Id, s.Items.Folder.Id);
        if (state is "trash" or "hiddenCycle") await Sql("UPDATE cerberus.folder SET deleted_at=statement_timestamp() WHERE id={0}", parent.Folder.Id);
        if (state == "terminal") await Terminal("folder", parent.Folder.PublicId);
        if (state is "cycle" or "hiddenCycle") await Sql("UPDATE cerberus.folder SET parent_folder_id=id WHERE id={0}", parent.Folder.Id);
        if (state is "missing" or "foreign")
        {
            var other = await AssociationSetup.Items(fixture, s.Recipient); await using var db = fixture.CreateContext(); await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("SET LOCAL session_replication_role=replica");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.folder SET parent_folder_id={(state == "missing" ? long.MaxValue : other.Folder.Id)} WHERE id={parent.Folder.Id}"); await tx.CommitAsync();
        }
        try
        {
            var before = await All(s); var result = await Change(s); Assert.Equal(state == "cycle" ? "persistence_unavailable" : null, result.Error);
            if (result.Data is { } d) { Assert.Equal(state == "active" ? [s.Items.Record.PublicId] : Array.Empty<Guid>(), d.RecordIds); Assert.Equal(state == "active" ? [s.Items.Folder.PublicId] : Array.Empty<Guid>(), d.FolderIds); }
            else Assert.Equal(before, await All(s));
        }
        finally { if (state is "missing" or "foreign") await Sql("UPDATE cerberus.folder SET parent_folder_id=NULL WHERE id={0}", parent.Folder.Id); }
    }

    [FunctionalTheory][InlineData("failure")][InlineData("cancel")][InlineData("grant")][InlineData("generation")][InlineData("account")][InlineData("collection")][InlineData("roots")][InlineData("pins")]
    public async Task GivenPostWriteFault_WhenChanging_ThenRollbackBothMutationsAndRetry(string stage)
    {
        using var s = await Setup(); await Members(s); var before = await All(s); using var ct = new CancellationTokenSource();
        var hook = new After(Write, async c =>
        {
            if (stage == "failure") throw new TimeoutException("controlled post-write failure");
            if (stage == "cancel") { ct.Cancel(); ct.Token.ThrowIfCancellationRequested(); }
            await Execute(c, stage switch
            {
                "grant" => "UPDATE cerberus.collection_grant SET revision=revision+1 WHERE id=@grant",
                "generation" => "UPDATE cerberus.account SET revocation_generation=revocation_generation+1 WHERE id=@recipient",
                "account" => "UPDATE cerberus.account SET revision=revision+1 WHERE id=@recipient",
                "collection" => "UPDATE cerberus.collection SET revision=revision+1 WHERE id=@collection",
                "roots" => "DELETE FROM cerberus.collection_record WHERE collection_id=@collection",
                _ => "UPDATE cerberus.vault_protection SET revision=revision+1 WHERE account_id=@recipient"
            }, new("grant", s.Grant.Id), new("recipient", s.Recipient.InternalId), new("collection", s.Items.Collection.Id));
        });
        if (stage == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Change(s, factory: new ProfileSetup.Factory(fixture, hook), ct: ct.Token));
        else Assert.Equal("persistence_unavailable", (await Change(s, factory: new ProfileSetup.Factory(fixture, hook))).Error);
        Assert.Equal(before, await All(s)); Assert.Null((await Change(s)).Error);
    }

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenExpiryAtAuthorization_WhenChanging_ThenOnlyPostAuthorizationExpiryCanCommit(bool after)
    {
        using var s = await Setup(); var deadline = DateTimeOffset.UtcNow.AddSeconds(1.2);
        await Sql("UPDATE cerberus.vault_access_session SET expires_at={0} WHERE handle_verifier={1}", deadline, s.Owner.Verifier);
        async Task Pause(DbCommand _) { var wait = deadline - DateTimeOffset.UtcNow; if (wait > TimeSpan.Zero) await Task.Delay(wait + TimeSpan.FromMilliseconds(100)); }
        DbCommandInterceptor hook = after ? new After(Write, Pause) : new Before(Write, Pause);
        await Outcome(s, after ? null : "vault_access_denied", factory: new ProfileSetup.Factory(fixture, hook));
    }

    [FunctionalTheory][InlineData(false, false)][InlineData(false, true)][InlineData(true, false)][InlineData(true, true)]
    public async Task GivenFailureInsideEitherAtomicUpdate_WhenChanging_Then503AndRollbackBeforeCleanRetry(bool grant, bool suppress)
    {
        using var s = await Setup(); var before = await All(s); var name = "uc36_generation_" + Guid.NewGuid().ToString("N");
        var table = grant ? "collection_grant" : "account"; var column = grant ? "revision" : "revocation_generation";
        var install = "CREATE FUNCTION cerberus." + name + "() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN "
            + (suppress ? "RETURN NULL;" : "RAISE EXCEPTION 'controlled generation write failure';") + " END $$; CREATE TRIGGER " + name
            + " BEFORE UPDATE OF " + column + " ON cerberus." + table + " FOR EACH ROW WHEN (OLD.id=" + (grant ? s.Grant.Id : s.Recipient.InternalId)
            + ") EXECUTE FUNCTION cerberus." + name + "()";
        await Sql(install);
        try { Assert.Equal("persistence_unavailable", (await Change(s)).Error); Assert.Equal(before, await All(s)); }
        finally { await Sql("DROP TRIGGER " + name + " ON cerberus." + table + "; DROP FUNCTION cerberus." + name + "()"); }
        Assert.Equal(before, await All(s)); Assert.Null((await Change(s)).Error);
    }

    [FunctionalFact]
    public async Task GivenLastSafeCounters_WhenChanging_ThenAdvanceBothToMaximumExactlyOnce()
    {
        using var s = await Setup(); await Rebind(s, ProtocolBinary.MaxInteger - 1); s.Expected = ProtocolBinary.MaxInteger - 1;
        await Sql("UPDATE cerberus.account SET revocation_generation={0} WHERE id={1}", ProtocolBinary.MaxInteger - 1, s.Recipient.InternalId);
        var result = await Change(s); Assert.Null(result.Error); Assert.Equal(ProtocolBinary.MaxInteger, result.Data!.Revision); Assert.Equal(ProtocolBinary.MaxInteger, await Generation(s));
    }

    [FunctionalTheory][InlineData("policy", "vault_access_denied")][InlineData("recipient", "not_found")][InlineData("grant", "revision_conflict")][InlineData("generation", "revision_conflict")][InlineData("terminal", "not_found")]
    public async Task GivenChangeAtGuardedStatement_WhenChanging_ThenRejectAndRollback(string state, string error)
    {
        using var s = await Setup(); var before = await All(s);
        var hook = new Before(Write, c => Execute(c, state switch
        {
            "policy" => "UPDATE cerberus.account SET policy_revision=2 WHERE id=@owner",
            "recipient" => "UPDATE cerberus.account SET state=1 WHERE id=@recipient",
            "grant" => "UPDATE cerberus.collection_grant SET revision=revision+1 WHERE id=@grant",
            "generation" => "UPDATE cerberus.account SET revocation_generation=revocation_generation+1 WHERE id=@recipient",
            _ => "INSERT INTO cerberus.terminal_erasure(resource_kind,resource_id,deleted_at) VALUES('grant',@public,statement_timestamp())"
        }, new("owner", s.Owner.InternalId), new("recipient", s.Recipient.InternalId), new("grant", s.Grant.Id), new("public", s.Grant.PublicId)));
        Assert.Equal(error, (await Change(s, factory: new ProfileSetup.Factory(fixture, hook))).Error); Assert.Equal(before, await All(s));
        Assert.Null((await Change(s)).Error);
    }

    [FunctionalTheory][InlineData("actor", "authentication_required")][InlineData("handle", "vault_access_required")][InlineData("target", "validation_failed")][InlineData("grant", "validation_failed")][InlineData("input", "validation_failed")]
    public async Task GivenInvalidTrustedBoundary_WhenChanging_ThenRejectBeforeDatabase(string state, string error)
    {
        using var key = new ProtectionFixture(); var collection = Guid.NewGuid(); var grant = Guid.NewGuid();
        var input = new CollectionGrantChangeInput(1, "read-write", key.WrapGrant(Guid.NewGuid(), collection, grant, Guid.NewGuid(), key.Material.RecipientKey, 1, 2));
        var request = new CollectionGrantChangeRequest(state == "actor" ? Guid.Empty : Guid.NewGuid(), state == "handle" ? "" : "v", state == "target" ? Guid.Empty : collection, state == "grant" ? Guid.Empty : grant, state == "input" ? null! : input);
        Assert.Equal(error, (await Store(new ThrowingFactory(new Exception("must not persist"))).ChangeAsync(request, default)).Error);
    }

    [FunctionalTheory][InlineData("timeout")][InlineData("cancel")][InlineData("db")][InlineData("update")][InlineData("json")][InlineData("format")][InlineData("wrapped")][InlineData("unique")]
    public async Task GivenInternalFailure_WhenChanging_ThenReturnSafeError(string kind)
    {
        using var s = await Setup(); Exception exception = kind switch
        { "cancel" => new OperationCanceledException("private"), "db" => new NpgsqlException("private"), "update" => new DbUpdateException("private"), "json" => new JsonException("private"), "format" => new FormatException("private"), "wrapped" => new InvalidOperationException("private", new TimeoutException("private")), "unique" => new PostgresException("private", "ERROR", "ERROR", "23505"), _ => new TimeoutException("private") };
        await Outcome(s, kind == "unique" ? "revision_conflict" : "persistence_unavailable", factory: new ThrowingFactory(exception));
    }

    [FunctionalFact]
    public async Task GivenSelectedRecipientAndUnlinkedDescendants_WhenChanging_ThenInvalidateSelectionAndReturnSortedDirectRoots()
    {
        using var s = await Setup(); await Members(s); var other = await AssociationSetup.Items(fixture, s.Owner); var child = await AssociationSetup.Items(fixture, s.Owner);
        await Sql("INSERT INTO cerberus.collection_record(account_id,collection_id,record_id) VALUES({0},{1},{2})", s.Owner.InternalId, s.Items.Collection.Id, other.Record.Id);
        await Sql("INSERT INTO cerberus.collection_folder(account_id,collection_id,folder_id) VALUES({0},{1},{2})", s.Owner.InternalId, s.Items.Collection.Id, other.Folder.Id);
        await Sql("UPDATE cerberus.folder SET parent_folder_id={0} WHERE id={1}", s.Items.Folder.Id, child.Folder.Id);
        await Sql("UPDATE cerberus.record SET folder_id={0} WHERE id={1}", child.Folder.Id, child.Record.Id);
        var profile = await AssociationSetup.Profile(fixture, s.Recipient, s.RecipientKey); await Sql("INSERT INTO cerberus.profile_collection(profile_id,collection_id) VALUES({0},{1})", profile, s.Items.Collection.Id);
        var selected = await Selected(s.Recipient, profile);
        Assert.Null((await new CollectionReadStore(fixture).ReadAsync(new(s.Recipient.Actor, selected, s.PathCollection), default)).Error);
        var result = await Change(s); Assert.Null(result.Error);
        Assert.Equal(new[] { s.Items.Record.PublicId, other.Record.PublicId }.Order(), result.Data!.RecordIds); Assert.Equal(new[] { s.Items.Folder.PublicId, other.Folder.PublicId }.Order(), result.Data.FolderIds);
        Assert.Equal("vault_access_denied", (await new CollectionReadStore(fixture).ReadAsync(new(s.Recipient.Actor, selected, s.PathCollection), default)).Error);
    }

    [FunctionalTheory][InlineData(false, "revision", 0L)][InlineData(true, "revision", 9007199254740992L)]
    [InlineData(false, "policy_revision", 0L)][InlineData(false, "policy_revision", 9007199254740992L)]
    [InlineData(true, "policy_revision", 9007199254740992L)][InlineData(true, "revocation_generation", 9007199254740992L)]
    public async Task GivenUnsafePersistedAccountCounters_WhenChanging_Then503WithoutMutation(bool owner, string column, long value)
    {
        using var s = await Setup(); var id = owner ? s.Owner.InternalId : s.Recipient.InternalId;
        await Sql("UPDATE cerberus.account SET " + column + "={0} WHERE id={1}", value, id);
        if (owner && column != "revision") await Sql("UPDATE cerberus.vault_access_session SET " + column + "={0} WHERE account_id={1}", value, id);
        await Outcome(s, "persistence_unavailable");
    }

    private sealed class ThrowingFactory(Exception failure) : IDbContextFactory<AppDbContext>
    { public AppDbContext CreateDbContext() => throw failure; public Task<AppDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromException<AppDbContext>(failure); }

    private ICollectionGrantChangeStore Store(IDbContextFactory<AppDbContext>? factory = null) => new CollectionGrantChangeStore(factory ?? fixture);
    private Task<VaultResult<CollectionGrantChangeDetails>> Change(Scene s, CollectionGrantChangeInput? input = null, IDbContextFactory<AppDbContext>? factory = null, CancellationToken ct = default)
        => Store(factory).ChangeAsync(new(s.Owner.Actor, s.Owner.Verifier, s.PathCollection, s.PathGrant, input ?? s.Input), ct);
    private async Task Outcome(Scene s, string? error, CollectionGrantChangeInput? input = null, IDbContextFactory<AppDbContext>? factory = null)
    {
        var before = await All(s); var stable = await Stable(s); var generation = await Generation(s); var result = await Change(s, input, factory);
        Assert.Equal(error, result.Error);
        if (error is not null) { Assert.Null(result.Data); Assert.Equal(before, await All(s)); }
        else { Assert.Equal(stable, await Stable(s)); Assert.Equal(generation + 1, await Generation(s)); }
    }
    private sealed class Scene(ProtectionFixture ownerKey, ProtectionFixture recipientKey, ProfileSetup.State owner, ProfileSetup.State recipient, AssociationSetup.ResourceSet items, CollectionGrant grant) : IDisposable
    {
        public ProtectionFixture OwnerKey { get; } = ownerKey; public ProtectionFixture RecipientKey { get; } = recipientKey;
        public ProfileSetup.State Owner { get; set; } = owner; public ProfileSetup.State Recipient { get; } = recipient;
        public AssociationSetup.ResourceSet Items { get; } = items; public CollectionGrant Grant { get; } = grant;
        public Guid PathCollection { get; set; } = items.Collection.PublicId; public Guid PathGrant { get; set; } = grant.PublicId;
        public long Expected { get; set; } = 1; public string Access { get; set; } = grant.Access == CollectionGrantAccess.ReadOnly ? "read-write" : "read-only";
        public CollectionGrantChangeInput Input => new(Expected, Access, OwnerKey.WrapGrant(Owner.AccountId, PathCollection, PathGrant, Recipient.Actor, RecipientKey.Material.RecipientKey, 1, Expected + 1));
        public void Dispose() { OwnerKey.Dispose(); RecipientKey.Dispose(); }
    }
    private async Task<Scene> Setup(bool write = false)
    {
        var owner = new ProtectionFixture(); var recipient = new ProtectionFixture(); var a = await ProfileSetup.Create(fixture, owner); var b = await ProfileSetup.Create(fixture, recipient);
        var items = await AssociationSetup.Items(fixture, a); var grant = await AssociationSetup.Grant(fixture, a, owner, b, recipient, items.Collection, write ? CollectionGrantAccess.ReadWrite : CollectionGrantAccess.ReadOnly);
        return new(owner, recipient, a, b, items, grant);
    }
    private async Task Rebind(Scene s, long revision) => await Sql("UPDATE cerberus.collection_grant SET revision={0},recipient_key_envelope={1} WHERE id={2}", revision, Bytes(s.OwnerKey.WrapGrant(s.Owner.AccountId, s.PathCollection, s.PathGrant, s.Recipient.Actor, s.RecipientKey.Material.RecipientKey, 1, revision)), s.Grant.Id);
    private async Task<long> Generation(Scene s) { await using var db = fixture.CreateContext(); return await db.Accounts.Where(x => x.Id == s.Recipient.InternalId).Select(x => x.RevocationGeneration).SingleAsync(); }
    private async Task<string> All(Scene s) => (await Stable(s)) + (await Grants(s)) + (await Generation(s));
    private async Task<string> Grants(Scene s) { await using var db = fixture.CreateContext(); return await db.Database.SqlQuery<string>($"SELECT COALESCE(jsonb_agg(to_jsonb(g) ORDER BY id),'[]'::jsonb)::text AS \"Value\" FROM cerberus.collection_grant g WHERE collection_id={s.Items.Collection.Id}").SingleAsync(); }
    private async Task<string> Stable(Scene s)
    {
        await using var db = fixture.CreateContext();
        return await db.Database.SqlQuery<string>($"""
            SELECT jsonb_build_object(
              'accounts',(SELECT jsonb_agg(CASE WHEN id={s.Recipient.InternalId} THEN to_jsonb(a)-'revocation_generation' ELSE to_jsonb(a) END ORDER BY id) FROM cerberus.account a WHERE id IN ({s.Owner.InternalId},{s.Recipient.InternalId})),
              'protection',(SELECT jsonb_agg(to_jsonb(p) ORDER BY id) FROM cerberus.vault_protection p WHERE account_id IN ({s.Owner.InternalId},{s.Recipient.InternalId})),
              'sessions',(SELECT jsonb_agg(to_jsonb(v) ORDER BY id) FROM cerberus.vault_access_session v WHERE account_id IN ({s.Owner.InternalId},{s.Recipient.InternalId})),
              'profiles',(SELECT jsonb_agg(to_jsonb(p) ORDER BY id) FROM cerberus.profile p WHERE account_id IN ({s.Owner.InternalId},{s.Recipient.InternalId})),
              'collections',(SELECT jsonb_agg(to_jsonb(c) ORDER BY id) FROM cerberus.collection c WHERE account_id IN ({s.Owner.InternalId},{s.Recipient.InternalId})),
              'records',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM cerberus.record r WHERE account_id IN ({s.Owner.InternalId},{s.Recipient.InternalId})),
              'folders',(SELECT jsonb_agg(to_jsonb(f) ORDER BY id) FROM cerberus.folder f WHERE account_id IN ({s.Owner.InternalId},{s.Recipient.InternalId})),
              'otherGrants',(SELECT jsonb_agg(to_jsonb(g) ORDER BY g.id) FROM cerberus.collection_grant g JOIN cerberus.collection c ON c.id=g.collection_id WHERE c.account_id IN ({s.Owner.InternalId},{s.Recipient.InternalId}) AND g.id<>{s.Grant.Id}),
              'pc',(SELECT jsonb_agg(to_jsonb(pc) ORDER BY profile_id) FROM cerberus.profile_collection pc WHERE collection_id={s.Items.Collection.Id}),
              'cf',(SELECT jsonb_agg(to_jsonb(cf) ORDER BY folder_id) FROM cerberus.collection_folder cf WHERE collection_id={s.Items.Collection.Id}),
              'cr',(SELECT jsonb_agg(to_jsonb(cr) ORDER BY record_id) FROM cerberus.collection_record cr WHERE collection_id={s.Items.Collection.Id}))::text AS "Value"
            """).SingleAsync();
    }
    private async Task Members(Scene s) { await Sql("INSERT INTO cerberus.collection_record(account_id,collection_id,record_id) VALUES({0},{1},{2})", s.Owner.InternalId, s.Items.Collection.Id, s.Items.Record.Id); await Sql("INSERT INTO cerberus.collection_folder(account_id,collection_id,folder_id) VALUES({0},{1},{2})", s.Owner.InternalId, s.Items.Collection.Id, s.Items.Folder.Id); }
    private async Task Terminal(string kind, Guid id) => await Sql("INSERT INTO cerberus.terminal_erasure(resource_kind,resource_id,deleted_at) VALUES({0},{1},statement_timestamp())", kind, id);
    private async Task Sql(string sql, params object[] values) { await using var db = fixture.CreateContext(); await db.Database.ExecuteSqlRawAsync(sql, values); }
    private static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, ProtectionFixture.Json);
    private async Task<string> Selected(ProfileSetup.State s, long profile)
    {
        var verifier = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)); await using var db = fixture.CreateContext();
        db.VaultAccessSessions.Add(new() { AccountId = s.InternalId, ProfileId = profile, HandleVerifier = verifier, IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), PolicyRevision = 1, RevocationGeneration = 1 }); await db.SaveChangesAsync(); return verifier;
    }
    private async Task<string> Unlock(ProfileSetup.State s, ProtectionFixture key)
    {
        var store = new VaultProtectionStore(fixture); var raw = "{}"u8.ToArray();
        var challenge = (await store.ChallengeAsync(s.Actor, ProtocolBinary.Encode(SHA256.HashData(raw)), default)).Data!;
        var result = await store.UnlockAsync(s.Actor, challenge.ProtectionRevision, challenge.ChallengeId, key.Sign(challenge), raw, default); Assert.Null(result.Error);
        Assert.True(OpaqueAccessHandle.TryHash(result.Data!.Access, out var verifier)); return verifier;
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static bool Lock(DbCommand c, string table) => c.CommandText.Contains("FROM cerberus." + table + " ") && (c.CommandText.Contains("FOR UPDATE") || c.CommandText.Contains("FOR NO KEY UPDATE"));
    private static bool Write(DbCommand c) => c.CommandText.Contains("UPDATE cerberus.collection_grant AS g");
    private static async Task Execute(DbCommand c, string sql, params NpgsqlParameter[] parameters)
    { await using var command = c.Connection!.CreateCommand(); command.Transaction = c.Transaction; command.CommandText = sql; foreach (var p in parameters) command.Parameters.Add(p); await command.ExecuteNonQueryAsync(); }
    private sealed class Before(Func<DbCommand, bool> match, Func<DbCommand, Task> action) : DbCommandInterceptor
    {
        private bool used;
        private async Task Check(DbCommand c) { if (!used && match(c)) { used = true; await action(c); } }
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<int> r, CancellationToken ct = default) { await Check(c); return r; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<DbDataReader> r, CancellationToken ct = default) { await Check(c); return r; }
    }
    private sealed class After(Func<DbCommand, bool> match, Func<DbCommand, Task> action) : DbCommandInterceptor
    { private bool used; public override async ValueTask<int> NonQueryExecutedAsync(DbCommand c, CommandExecutedEventData d, int r, CancellationToken ct = default) { if (!used && match(c)) { used = true; await action(c); } return r; } }
    private sealed class Capture : DbCommandInterceptor
    {
        public List<string> Locks { get; } = []; public List<object[]> Values { get; } = []; public int Writes { get; private set; }
        public List<string> Reads { get; } = [];
        public string Snapshot { get; private set; } = ""; public NpgsqlParameter[] Parameters { get; private set; } = [];
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<int> r, CancellationToken ct = default)
        { if (c.CommandText.Contains("FOR UPDATE") || c.CommandText.Contains("FOR NO KEY UPDATE")) { Locks.Add(c.CommandText); Values.Add(c.Parameters.Cast<DbParameter>().Select(x => x.Value!).ToArray()); } if (Write(c)) Writes++; return ValueTask.FromResult(r); }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<DbDataReader> r, CancellationToken ct = default)
        { Reads.Add(c.CommandText); if (Snapshot.Length == 0 && c.CommandText.StartsWith("WITH RECURSIVE")) { Snapshot = c.CommandText; Parameters = c.Parameters.Cast<NpgsqlParameter>().Select(x => x.Clone()).ToArray(); } return ValueTask.FromResult(r); }
    }
}
